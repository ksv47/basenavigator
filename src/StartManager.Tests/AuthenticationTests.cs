using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class AuthenticationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-authentication-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "Settings", "settings.json");
    private string Client => Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
    private static readonly Guid Base = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Library Lib = new();
    // The local list of the profile: the list of these tests' passwords.
    private string Source => ActivityStore.Source(Settings, "");
    public AuthenticationTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Client, "fixture");
    }
    public void Dispose() => Directory.Delete(root, true);

    private static Dictionary<PasswordRole, string?> Passwords(string? enterprise = null, string? designer = null, string? repository = null) => new()
    { [PasswordRole.Enterprise] = enterprise, [PasswordRole.Designer] = designer, [PasswordRole.Repository] = repository };

    [Fact] public void VaultKeepsPasswordsSealedAndPerBase()
    {
        var vault = new PasswordVault(Settings);
        Assert.Empty(vault.Read(Lib, Source, Base).Passwords);
        vault.Write(Source, Base, Passwords("Пароль 1", "cfg \"quoted\"", "repo"));
        vault.Write(Source, Other, Passwords("other"));
        var read = new PasswordVault(Settings).Read(Lib, Source, Base).Passwords;
        Assert.Equal(("Пароль 1", "cfg \"quoted\"", "repo"), (read[PasswordRole.Enterprise], read[PasswordRole.Designer], read[PasswordRole.Repository]));
        // Neither file holds the text of a password.
        foreach (var file in new[] { vault.PathName, vault.KeyPath })
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var secret in new[] { "Пароль 1", "repo", "other" })
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
        }
        // An empty password removes only that role; removing a base keeps the others.
        vault.Write(Source, Base, new Dictionary<PasswordRole, string?> { [PasswordRole.Designer] = "" });
        Assert.Equal([PasswordRole.Enterprise, PasswordRole.Repository], vault.Read(Lib, Source, Base).Passwords.Keys.Order());
        vault.Remove(Source, Base);
        Assert.Empty(vault.Read(Lib, Source, Base).Passwords);
        Assert.Equal("other", vault.Get(Lib, Source, Other, PasswordRole.Enterprise));
    }

    // The list keys of the passwords are full paths: a moved profile folder reads them under the old keys until the profile
    // is saved at the new place, which moves them to the new keys.
    [Fact] public void MovedProfileFolderKeepsItsPasswords()
    {
        var entry = new BaseEntry { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
        new LibraryStore(Settings).Save(new Library { Bases = [entry] });
        new PasswordVault(Settings).Write(Source, Base, Passwords("secret"));
        var moved = Path.Combine(root, "elsewhere", "Settings");
        Directory.CreateDirectory(moved);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Settings)!)) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)));
        var settings = Path.Combine(moved, "settings.json");
        var store = new LibraryStore(settings);
        var loaded = store.Load();
        Assert.NotNull(loaded.Moved);
        var vault = new PasswordVault(settings);
        Assert.Equal("secret", vault.For(loaded).Read(Base)[PasswordRole.Enterprise]);
        store.Save(loaded);
        var saved = new LibraryStore(settings).Load();
        Assert.Null(saved.Moved);
        Assert.Equal("secret", vault.Get(saved, ActivityStore.Source(settings, ""), Base, PasswordRole.Enterprise));
        Assert.DoesNotContain(Source.Replace("\\", "\\\\"), File.ReadAllText(vault.PathName));
    }

    // Codex re-check 25.10, R2: the vault could not be changed when the moved profile was saved. The profile keeps its old anchor,
    // so the next load sees the move again and the passwords are still found; a later save moves them.
    [Fact] public void PasswordsOfAMovedFolderStayFoundUntilTheyAreMoved()
    {
        var entry = new BaseEntry { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
        new LibraryStore(Settings).Save(new Library { Bases = [entry] });
        new PasswordVault(Settings).Write(Source, Base, Passwords("secret"));
        var moved = Path.Combine(root, "elsewhere", "Settings");
        Directory.CreateDirectory(moved);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Settings)!)) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)));
        var settings = Path.Combine(moved, "settings.json");
        var vault = new PasswordVault(settings);
        var store = new LibraryStore(settings);
        PasswordVault.BeforeAdopt = _ => throw new IOException("the vault is busy");
        try
        {
            var completed = ProfileRelocation.Complete(store.Load(), store);
            Assert.Contains("пароли не перенесены", store.RelocationWarning);
            Assert.NotNull(completed.Moved);
            Assert.Equal("secret", vault.For(completed).Read(Base)[PasswordRole.Enterprise]);
            var again = new LibraryStore(settings).Load();
            Assert.NotNull(again.Moved);
            Assert.Equal("secret", vault.For(again).Read(Base)[PasswordRole.Enterprise]);
        }
        finally { PasswordVault.BeforeAdopt = null; }
        var retry = new LibraryStore(settings);
        var done = ProfileRelocation.Complete(retry.Load(), retry);
        Assert.Null(retry.RelocationWarning); Assert.Null(done.Moved);
        var saved = new LibraryStore(settings).Load();
        Assert.Null(saved.Moved);
        Assert.Equal("secret", vault.Get(saved, ActivityStore.Source(settings, ""), Base, PasswordRole.Enterprise));
    }

    // Codex full audit 24.10, P1-1: one base ID in two lists, other servers and users; each list passes its own password,
    // and removing the base from one list keeps the other's.
    [Fact] public void TheSameIdInTwoListsHasItsOwnPasswordInEach()
    {
        var (a, b) = (Path.Combine(root, "a.v8i"), Path.Combine(root, "b.v8i"));
        var (sourceA, sourceB) = (ActivityStore.Source(Settings, a), ActivityStore.Source(Settings, b));
        BaseEntry Base1(string server, string user) => new()
        {
            Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = server + "\\base",
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = user }
        };
        var (inA, inB) = (Base1("srv-a", "user-a"), Base1("srv-b", "user-b"));
        var listA = new Library { Executable = Client, ListPath = a, Bases = [inA], Sources = new() { [sourceB] = new SourceSettings { Bases = [inB] } } };
        var listB = LibrarySources.Select(listA, Settings, b);
        var vault = new PasswordVault(Settings);
        vault.Write(sourceA, Base, Passwords("pass-a"));
        vault.Write(sourceB, Base, Passwords("pass-b"));
        IList<string> Start(Library library, BaseEntry entry) => Launcher.Build(library, entry, false, [], passwords: vault.For(library)).ArgumentList;
        Assert.Equal(["ENTERPRISE", "/S", "srv-a\\base", "/N", "user-a", "/P", "pass-a", "/WA-"], Start(listA, inA));
        Assert.Equal(["ENTERPRISE", "/S", "srv-b\\base", "/N", "user-b", "/P", "pass-b", "/WA-"], Start(listB, inB));
        Assert.Equal("pass-a", vault.Get(listA, sourceA, Base, PasswordRole.Enterprise));
        vault.Remove(sourceB, Base);
        Assert.Empty(vault.Read(listB, sourceB, Base).Passwords);
        Assert.Equal(["ENTERPRISE", "/S", "srv-a\\base", "/N", "user-a", "/P", "pass-a", "/WA-"], Start(listA, inA));
    }

    // Codex full audit 24.10, P2-6: a field of another version inside «Authentication» is kept through an unrelated save, a switch
    // of the list and an edit of a known field, for the open list and for the others of the profile.
    [Fact] public void UnknownAuthenticationFieldsSurviveSavesSwitchesAndEdits()
    {
        var list = Path.Combine(root, "b.v8i");
        var store = new LibraryStore(Settings);
        BaseEntry Server(Guid id, string name) => new() { Id = id, Name = name, Kind = ConnectionKind.Server, Location = "srv\\" + name,
            Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user-" + name } };
        store.Save(new Library { Bases = [Server(Base, "a")], Sources = new() { [ActivityStore.Source(Settings, list)] = new SourceSettings { Bases = [Server(Other, "b")] } } });
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!;
        var authentications = new List<System.Text.Json.Nodes.JsonObject>();
        void Find(System.Text.Json.Nodes.JsonNode? item)
        {
            if (item is System.Text.Json.Nodes.JsonObject obj)
                foreach (var (key, value) in obj.ToList()) { if (key == "Authentication" && value is System.Text.Json.Nodes.JsonObject auth) authentications.Add(auth); else Find(value); }
            else if (item is System.Text.Json.Nodes.JsonArray array) foreach (var value in array) Find(value);
        }
        Find(node);
        Assert.Equal(3, authentications.Count);   // the open list, its copy among the sources, the other list
        foreach (var auth in authentications) auth["FutureField"] = System.Text.Json.Nodes.JsonNode.Parse("{\"kept\":[1,2]}");
        File.WriteAllText(Settings, node.ToJsonString());
        int Kept() => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Settings), "\"FutureField\"").Count;

        var loaded = new LibraryStore(Settings).Load();
        var saving = new LibraryStore(Settings); saving.Load();
        saving.Save(loaded with { UseCommonLists = true });
        Assert.Equal(3, Kept());
        // Read again, the settings are equal to those of the first read: the dialog does not take them as changed elsewhere.
        Assert.Equal(loaded.Bases[0].Authentication, new LibraryStore(Settings).Load().Bases[0].Authentication);
        var future = loaded.Bases[0].Authentication!;
        Assert.NotEqual(future with { Extra = null }, future);
        Assert.NotEqual(future, future with { Extra = new() { ["FutureField"] = System.Text.Json.JsonSerializer.SerializeToElement(1) } });
        Assert.NotEqual(future, future with { Extra = new() { ["Other"] = future.Extra!["FutureField"] } });
        // Only unknown fields left: still kept, not taken as no settings.
        Assert.False(new AuthenticationSettings { Extra = future.Extra }.IsEmpty);
        Assert.True(new AuthenticationSettings { Extra = [] }.IsEmpty);
        // The other list opened, then a known field of its base edited.
        var switching = new LibraryStore(Settings);
        var other = LibrarySources.Select(switching.Load(), Settings, list);
        switching.Save(other);
        Assert.Equal(3, Kept());
        var original = BaseEdit.AsDialogReturns(other.Bases[0]);
        var edited = original with { Authentication = original.Authentication! with { EnterpriseUser = "changed" } };
        var current = BaseEdit.OnCurrent(other.Bases, original, edited)!;
        switching.Save(other with { Bases = [current] });
        Assert.Equal(3, Kept());
        Assert.Contains("\"changed\"", File.ReadAllText(Settings));
    }

    // Grok audit of the fixes 25.10, P3: window 1 has the properties open; window 2 saves the base with an unknown authentication
    // field; window 1 then changes the user. The unknown field of window 2 stays.
    [Fact] public void AnEditOfTheUserKeepsAnUnknownFieldSavedByAnotherWindow()
    {
        var auth = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user" };
        var entry = new BaseEntry { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "srv\\base", Authentication = auth };
        var original = BaseEdit.AsDialogReturns(entry);
        var future = new Dictionary<string, System.Text.Json.JsonElement> { ["FutureField"] = System.Text.Json.JsonSerializer.SerializeToElement(42) };
        var now = entry with { Authentication = auth with { Extra = future } };
        var edited = original with { Authentication = original.Authentication! with { EnterpriseUser = "changed" } };
        var saved = BaseEdit.OnCurrent([now], original, edited)!.Authentication!;
        Assert.Equal("changed", saved.EnterpriseUser);
        Assert.Same(future, saved.Extra);
        // Nothing changed in window 1: the settings are those of window 2 as they are.
        Assert.Same(now.Authentication, BaseEdit.OnCurrent([now], original, original)!.Authentication);
        // The page cleared in window 1: no settings.
        Assert.Null(BaseEdit.OnCurrent([now], original, original with { Authentication = null })!.Authentication);
    }

    // A passwords.json of a build up to 0.4.0.0: «<ID>/<role>» sealed as that build seals it (the format did not change).
    private void WriteEarlierVault(params (Guid Id, PasswordRole Role, string Password)[] values)
    {
        // Added to the file as it is (an earlier build writing after this one), or a new file with a new key.
        var vault = new PasswordVault(Settings);
        var key = File.Exists(vault.KeyPath) ? File.ReadAllBytes(vault.KeyPath) : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(vault.KeyPath, key);
        var passwords = File.Exists(vault.PathName) ? SavedNames(vault).ToDictionary(x => x.Key, x => x.Value.GetString()!) : new Dictionary<string, string>();
        foreach (var (id, role, password) in values)
        {
            var name = id.ToString("D") + "/" + role;
            var plain = Encoding.UTF8.GetBytes(password);
            var output = new byte[28 + plain.Length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new System.Security.Cryptography.AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes("BaseNavigator password v1\0" + name));
            passwords[name] = Convert.ToBase64String(output);
        }
        File.WriteAllText(vault.PathName, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Passwords = passwords }));
    }

    private static Dictionary<string, System.Text.Json.JsonElement> SavedNames(PasswordVault vault)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(vault.PathName));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        return json.RootElement.GetProperty("Passwords").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
    }

    private BaseEntry Login(Guid id) => new() { Id = id, Name = "База", Kind = ConnectionKind.Server, Location = "srv\\base",
        Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user" } };

    // Decision of the user 25.10.2026 (after Codex re-check 3): passwords of 0.3.0.x («<ID>/<role>») are never moved to a list
    // nor given to a base. The start removes them whatever the profile (none, damaged, any lists), keeps the passwords of this
    // build and keeps the number of bases for the notice in the same write.
    [Theory, InlineData("none"), InlineData("damaged"), InlineData("lists")]
    public void PasswordsOfAnEarlierBuildAreRemovedAtTheStartWhateverTheProfile(string profile)
    {
        var a = Path.Combine(root, "a.v8i"); var sourceA = ActivityStore.Source(Settings, a);
        var library = new Library { ListPath = a, Bases = [Login(Base)] };
        if (profile == "damaged") File.WriteAllText(Settings, "{ damaged");
        if (profile == "lists") new LibraryStore(Settings).Save(library);
        new PasswordVault(Settings).Write(sourceA, Other, Passwords("kept"));
        WriteEarlierVault((Base, PasswordRole.Enterprise, "old-ent"), (Base, PasswordRole.Repository, "old-repo"), (Other, PasswordRole.Designer, "old-cfg"));
        var vault = new PasswordVault(Settings);
        Assert.Empty(vault.Read(library, sourceA, Base).Passwords);
        Assert.Equal(2, vault.RetireLegacy());
        Assert.Equal([sourceA + "/" + Other + "/Enterprise"], SavedNames(vault).Keys);
        Assert.Empty(vault.Read(library, sourceA, Base).Passwords);
        Assert.Equal("kept", vault.Get(library, sourceA, Other, PasswordRole.Enterprise));
        Assert.Equal(0, vault.RetireLegacy());
        Assert.Equal((2, 2), Counts(vault.PendingRetired()));
        Assert.Equal("Сохранённые пароли прежней версии удалены у баз: 2 — введите их заново в свойствах.", PasswordVault.RetiredMessage(2));
    }

    // Codex re-check 3, C1: the key is missing at the start, so the earlier passwords cannot be removed; the lists change in the
    // session. No read gives them to any list; the next start with the key removes them, and no list has got them.
    [Fact] public void AnEarlierPasswordThatCannotBeRemovedYetIsGivenToNobody()
    {
        WriteEarlierVault((Base, PasswordRole.Enterprise, "old-ent"));
        var (a, b) = (Path.Combine(root, "a.v8i"), Path.Combine(root, "b.v8i"));
        var (sourceA, sourceB) = (ActivityStore.Source(Settings, a), ActivityStore.Source(Settings, b));
        var both = new Library { Executable = Client, ListPath = a, Bases = [Login(Base)], Sources = new() { [sourceB] = new SourceSettings { Bases = [Login(Base)] } } };
        new LibraryStore(Settings).Save(both);
        var vault = new PasswordVault(Settings);
        var key = File.ReadAllBytes(vault.KeyPath); File.Delete(vault.KeyPath);
        Assert.Throws<IOException>(() => vault.RetireLegacy());
        Assert.Equal(0, vault.PendingRetired().Bases);
        var listB = LibrarySources.Select(both with { Bases = [] }, Settings, b);
        foreach (var (library, source) in new[] { (both, sourceA), (listB, sourceB) })
            Assert.Empty(vault.Read(library, source, Base).Passwords);
        File.WriteAllBytes(vault.KeyPath, key);
        Assert.DoesNotContain("old-ent", Launcher.Build(listB, Login(Base), false, [], passwords: vault.For(listB)).ArgumentList);
        Assert.Equal(1, vault.RetireLegacy());
        Assert.Empty(SavedNames(vault));
        Assert.Empty(vault.Read(listB, sourceB, Base).Passwords);
    }

    // Codex re-check 3, C3: the notice is written with the removal and stays until the main window shows it: a shortcut start,
    // --no-gui or a crash before the window keep it; other changes of the vault keep it. Shown, it goes. An earlier build writing
    // the file drops it (accepted); what that build wrote is counted by the next start.
    [Fact] public void TheNoticeOfRemovedPasswordsStaysUntilItIsShown()
    {
        WriteEarlierVault((Base, PasswordRole.Enterprise, "old-ent"));
        Assert.Equal(1, new PasswordVault(Settings).RetireLegacy());
        var restarted = new PasswordVault(Settings);
        Assert.Equal((1, 1), Counts(restarted.PendingRetired()));
        restarted.Write(Source, Other, Passwords("new"));
        restarted.Remove(Source, Other);
        restarted.Adopt(ProfileRelocation.Detect(Path.Combine(root, "old", "settings.json"), [], Settings)!);
        Assert.Equal((1, 1), Counts(new PasswordVault(Settings).PendingRetired()));
        restarted.Acknowledge(restarted.PendingRetired());
        Assert.Equal((0, 1), Counts(restarted.PendingRetired()));
        WriteEarlierVault((Other, PasswordRole.Enterprise, "later"));
        Assert.Equal((0, 0), Counts(restarted.PendingRetired()));
        Assert.Equal(1, restarted.RetireLegacy());
        Assert.Equal((1, 1), Counts(restarted.PendingRetired()));
        Assert.Equal((0, 0), Counts(new PasswordVault(Path.Combine(root, "empty", "settings.json")).PendingRetired()));
        foreach (var (total, shown) in new[] { (-1, 0), (1, -1), (1, 2) })
        {
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(restarted.PathName))!;
            file["RetiredTotal"] = total; file["ShownUpTo"] = shown;
            File.WriteAllText(restarted.PathName, file.ToJsonString());
            Assert.Throws<InvalidDataException>(() => restarted.PendingRetired());
        }
    }

    private static (int Bases, int UpTo) Counts(RetiredNotice notice) => (notice.Bases, notice.UpTo);

    // Codex re-check 5, RC5-1: A and B show notice X; A acknowledges it; an earlier build rewrites the file (the counters and their
    // generation go) with a new old-format password Y; a start removes Y. B's acknowledgement of X, of the generation before,
    // must leave Y pending.
    [Fact] public void AnAcknowledgementFromBeforeAnEarlierBuildWroteDoesNotTakeANewNotice()
    {
        WriteEarlierVault((Base, PasswordRole.Enterprise, "x"));
        var (a, b) = (new PasswordVault(Settings), new PasswordVault(Settings));
        Assert.Equal(1, a.RetireLegacy());
        var (seenA, seenB) = (a.PendingRetired(), b.PendingRetired());
        Assert.NotNull(seenA.Generation);
        a.Acknowledge(seenA);
        Assert.Equal((0, 1), Counts(b.PendingRetired()));
        WriteEarlierVault((Other, PasswordRole.Enterprise, "y"));
        Assert.DoesNotContain("Retired", File.ReadAllText(a.PathName));
        Assert.Equal(1, a.RetireLegacy());
        var y = a.PendingRetired();
        Assert.Equal((1, 1), Counts(y));
        Assert.NotEqual(seenA.Generation, y.Generation);
        b.Acknowledge(seenB);
        Assert.Equal(y, a.PendingRetired());
        a.Acknowledge(new RetiredNotice(1, 1));   // a snapshot without a generation (a vault read before any removal)
        Assert.Equal(y, a.PendingRetired());
        b.Acknowledge(y);
        Assert.Equal((0, 1), Counts(a.PendingRetired()));
    }

    // Codex re-check 4, N1: windows A and B both show the notice of removal X. A acknowledges it; then removal Y happens; B's
    // acknowledgement of its older notice must not take Y. A repeated acknowledgement changes nothing either, and one given while
    // another removal was added acknowledges only what it showed.
    [Fact] public void AnOlderAcknowledgementDoesNotTakeANewerNotice()
    {
        WriteEarlierVault((Base, PasswordRole.Enterprise, "x"));
        var (a, b) = (new PasswordVault(Settings), new PasswordVault(Settings));
        Assert.Equal(1, a.RetireLegacy());
        var (seenA, seenB) = (a.PendingRetired(), b.PendingRetired());
        Assert.Equal(seenA, seenB);
        a.Acknowledge(seenA);
        WriteEarlierVault((Other, PasswordRole.Enterprise, "y"));
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(a.PathName))!;
        file["RetiredTotal"] = 1; file["ShownUpTo"] = 1; file["RetiredGeneration"] = seenA.Generation;   // as this build left them (the fixture writes the file anew)
        File.WriteAllText(a.PathName, file.ToJsonString());
        Assert.Equal(1, b.RetireLegacy());
        b.Acknowledge(seenB);
        a.Acknowledge(seenA);
        Assert.Equal((1, 2), Counts(a.PendingRetired()));
        // Shown while a third removal was counted: only the second goes.
        var seenY = b.PendingRetired();
        WriteEarlierVault((Guid.NewGuid(), PasswordRole.Enterprise, "z"));
        file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(a.PathName))!;
        file["RetiredTotal"] = 2; file["ShownUpTo"] = 1; file["RetiredGeneration"] = seenY.Generation;
        File.WriteAllText(a.PathName, file.ToJsonString());
        Assert.Equal(1, a.RetireLegacy());
        b.Acknowledge(seenY);
        Assert.Equal((1, 3), Counts(a.PendingRetired()));
        var last = a.PendingRetired(); a.Acknowledge(last); a.Acknowledge(last);
        Assert.Equal((0, 3), Counts(b.PendingRetired()));
    }


    // Codex re-check 2, F2: the key of a moved folder is missing when the profile is saved. The move is kept and said; with the key
    // back the passwords are found and the next save moves them.
    [Fact] public void AMissingKeyKeepsTheMoveOfTheFolder()
    {
        var entry = new BaseEntry { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
        new LibraryStore(Settings).Save(new Library { Bases = [entry] });
        new PasswordVault(Settings).Write(Source, Base, Passwords("secret"));
        var moved = Path.Combine(root, "elsewhere", "Settings");
        Directory.CreateDirectory(moved);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Settings)!)) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)));
        var settings = Path.Combine(moved, "settings.json");
        var vault = new PasswordVault(settings);
        var key = File.ReadAllBytes(vault.KeyPath); File.Delete(vault.KeyPath);
        var store = new LibraryStore(settings);
        var completed = ProfileRelocation.Complete(store.Load(), store);
        Assert.Contains("пароли не перенесены", store.RelocationWarning);
        Assert.NotNull(completed.Moved);
        File.WriteAllBytes(vault.KeyPath, key);
        var again = new LibraryStore(settings);
        var reloaded = again.Load();
        Assert.NotNull(reloaded.Moved);
        Assert.Equal("secret", vault.For(reloaded).Read(Base)[PasswordRole.Enterprise]);
        var done = ProfileRelocation.Complete(reloaded, again);
        Assert.Null(again.RelocationWarning); Assert.Null(done.Moved);
        var saved = new LibraryStore(settings).Load();
        Assert.Null(saved.Moved);
        Assert.Equal("secret", vault.Get(saved, ActivityStore.Source(settings, ""), Base, PasswordRole.Enterprise));
    }

    [Fact] public void DamagedOrMissingKeyIsRefusedAndNothingIsOverwritten()
    {
        var vault = new PasswordVault(Settings);
        vault.Write(Source, Base, Passwords("secret"));
        var text = File.ReadAllText(vault.PathName);
        // A value moved to another base does not open: the base and role are part of the seal.
        File.WriteAllText(vault.PathName, text.Replace(Base.ToString("D"), Other.ToString("D")));
        Assert.Throws<InvalidDataException>(() => vault.Read(Lib, Source, Other).Passwords);
        File.WriteAllText(vault.PathName, text);
        var key = File.ReadAllBytes(vault.KeyPath);
        key[0] ^= 1; File.WriteAllBytes(vault.KeyPath, key);
        Assert.Contains("не расшифрован", Assert.Throws<InvalidDataException>(() => vault.Read(Lib, Source, Base).Passwords).Message);
        File.Delete(vault.KeyPath);
        Assert.Contains("нет файла ключа", Assert.Throws<IOException>(() => vault.Read(Lib, Source, Base).Passwords).Message);
        // A new key would make the saved passwords unreadable: the write is refused and the file stays.
        Assert.Throws<IOException>(() => vault.Write(Source, Other, Passwords("new")));
        Assert.Equal(text, File.ReadAllText(vault.PathName));
        Assert.False(File.Exists(vault.KeyPath));
        File.WriteAllText(vault.PathName, "{ not json");
        Assert.Contains("повреждён", Assert.Throws<InvalidDataException>(() => vault.Read(Lib, Source, Base).Passwords).Message);
    }

    // Codex F1: a damaged key of the right length, or a damaged value of another base, must not take a write: the vault
    // would hold passwords under two keys and the replaced one could never be read again.
    [Fact] public void WriteIsRefusedWhenSavedValuesDoNotOpen()
    {
        var vault = new PasswordVault(Settings);
        vault.Write(Source, Base, Passwords("secret"));
        var key = File.ReadAllBytes(vault.KeyPath); var text = File.ReadAllText(vault.PathName);
        var damaged = (byte[])key.Clone(); damaged[0] ^= 1; File.WriteAllBytes(vault.KeyPath, damaged);
        Assert.Throws<InvalidDataException>(() => vault.Write(Source, Base, Passwords("replacement")));
        Assert.Throws<InvalidDataException>(() => vault.Write(Source, Other, Passwords("other")));
        Assert.Equal(text, File.ReadAllText(vault.PathName));
        File.WriteAllBytes(vault.KeyPath, key);
        Assert.Equal("secret", vault.Get(Lib, Source, Base, PasswordRole.Enterprise));
        // A value that is not Base64, or null (Codex F8), is the corruption error on reading and refuses a write.
        foreach (var value in new[] { "\"not base64!\"", "null", "\"AAAA\"" })
        {
            // The serializer writes «+» as +: the value is everything between the quotes after the name.
            var replaced = System.Text.RegularExpressions.Regex.Replace(text, "(/" + Base.ToString("D") + "/Enterprise\":\\s*)\"[^\"]+\"", "${1}" + value);
            Assert.NotEqual(text, replaced);
            File.WriteAllText(vault.PathName, replaced);
            Assert.Throws<InvalidDataException>(() => vault.Read(Lib, Source, Base).Passwords);
            Assert.Throws<InvalidDataException>(() => vault.Write(Source, Other, Passwords("other")));
        }
    }

    [LinuxFact] public void VaultFilesAreOwnerOnlyOnLinux()
    {
        var vault = new PasswordVault(Settings);
        vault.Write(Source, Base, Passwords("secret"));
        vault.Write(Source, Other, Passwords("other"));
        foreach (var file in new[] { vault.PathName, vault.KeyPath })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        // Codex F2: a folder copied with 0644 files is narrowed before use, on reading and on writing.
        var broad = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        foreach (var file in new[] { vault.PathName, vault.KeyPath }) File.SetUnixFileMode(file, broad);
        Assert.Equal("secret", vault.Get(Lib, Source, Base, PasswordRole.Enterprise));
        foreach (var file in new[] { vault.PathName, vault.KeyPath })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        foreach (var file in new[] { vault.PathName, vault.KeyPath }) File.SetUnixFileMode(file, broad);
        vault.Write(Source, Other, Passwords("changed"));
        foreach (var file in new[] { vault.PathName, vault.KeyPath })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    private BaseEntry Entry(AuthenticationSettings? authentication = null, bool ask = false, string? parameters = null) => new()
    { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base", Authentication = authentication, AskCredentials = ask, AdditionalParameters = parameters };

    private IReadOnlyList<string> Arguments(BaseEntry entry, bool designer, LaunchOptions? options = null) =>
        [.. Launcher.Build(new Library { Executable = Client }, entry, designer, [], options: options, passwords: new PasswordVault(Settings).For(new Library())).ArgumentList];

    [Fact] public void SavedNamesAndPasswordsArePassedToOneC()
    {
        new PasswordVault(Settings).Write(Source, Base, Passwords("ent pass", "cfg", "repo pass"));
        var auth = new AuthenticationSettings
        {
            EnterpriseLogin = true, EnterpriseUser = "Иванов И.", DesignerLogin = true, DesignerUser = "Admin",
            Repository = true, RepositoryPath = "tcp://repo/erp", RepositoryUser = "dev"
        };
        var entry = Entry(auth, parameters: "/L ru");
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "Иванов И.", "/P", "ent pass", "/WA-", "/L", "ru"], Arguments(entry, false));
        Assert.Equal(["DESIGNER", "/S", "server\\base", "/N", "Admin", "/P", "cfg", "/WA-",
            "/ConfigurationRepositoryF", "tcp://repo/erp", "/ConfigurationRepositoryN", "dev", "/ConfigurationRepositoryP", "repo pass", "/L", "ru"], Arguments(entry, true));
        // A user without a saved password: 1C asks for the password itself.
        new PasswordVault(Settings).Remove(Source, Base);
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "Иванов И.", "/WA-", "/L", "ru"], Arguments(entry, false));
        // The check box off: the name is kept but not passed.
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/L", "ru"], Arguments(entry with { Authentication = auth with { EnterpriseLogin = false } }, false));
    }

    [Fact] public void OsAuthenticationAndTheListModeChooseWa()
    {
        var auth = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user", OsAuthentication = true };
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/WA+"], Arguments(Entry(auth), false));
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/WA-"], Arguments(Entry(ask: true), false));
        Assert.Equal(["ENTERPRISE", "/S", "server\\base"], Arguments(Entry(), false));
        // «Запустить с аутентификацией»: the saved name and password are not passed.
        new PasswordVault(Settings).Write(Source, Base, Passwords("secret"));
        var login = Arguments(Entry(auth with { OsAuthentication = false }), false, LaunchOptions.Login);
        Assert.DoesNotContain("/N", login); Assert.DoesNotContain("secret", login); Assert.Equal("/WA-", login[^1]);
        // Kimi P3: the configurator started that way still connects the repository of the page, with its saved password.
        new PasswordVault(Settings).Write(Source, Base, Passwords(repository: "/WA+"));
        var designer = auth with { OsAuthentication = false, DesignerLogin = true, DesignerUser = "Admin", Repository = true, RepositoryPath = "tcp://repo/erp", RepositoryUser = "dev" };
        Assert.Equal(["DESIGNER", "/S", "server\\base", "/ConfigurationRepositoryF", "tcp://repo/erp", "/ConfigurationRepositoryN", "dev",
            "/ConfigurationRepositoryP", "/WA+", "/WA-"], Arguments(Entry(designer, ask: true), true, LaunchOptions.Login));
    }

    // Coverage gap of the Codex re-check: «Запустить с аутентификацией» for the configurator of a base that is not on a web server
    // keeps the repository of the page, passes no /WSA- even if the record carries WSA=1, and never passes the saved login.
    [Fact] public void StartWithLoginConnectsTheRepositoryAndNeverPassesWsaForALocalBase()
    {
        new PasswordVault(Settings).Write(Source, Base, Passwords("ent", "cfg", "repo pass"));
        var auth = new AuthenticationSettings { DesignerLogin = true, DesignerUser = "Admin", Repository = true, RepositoryPath = "tcp://repo/erp", RepositoryUser = "dev" };
        var entry = Entry(auth) with { WebServerAskCredentials = true };
        var args = Arguments(entry, true, LaunchOptions.Login);
        Assert.Equal(["DESIGNER", "/S", "server\\base", "/ConfigurationRepositoryF", "tcp://repo/erp", "/ConfigurationRepositoryN", "dev",
            "/ConfigurationRepositoryP", "repo pass", "/WA-"], args);
        Assert.DoesNotContain("/WSA-", args); Assert.DoesNotContain("cfg", args);
        Assert.DoesNotContain("/WSA-", Arguments(entry, false, LaunchOptions.Login));
    }

    [Fact] public void TheSameKeyOnThePageAndInParametersIsRefused()
    {
        var auth = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user" };
        Assert.Contains("/N", Assert.Throws<ArgumentException>(() => Arguments(Entry(auth, parameters: "/N other"), false)).Message);
        Assert.Contains("/WA", Assert.Throws<ArgumentException>(() => Arguments(Entry(ask: true, parameters: "/WA+"), false)).Message);
        // Codex F5: a value of a known key is not a switch; a name written together with the key is one.
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "user", "/WA-", "/C", "/N"], Arguments(Entry(auth, parameters: "/C /N"), false));
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/WA-", "/C", "/WA+"], Arguments(Entry(ask: true, parameters: "/C /WA+"), false));
        Assert.Contains("/N", Assert.Throws<ArgumentException>(() => Arguments(Entry(auth, parameters: "/Nother"), false)).Message);
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "user", "/WA-", "/NoProxy"], Arguments(Entry(auth, parameters: "/NoProxy"), false));
        // Codex F5 recheck: a repository user given in the parameters is a value too, also when it looks like a key.
        var designer = new AuthenticationSettings { DesignerLogin = true, DesignerUser = "user" };
        foreach (var value in new[] { "/WA+", "/Nother" })
            Assert.Equal(["DESIGNER", "/S", "server\\base", "/N", "user", "/WA-", "/ConfigurationRepositoryN", value],
                Arguments(Entry(designer, parameters: "/ConfigurationRepositoryN " + value), true));
        // The repository keys take a value also for «Запустить с аутентификацией»: «/WA+» there is a user name, kept.
        Assert.Equal(["DESIGNER", "/S", "server\\base", "/ConfigurationRepositoryN", "/WA+", "/Debug", "/WA-"],
            Arguments(Entry(parameters: "/ConfigurationRepositoryN /WA+ /Debug"), true, LaunchOptions.Login));
        // A password that looks like a key is a value of /P, not a conflict.
        new PasswordVault(Settings).Write(Source, Base, Passwords("/WA+"));
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "user", "/P", "/WA+", "/WA-"], Arguments(Entry(auth), false));
        // Without keys of the page the parameters are passed as they are.
        Assert.Equal(["ENTERPRISE", "/S", "server\\base", "/N", "other"], Arguments(Entry(parameters: "/N other"), false));
        Assert.Throws<ArgumentException>(() => new AuthenticationSettings { Repository = true }.Validate());
        Assert.Throws<ArgumentException>(() => new AuthenticationSettings { EnterpriseUser = "a\nb" }.Validate());
    }

    [Fact] public void ListModeIsReadAndWrittenAsWaAndPageSettingsStayLocal()
    {
        var list = Path.Combine(root, "ibases.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Base}\r\nWA=1\r\nCustom=keep\r\n[Другая]\r\nConnect=Srvr=\"server\";Ref=\"other\";\r\nID={Other}\r\nWA=0\r\n", new UTF8Encoding(false));
        var store = new V8iStore(list); var library = store.Load(new());
        Assert.Equal([false, true], library.Bases.Select(x => x.AskCredentials));
        var auth = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "user" };
        var changed = library.Bases[0] with { AskCredentials = true, Authentication = auth };
        var saved = store.Save(library with { Bases = [changed, library.Bases[1]] });
        var text = File.ReadAllText(list);
        Assert.Contains($"ID={Base}\r\nWA=0\r\nCustom=keep\r\n", text);
        Assert.DoesNotContain("user", text);
        // The page settings come back from the local settings on the next load; WA from the list.
        var reloaded = new V8iStore(list).Load(saved);
        Assert.Equal(auth, reloaded.Bases[0].Authentication);
        Assert.True(reloaded.Bases[0].AskCredentials);
        // Kimi P3: the exported copy of a local list keeps «Запрашивать имя и пароль».
        var exported = ListMaintenance.ToV8i(new Library { Bases = [.. reloaded.Bases] });
        Assert.Contains($"ID={Base}\r\nFolder=/\r\nVersion=8.3\r\nWA=0", exported.Replace("\n", "\r\n").Replace("\r\r", "\r"));
        // Saving the profile and reading it back keeps the page.
        var settings = new LibraryStore(Settings); settings.Save(saved);
        Assert.Equal(auth, new V8iStore(list).Load(new LibraryStore(Settings).Load()).Bases[0].Authentication);
    }

    private static string Bytes(string hex)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetString(Convert.FromHexString(hex));
    }

    // Codex F4. Fixtures computed by a separate implementation written from the original's code (_Unit186.pas,
    // sub_00847BD4/sub_00847A7C), not by LegacyPasswords itself.
    [Theory]
    [InlineData("7ecefed1b85a9cfe", "Пароль1")]
    [InlineData("7ec98254b1749a", "Иванов")]
    [InlineData("768174988085613832", "Secret_42")]
    public void LegacyPasswordsAreTheOriginalCipher(string encrypted, string password)
    {
        Assert.Equal(password, LegacyPasswords.Decrypt(Bytes(encrypted)));
        Assert.Equal(Bytes(encrypted), LegacyPasswords.Encrypt(password));
    }

    [Fact] public void LegacyDecryptionIsTheOriginalsOwnAlsoWhereItLoses()
    {
        // The original stores a space as «P» (+0x30 without the key) but subtracts the key when reading: it passes «a4b».
        Assert.Equal(Bytes("845073"), LegacyPasswords.Encrypt("a b"));
        Assert.Equal("a4b", LegacyPasswords.Decrypt(Bytes("845073")));
    }

    private (LegacyImportPreview Preview, string Settings) ImportOriginal(bool newEncryption) =>
        ImportOriginal($"<Settings><Security><NewMethodEncryption>{(newEncryption ? "true" : "false")}</NewMethodEncryption></Security></Settings>");

    private (LegacyImportPreview Preview, string Settings) ImportOriginal(string? settingsCnf, string enterprisePassword = "ent-42")
    {
        var folder = Path.Combine(root, "SMSettings"); Directory.CreateDirectory(folder);
        var list = Path.Combine(root, "original.v8i");
        File.WriteAllText(list, $"[Бухгалтерия]\nID={Base}\nConnect=Srvr=\"server\";Ref=\"accounting\";\nFolder=/\nWA=0\n");
        File.WriteAllText(Path.Combine(folder, "v8config.smc"),
            $"﻿[{Base}]\r\nUserLoginEnt=1\r\nEntUser=Иванов\r\nEntPassword={LegacyPasswords.Encrypt(enterprisePassword)}\r\nUserLoginCnf=0\r\nCfgUser=\r\nCfgPassword=\r\n" +
            $"OSAuthentication=0\r\nUserStorage=1\r\nStorageDir=tcp://repo/erp\r\nStorageUser=dev\r\nStoragePassword={LegacyPasswords.Encrypt("repo-7")}\r\n", new UTF8Encoding(false));
        var cnf = Path.Combine(folder, "settings.cnf");
        if (settingsCnf is null) File.Delete(cnf); else File.WriteAllText(cnf, settingsCnf);
        var target = Path.Combine(root, "import", "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return (LegacyImport.Preview(new Library(), list, folder, new(Clients: false, Favorites: false)), target);
    }

    [Fact] public void ImportFromTheOriginalTakesThePageAndItsPasswords()
    {
        var (preview, target) = ImportOriginal(newEncryption: false);
        var entry = Assert.Single(preview.Added);
        Assert.Equal(new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Иванов", Repository = true, RepositoryPath = "tcp://repo/erp", RepositoryUser = "dev" },
            entry.Authentication);
        Assert.True(entry.AskCredentials);
        Assert.DoesNotContain(preview.Notices, x => x.Contains("EntPassword") || x.Contains("ent-42"));
        var applied = LegacyImport.Apply(preview, new LibraryStore(target), new ActivityStore(target));
        Assert.Null(applied.FavoriteError);
        var read = new PasswordVault(target).Read(Lib, ActivityStore.Source(target, ""), Base).Passwords;
        Assert.Equal(("ent-42", "repo-7"), (read[PasswordRole.Enterprise], read[PasswordRole.Repository]));
        Assert.False(read.ContainsKey(PasswordRole.Designer));
        Assert.DoesNotContain("ent-42", File.ReadAllText(target));
    }

    [Fact] public void CyrillicPasswordOfTheOriginalIsImported()
    {
        var (preview, target) = ImportOriginal("<Settings/>", "Пароль1");
        LegacyImport.Apply(preview, new LibraryStore(target), new ActivityStore(target));
        Assert.Equal("Пароль1", new PasswordVault(target).Get(Lib, ActivityStore.Source(target, ""), Base, PasswordRole.Enterprise));
    }

    // Codex F6: a missing, damaged or unclear settings.cnf says nothing about the method; passwords are not guessed.
    [Theory]
    [InlineData(null)]
    [InlineData("<Settings><Security><NewMethodEncryption>true")]
    [InlineData("<Settings><NewMethodEncryption>maybe</NewMethodEncryption></Settings>")]
    public void UnknownPasswordMethodKeepsNamesButNotPasswords(string? settingsCnf)
    {
        var (preview, _) = ImportOriginal(settingsCnf);
        Assert.Equal("Иванов", Assert.Single(preview.Added).Authentication?.EnterpriseUser);
        Assert.Empty(preview.Passwords);
        Assert.Contains(preview.Notices, x => x.Contains("не определён"));
    }

    [Fact] public void UnknownEncryptionOfTheOriginalKeepsNamesButNotPasswords()
    {
        var (preview, target) = ImportOriginal(newEncryption: true);
        Assert.Equal("Иванов", Assert.Single(preview.Added).Authentication?.EnterpriseUser);
        Assert.Empty(preview.Passwords);
        Assert.Contains(preview.Notices, x => x.Contains("NewMethodEncryption"));
        LegacyImport.Apply(preview, new LibraryStore(target), new ActivityStore(target));
        Assert.False(File.Exists(new PasswordVault(target).PathName));
    }
}
