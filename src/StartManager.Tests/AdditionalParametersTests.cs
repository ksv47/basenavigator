using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Дополнительные параметры» as in the original: the string of a base is kept in AdditionalParameters= of its .v8i record.
/// Synthetic data only.</summary>
public sealed class AdditionalParametersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "additional-parameters-" + Guid.NewGuid().ToString("N"));
    private string List => Path.Combine(root, "ibases.v8i");
    private string Client => Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
    private static readonly Guid Nordwind = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Demo = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static string Fixture(string nordwindTail = "") =>
        "[Nordwind]\r\nConnect=Srvr=\"demo-server\";Ref=\"nordwind\";\r\nID=" + Nordwind + "\r\nFolder=/\r\nUnknown=keep\r\n" + nordwindTail +
        "[Demo]\r\nConnect=File=\"D:\\Bases\\Demo\";\r\nID=" + Demo + "\r\nFolder=/\r\nVersion=8.3\r\n" +
        "[Unsupported]\r\nConnect=Custom=\"x\";\r\nFolder=/\r\nAdditionalParameters=/Debug\r\n";

    public AdditionalParametersTests() { Directory.CreateDirectory(root); File.WriteAllText(Client, "fixture"); }
    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(root)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private V8iStore Write(string content, Encoding? encoding = null)
    { File.WriteAllText(List, content, encoding ?? new UTF8Encoding(false)); return new(List); }

    private static Library With(Library library, Guid id, Func<BaseEntry, BaseEntry> change) =>
        library with { Bases = [.. library.Bases.Select(x => x.Id == id ? change(x) : x)] };

    [Fact]
    public void SavedStringGoesToTheEndOfTheRecordAndEverythingElseStays()
    {
        var store = Write(Fixture()); var library = store.Load(new());
        const string parameters = "/C \"Привет, \"\"мир\"\"\" /DisableStartupMessages";
        store.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, "  " + parameters + " ", inList: true)));
        Assert.Equal(Fixture("AdditionalParameters=" + parameters + "\r\n"), File.ReadAllText(List));
        Assert.Equal(Fixture(), File.ReadAllText(List + ".bak"));
        var reread = new V8iStore(List).Load(new());
        var nordwind = reread.Bases.Single(x => x.Id == Nordwind);
        Assert.Equal(parameters, nordwind.ListParameters); Assert.Null(nordwind.AdditionalParameters);
        Assert.Equal(new[] { "/C", "Привет, \"мир\"", "/DisableStartupMessages" }, LaunchParameters.Validate(nordwind.EffectiveParameters));
        Assert.Equal("", reread.Bases.Single(x => x.Id == Demo).ListParameters);
    }

    [Fact]
    public void ChangedStringReplacesTheLineInPlaceAndAnEmptyOneRemovesIt()
    {
        var store = Write(Fixture("AdditionalParameters=/L en\r\nAfter=keep\r\n")); var library = store.Load(new());
        Assert.Equal("/L en", library.Bases.Single(x => x.Id == Nordwind).EffectiveParameters);
        library = store.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, "/L ru", inList: true)));
        Assert.Equal(Fixture("AdditionalParameters=/L ru\r\nAfter=keep\r\n"), File.ReadAllText(List));
        // As the original: an emptied field removes the key (TBasesIniFile.DeleteKey), no «AdditionalParameters=» is left.
        store.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, "   ", inList: true)));
        Assert.Equal(Fixture("After=keep\r\n"), File.ReadAllText(List));
        Assert.Equal("", new V8iStore(List).Load(new()).Bases.Single(x => x.Id == Nordwind).EffectiveParameters);
    }

    [Fact]
    public void UnchangedStringDoesNotRewriteTheList()
    {
        var store = Write(Fixture("AdditionalParameters=/L en\r\n")); var library = store.Load(new());
        var bytes = File.ReadAllBytes(List);
        store.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, "/L en", inList: true)));
        Assert.Equal(bytes, File.ReadAllBytes(List)); Assert.False(File.Exists(List + ".bak"));
    }

    [Fact]
    public void NewRecordGetsTheLineOnlyWithAString()
    {
        var store = Write(Fixture()); var library = store.Load(new());
        var first = BaseEdit.WithParameters(new BaseEntry { Name = "Nordwind 2", Kind = ConnectionKind.File, Location = "D:\\Bases\\Demo2" }, "/ClearCache", inList: true);
        var second = BaseEdit.WithParameters(new BaseEntry { Name = "Nordwind 3", Kind = ConnectionKind.File, Location = "D:\\Bases\\Demo3" }, "", inList: true);
        store.Save(library with { Bases = [.. library.Bases, first, second] });
        var text = File.ReadAllText(List);
        Assert.Equal(2, text.Split("AdditionalParameters=").Length - 1); // Nordwind 2 and the unsupported record
        var reread = new V8iStore(List).Load(new());
        Assert.Equal("/ClearCache", reread.Bases.Single(x => x.Id == first.Id).ListParameters);
        Assert.Equal("", reread.Bases.Single(x => x.Id == second.Id).ListParameters);
        var record = text[text.IndexOf("[Nordwind 3]", StringComparison.Ordinal)..];
        Assert.DoesNotContain("AdditionalParameters", record);
    }

    [Fact]
    public void CyrillicStringIsKeptInAWindows1251List()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        // A list in Windows-1251 (not valid UTF-8): its encoding stays.
        var store = Write("; Синтетический список\r\n" + Fixture(), cp1251); var library = store.Load(new());
        store.Save(With(library, Demo, x => BaseEdit.WithParameters(x, "/C \"режим проверки\"", inList: true)));
        Assert.Contains("AdditionalParameters=/C \"режим проверки\"\r\n", cp1251.GetString(File.ReadAllBytes(List)));
        Assert.Equal("/C \"режим проверки\"", new V8iStore(List).Load(new()).Bases.Single(x => x.Id == Demo).ListParameters);
    }

    [Fact]
    public void ProfileStringMovesToTheListWhenThePropertiesAreSaved()
    {
        var store = Write(Fixture("AdditionalParameters=/L en\r\n"));
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        Library Open(Library settings) => new V8iStore(List).Load(LibrarySources.Select(settings, profile.PathName, List));
        // A string of the profile saved by an earlier build: it wins until the properties are saved, nothing is rewritten before.
        var library = store.Load(new());
        profile.Save(With(library, Nordwind, x => x with { AdditionalParameters = "/DisableStartupMessages" }));
        var before = File.ReadAllBytes(List);
        library = Open(profile.Load());
        var nordwind = library.Bases.Single(x => x.Id == Nordwind);
        Assert.Equal("/DisableStartupMessages", nordwind.EffectiveParameters);
        Assert.Contains("/DisableStartupMessages", Launcher.Build(Client, nordwind, false).ArgumentList);
        Assert.Equal(before, File.ReadAllBytes(List));
        // «Сохранить» in the properties: the field shows the profile string and saves it into the record; the profile forgets it.
        var shared = new V8iStore(List); library = shared.Load(LibrarySources.Select(profile.Load(), profile.PathName, List));
        var saved = shared.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, x.EffectiveParameters, inList: true)), ensureId: Nordwind);
        profile.Save(saved);
        Assert.Contains("AdditionalParameters=/DisableStartupMessages\r\n", File.ReadAllText(List));
        Assert.DoesNotContain("/L en", File.ReadAllText(List));
        Assert.DoesNotContain("DisableStartupMessages", File.ReadAllText(profile.PathName));
        nordwind = Open(profile.Load()).Bases.Single(x => x.Id == Nordwind);
        Assert.Null(nordwind.AdditionalParameters); Assert.Equal("/DisableStartupMessages", nordwind.EffectiveParameters);
    }

    [Fact]
    public void ReadOnlyListKeepsTheStringInTheProfile()
    {
        var text = Fixture("AdditionalParameters=/L en\r\n");
        var store = Write(text);
        File.SetAttributes(List, File.GetAttributes(List) | FileAttributes.ReadOnly);
        var library = store.Load(new());
        // Root on Linux writes anyway: there the list is not read-only and the case does not apply.
        if (store.ReadOnly is null) return;
        var nordwind = library.Bases.Single(x => x.Id == Nordwind);
        // Left as the list gives it: still inherited, nothing to save.
        Assert.Same(nordwind, BaseEdit.WithParameters(nordwind, "/L en", inList: false));
        var edited = BaseEdit.WithParameters(nordwind, "/L ru", inList: false);
        Assert.Equal("/L ru", edited.AdditionalParameters); Assert.Equal("/L en", edited.ListParameters);
        var saved = store.Save(With(library, Nordwind, _ => edited));
        Assert.Equal(text, File.ReadAllText(List));
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        profile.Save(saved);
        var reread = new V8iStore(List).Load(LibrarySources.Select(profile.Load(), profile.PathName, List)).Bases.Single(x => x.Id == Nordwind);
        Assert.Equal("/L ru", reread.EffectiveParameters);
        Assert.Equal(new[] { "ENTERPRISE", "/S", "demo-server\\nordwind", "/L", "ru" }, Launcher.Build(Client, reread, false).ArgumentList);
        // A string into the record would change the file: refused, the file stays.
        Assert.Throws<ListReadOnlyException>(() => store.Save(With(library, Nordwind, x => BaseEdit.WithParameters(x, "/L ru", inList: true))));
        Assert.Equal(text, File.ReadAllText(List));
    }

    [Fact]
    public void ProfileWithoutAListKeepsTheString()
    {
        var entry = new BaseEntry { Name = "Nordwind", Kind = ConnectionKind.File, Location = "D:\\Bases\\Demo" };
        Assert.Same(entry, BaseEdit.WithParameters(entry, "", inList: false));
        Assert.Equal("/Debug", BaseEdit.WithParameters(entry, "/Debug", inList: false).AdditionalParameters);
        var cleared = BaseEdit.WithParameters(entry with { AdditionalParameters = "/Debug" }, "", inList: false);
        Assert.Equal("", cleared.AdditionalParameters); Assert.Equal("", cleared.EffectiveParameters);
    }

    [Fact]
    public void AnotherProgramsChangeOfAnotherRecordIsMergedAndOfTheSameOneRefused()
    {
        var store = Write(Fixture()); var library = store.Load(new());
        // 1CEStart (or another window) changes the record of Demo meanwhile.
        File.WriteAllText(List, Fixture().Replace("Version=8.3\r\n", "Version=8.3\r\nAdditionalParameters=/L ru\r\n"), new UTF8Encoding(false));
        var mine = With(library, Nordwind, x => BaseEdit.WithParameters(x, "/ClearCache", inList: true));
        Assert.Throws<ListChangedException>(() => store.Save(mine));
        var (fresh, saved) = store.SaveOverExternalChange(mine);
        Assert.Equal("/ClearCache", saved.Bases.Single(x => x.Id == Nordwind).ListParameters);
        Assert.Equal("/L ru", saved.Bases.Single(x => x.Id == Demo).ListParameters);
        var reread = new V8iStore(List).Load(new());
        Assert.Equal("/ClearCache", reread.Bases.Single(x => x.Id == Nordwind).ListParameters);
        Assert.Equal("/L ru", reread.Bases.Single(x => x.Id == Demo).ListParameters);
        Assert.Contains("Unknown=keep", File.ReadAllText(List));
        Assert.Contains("[Unsupported]\r\nConnect=Custom=\"x\";\r\nFolder=/\r\nAdditionalParameters=/Debug", File.ReadAllText(List));

        // The same record changed there too: refused, their string stays.
        library = fresh.Load(new());
        File.WriteAllText(List, File.ReadAllText(List).Replace("/ClearCache", "/Debug"), new UTF8Encoding(false));
        var again = With(library, Nordwind, x => BaseEdit.WithParameters(x, "/L en", inList: true));
        var conflict = Assert.Throws<ListConflictException>(() => fresh.SaveOverExternalChange(again));
        Assert.Contains("Nordwind", conflict.Names);
        Assert.Equal("/Debug", new V8iStore(List).Load(new()).Bases.Single(x => x.Id == Nordwind).ListParameters);
    }

    [Fact]
    public void EditInAnOldWindowKeepsAStringChangedByAnotherProgramUnlessEdited()
    {
        var original = new BaseEntry { Id = Nordwind, Name = "Nordwind", Kind = ConnectionKind.File, Location = "D:\\Bases\\Demo", ListParameters = "/L en" };
        var now = original with { ListParameters = "/L ru" };
        // Only the comment changed in the dialog: the string as it is now stays.
        var comment = BaseEdit.WithParameters(original, "/L en", inList: true) with { Comment = "Синтетика" };
        Assert.Equal("/L ru", BaseEdit.OnCurrent([now], original, comment)!.ListParameters);
        // The string changed in the dialog wins.
        var edited = BaseEdit.WithParameters(original, "/Debug", inList: true);
        Assert.Equal("/Debug", BaseEdit.OnCurrent([now], original, edited)!.ListParameters);
    }

    [Fact]
    public void OldDialogRetriedAfterAConflictKeepsParametersMovedByAnotherWindow()
    {
        // Codex audit 27.10, P2: B's dialog shows the profile string /Debug over the list's /L en; A moves /ClearCache into the list.
        Write(Fixture("AdditionalParameters=/L en\r\n"));
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        Library Settings() => LibrarySources.Select(profile.Load(), profile.PathName, List);
        profile.Save(With(new V8iStore(List).Load(new()), Nordwind, x => x with { AdditionalParameters = "/Debug" }));
        var storeB = new V8iStore(List); var libraryB = storeB.Load(Settings());
        var shown = BaseEdit.AsDialogReturns(libraryB.Bases.Single(x => x.Id == Nordwind));
        Assert.Equal("/Debug", shown.EffectiveParameters);

        var storeA = new V8iStore(List); var libraryA = storeA.Load(Settings());
        profile.Save(storeA.Save(With(libraryA, Nordwind, x => BaseEdit.WithParameters(x, "/ClearCache", inList: true)), ensureId: Nordwind));

        // B saves only a comment, the parameters field untouched: the first attempt meets A's change of the same record.
        var edited = BaseEdit.WithParameters(shown, shown.EffectiveParameters, inList: true) with { Comment = "Синтетика" };
        var first = BaseEdit.OnCurrent(libraryB.Bases, shown, edited)!;
        Assert.Equal("/Debug", first.ListParameters); // unchanged since the dialog opened, as B knew it: the string moves
        Assert.Throws<ListChangedException>(() => storeB.Save(With(libraryB, Nordwind, _ => first), ensureId: Nordwind));
        Assert.Throws<ListConflictException>(() => storeB.SaveOverExternalChange(With(libraryB, Nordwind, _ => first), ensureId: Nordwind));

        // B reads the list again and retries with the same dialog: A's string stays, the comment is saved.
        storeB = new V8iStore(List); libraryB = storeB.Load(Settings());
        var retry = BaseEdit.OnCurrent(libraryB.Bases, shown, edited)!;
        Assert.Equal("/ClearCache", retry.ListParameters); Assert.Null(retry.AdditionalParameters);
        profile.Save(storeB.Save(With(libraryB, Nordwind, _ => retry), ensureId: Nordwind));
        Assert.Contains("AdditionalParameters=/ClearCache\r\n", File.ReadAllText(List));
        var reread = new V8iStore(List).Load(Settings()).Bases.Single(x => x.Id == Nordwind);
        Assert.Equal("/ClearCache", reread.EffectiveParameters); Assert.Equal("Синтетика", reread.Comment);

        // A string typed in the old dialog wins over theirs.
        var typed = BaseEdit.OnCurrent(libraryB.Bases, shown, BaseEdit.WithParameters(shown, "/L ru", inList: true))!;
        Assert.Equal("/L ru", typed.ListParameters); Assert.Null(typed.AdditionalParameters);
    }

    [Fact]
    public void CommonListBaseKeepsItsOwnParametersInTheProfileAndTheListIsNeverWritten()
    {
        var common = Path.Combine(root, "common.v8i");
        File.WriteAllText(common, Fixture("AdditionalParameters=/L en\r\n"), new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(common);
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        var loaded = CommonLists.Load([common], false, profile.Load(), profile.PathName);
        var nordwind = loaded.Bases.Single(x => x.Entry.Id == Nordwind).Entry;
        Assert.Null(nordwind.AdditionalParameters); Assert.Equal("/L en", nordwind.EffectiveParameters);

        // Saved as the window does it: the own string under the source of the common list.
        var edited = BaseEdit.WithParameters(nordwind, "/DisableStartupMessages", inList: false);
        var settings = profile.Load();
        settings.Sources[ActivityStore.Source(profile.PathName, common)] = new SourceSettings { Bases = [edited] };
        profile.Save(settings);
        Assert.Equal(bytes, File.ReadAllBytes(common)); Assert.False(File.Exists(common + ".bak"));

        // Reopened: the string of the profile applies, the list stays as it is.
        var reopened = CommonLists.Load([common], false, profile.Load(), profile.PathName).Bases.Single(x => x.Entry.Id == Nordwind).Entry;
        Assert.Equal("/DisableStartupMessages", reopened.AdditionalParameters); Assert.Equal("/L en", reopened.ListParameters);
        Assert.Equal(new[] { "ENTERPRISE", "/S", "demo-server\\nordwind", "/DisableStartupMessages" }, Launcher.Build(Client, reopened, false).ArgumentList);
        // Other bases of the list and loading without the profile are not affected.
        Assert.Null(CommonLists.Load([common], false).Bases.Single(x => x.Entry.Id == Nordwind).Entry.AdditionalParameters);
        Assert.Equal(bytes, File.ReadAllBytes(common));
    }

    [Fact]
    public void CommonListParametersKeepTheOtherLocalSettingsOfTheBase()
    {
        // Grok audit 27.10, P1: the same file was once open as the list, so its source holds the comment, the user, the rule…
        var common = Path.Combine(root, "common.v8i");
        File.WriteAllText(common, Fixture("AdditionalParameters=/L en\r\n"), new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(common);
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        var own = new V8iStore(common).Load(new());
        var local = With(own, Nordwind, x => x with { Comment = "Синтетика", Authentication = new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = "Tester" },
            PlatformRule = new PlatformRule { Version = "8.3.27" }, BackupFolder = "D:\\Backup\\Demo", ExternalProcessing = "D:\\Bases\\Demo\\addon.epf" });
        profile.Save(LibrarySources.Select(local, profile.PathName, Path.Combine(root, "other.v8i")));
        var key = ActivityStore.Source(profile.PathName, common);
        Assert.True(profile.Load().Sources.ContainsKey(key));
        CommonBase Shown() => CommonLists.Load([common], false, profile.Load(), profile.PathName).Bases.Single(x => x.Entry.Id == Nordwind);
        void AssertKept(BaseEntry entry)
        {
            Assert.Equal("Синтетика", entry.Comment); Assert.Equal("Tester", entry.Authentication?.EnterpriseUser);
            Assert.Equal("8.3.27", entry.PlatformRule?.Version); Assert.Equal("D:\\Bases\\Demo\\addon.epf", entry.ExternalProcessing);
            Assert.Equal("D:\\Backup\\Demo", entry.BackupFolder);
        }

        // «Сохранить» with the field left as the list gives it: nothing changes, nothing is dropped.
        var settings = profile.Load();
        Assert.Same(settings, CommonLists.WithParameters(settings, profile.PathName, Shown(), null));
        // A string of its own: only AdditionalParameters of the record changes.
        profile.Save(CommonLists.WithParameters(profile.Load(), profile.PathName, Shown(), "/DisableStartupMessages"));
        var shown = Shown().Entry; AssertKept(shown);
        Assert.Equal("/DisableStartupMessages", shown.EffectiveParameters);
        // Back to the string of the list: the override goes, the other settings stay.
        profile.Save(CommonLists.WithParameters(profile.Load(), profile.PathName, Shown(), null));
        shown = Shown().Entry; AssertKept(shown);
        Assert.Null(shown.AdditionalParameters); Assert.Equal("/L en", shown.EffectiveParameters);
        Assert.Equal(bytes, File.ReadAllBytes(common)); Assert.False(File.Exists(common + ".bak"));
    }

    [Fact]
    public void CommonListBaseWithoutIdGetsNoParametersOfItsOwn()
    {
        // Grok audit 27.10, P2: without ID= the base is known by a hash of its text, which another program changes.
        var common = Path.Combine(root, "common.v8i");
        File.WriteAllText(common, "[Nordwind]\r\nConnect=File=\"D:\\Bases\\Demo\";\r\nFolder=/\r\n", new UTF8Encoding(false));
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        var base0 = Assert.Single(CommonLists.Load([common], false, profile.Load(), profile.PathName).Bases);
        Assert.False(base0.PersistentId);
        var settings = profile.Load();
        var error = Assert.Throws<InvalidOperationException>(() => CommonLists.WithParameters(settings, profile.PathName, base0, "/Debug"));
        Assert.Contains("нет постоянного ID", error.Message);
        // Leaving the field as the list gives it is no change and is not refused.
        Assert.Same(settings, CommonLists.WithParameters(settings, profile.PathName, base0, null));
    }

    [Fact]
    public void LaunchUsesTheStringOfTheListAndItsErrorNamesTheField()
    {
        var store = Write(Fixture("AdditionalParameters=/L ru /C \"проверка\"\r\n"));
        var nordwind = store.Load(new()).Bases.Single(x => x.Id == Nordwind);
        Assert.Equal(new[] { "ENTERPRISE", "/S", "demo-server\\nordwind", "/L", "ru", "/C", "проверка" }, Launcher.Build(Client, nordwind, false).ArgumentList);
        var invalid = nordwind with { ListParameters = "/N user /Psecret" };
        var error = Assert.Throws<ArgumentException>(() => Launcher.Build(Client, invalid, false));
        Assert.Contains("«Дополнительные параметры»", error.Message);
        Assert.DoesNotContain("флажок", error.Message); Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("/UC 123", true)]
    [InlineData("/Debug /uc \"код доступа\"", true)]
    [InlineData("/UCкод", true)]
    [InlineData("/C /UC", false)]
    [InlineData("/C \"/UC 123\"", false)]
    [InlineData("/Debug", false)]
    [InlineData("/C \"unclosed", false)]
    public void AccessCodeIsFoundOnlyAsAKey(string text, bool expected) => Assert.Equal(expected, LaunchParameters.HasAccessCode(text));
}
