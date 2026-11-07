using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class QuickAccessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BaseNavigator-quick-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "library.json");
    public QuickAccessTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static BaseEntry Entry() => new() { Name = "Тест", Kind = ConnectionKind.Server, Location = "server\\base" };

    [Fact]
    public void FavoritesAreSourceScopedAndMergeBetweenInstances()
    {
        var a = new ActivityStore(Settings); var b = new ActivityStore(Settings); var id = Guid.NewGuid();
        Assert.Empty(a.Load().Favorites);
        a.SetFavorite("one", id, true);
        b.SetFavorite("two", id, true);
        var data = a.SetFavorite("one", id, false);
        Assert.Equal(new Favorite("two", id), Assert.Single(data.Favorites));
        Assert.False(File.Exists(Settings));
        Assert.True(File.Exists(a.PathName + ".bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void AnEditMadeDuringTheWriteIsKept()
    {
        var store = new ActivityStore(Settings); var mine = Guid.NewGuid(); var theirs = Guid.NewGuid();
        store.SetFavorite("one", Guid.NewGuid(), true);
        var other = Path.Combine(directory, "other", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        var external = new ActivityStore(other); external.SetFavorite("one", theirs, true);
        var edited = false;
        store.BeforeWrite = () => { if (!edited) { edited = true; File.Copy(external.PathName, store.PathName, true); } };
        var data = store.SetFavorite("one", mine, true);
        Assert.True(edited);
        Assert.Contains(new Favorite("one", theirs), data.Favorites); Assert.Contains(new Favorite("one", mine), data.Favorites);
        Assert.Equal(2, new ActivityStore(Settings).Load().Favorites.Count);
    }

    [Fact]
    public void ToggleDecidesByTheFileUnderTheLock()
    {
        var a = new ActivityStore(Settings); var b = new ActivityStore(Settings); var id = Guid.NewGuid();
        Assert.True(a.ToggleFavorite("one", id, false).Enabled);
        // Another window has already marked it: the toggle of this window removes it, as the file says.
        var result = b.ToggleFavorite("one", id, false);
        Assert.False(result.Enabled); Assert.Empty(result.Activity.Favorites);
        Assert.False(a.ToggleFavorite("one", id, removeOnly: true).Enabled);
    }

    [Fact]
    public void ProfileLinkStaysAndItsTargetIsWritten()
    {
        var real = Path.Combine(directory, "real", "library.json"); Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        new LibraryStore(real).Save(new Library());
        try { File.CreateSymbolicLink(Settings, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // Windows without the right to create links.
        var store = new LibraryStore(Settings); var library = store.Load();
        store.Save(library with { Executable = "/opt/1cv8/x86_64/8.3.27.2214/1cv8" });
        Assert.NotNull(new FileInfo(Settings).LinkTarget);
        Assert.Contains("8.3.27.2214", File.ReadAllText(real));
    }

    [Fact]
    public void CorruptionAndFutureVersionAreNeverOverwritten()
    {
        var store = new ActivityStore(Settings);
        foreach (var text in new[] { "broken", "null", "{\"Version\":2}", "{\"Favorites\":null}", "{\"History\":[null]}", "{\"Favorites\":[null]}" })
        {
            File.WriteAllText(store.PathName, text);
            Assert.ThrowsAny<Exception>(() => store.SetFavorite("one", Guid.NewGuid(), true));
            Assert.Equal(text, File.ReadAllText(store.PathName));
        }
    }

    [Fact]
    public void ExtensionFieldsSurviveAndHistoryIsBounded()
    {
        var store = new ActivityStore(Settings);
        File.WriteAllText(store.PathName, "{\"future\":{\"x\":42}}");
        var entry = Entry();
        for (var i = 0; i < 103; i++) store.Record("list", entry with { Name = "Base " + i }, i % 2 == 0);
        var data = store.Load();
        Assert.Equal(100, data.History.Count);
        Assert.Equal("Base 102", data.History[0].Name);
        Assert.Equal("Base 3", data.History[^1].Name);
        Assert.True(data.History[0].Designer);
        Assert.Equal(42, data.Extra!["future"].GetProperty("x").GetInt32());
    }

    [Fact]
    public void ExclusiveWriterDoesNotSilentlyOverwrite()
    {
        var store = new ActivityStore(Settings);
        store.SetFavorite("one", Guid.NewGuid(), true);
        var before = File.ReadAllText(store.PathName);
        using (var gate = new FileStream(store.PathName + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Record("one", Entry(), false));
        Assert.Equal(before, File.ReadAllText(store.PathName));
    }

    [Fact]
    public void SourcesSeparateSettingsFromSharedListsAndRespectPlatformCase()
    {
        Assert.NotEqual(ActivityStore.Source(Settings, ""), ActivityStore.Source("ignored", Settings));
        Assert.Equal(ActivityStore.Source(Settings, ""), ActivityStore.Source(Path.Combine(directory, ".", "library.json"), ""));
        Assert.Equal(OperatingSystem.IsWindows(), ActivityStore.Source(Settings, "") == ActivityStore.Source(Settings.ToUpperInvariant(), ""));
    }

    [Fact]
    public void ShortcutRoundTripsAndResolvesExactSourceWithoutChangingSettings()
    {
        var entry = Entry(); var store = new LibraryStore(Settings);
        store.Save(new Library { Bases = [entry] });
        var request = new ShortcutRequest(Settings, "", entry.Id, true);
        Assert.Equal(request, ShortcutRequest.Parse(request.Arguments()));
        Assert.Equal(entry, request.Resolve().Entry);
        Assert.Null(ShortcutRequest.Parse(["--settings", Settings]));
        Assert.Throws<InvalidOperationException>(() => (request with { Id = Guid.NewGuid() }).Resolve());
        var list = Path.Combine(directory, "shared.v8i");
        File.WriteAllText(list, $"[Shared]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={entry.Id}\n");
        var before = File.ReadAllText(Settings);
        Assert.Equal("Shared", (request with { List = list }).Resolve().Entry.Name);
        Assert.Equal(before, File.ReadAllText(Settings));
        store.Save(new Library { Bases = [entry], ListPath = list });
        Assert.Throws<InvalidOperationException>(() => request.Resolve());
        Assert.Throws<FileNotFoundException>(() => (request with { Settings = Path.Combine(directory, "missing") }).Resolve());
    }

    [Fact]
    public void ShortcutNeverBorrowsPerBaseClientFromAnotherSource()
    {
        var entry = Entry() with { Executable = "client-from-other-list" };
        var oldList = Path.Combine(directory, "old.v8i");
        var newList = Path.Combine(directory, "new.v8i");
        var contents = $"[Shared]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={entry.Id}\n";
        File.WriteAllText(oldList, contents); File.WriteAllText(newList, contents);
        new LibraryStore(Settings).Save(new Library { ListPath = newList, Executable = "global-client", Bases = [entry] });
        var oldRequest = new ShortcutRequest(Settings, oldList, entry.Id, false);
        var result = oldRequest.Resolve();
        Assert.Empty(result.Entry.Executable);
        Assert.Equal("global-client", result.Library.Executable);
        Assert.Equal(entry.Executable, (oldRequest with { List = newList }).Resolve().Entry.Executable);
    }

    [Fact]
    public void MalformedShortcutNeverFallsBackToAnotherBase()
    {
        var valid = new ShortcutRequest(Settings, "", Guid.NewGuid(), false).Arguments();
        foreach (var args in new[] { new[] { "--launch" }, valid.Concat(new[] { "--bad", "x" }).ToArray(), valid.Concat(new[] { "--mode", "designer" }).ToArray() })
            Assert.Throws<ArgumentException>(() => ShortcutRequest.Parse(args));
        foreach (var pair in new[] { (1, "relative.json"), (3, "relative.v8i"), (5, "not-a-guid"), (7, "wrong") })
        {
            var args = valid.ToArray(); args[pair.Item1] = pair.Item2;
            Assert.Throws<ArgumentException>(() => ShortcutRequest.Parse(args));
        }
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("C:\\folder\\", "\"C:\\folder\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void WindowsArgumentsAreQuoted(string value, string expected) => Assert.Equal(expected, ShortcutRequest.WindowsQuote(value));

    [Fact]
    public void DesktopArgumentsEscapeBothDesktopAndExecLayers()
    {
        var result = ShortcutRequest.DesktopExec("/opt/app", ["", "a b", "$HOME", "%f", "a\\b"]);
        Assert.Equal("\"/opt/app\" \"\" \"a b\" \"\\\\$HOME\" \"%%f\" \"a\\\\\\\\b\"", result);
        Assert.Throws<ArgumentException>(() => ShortcutRequest.DesktopExec("/a=b", []));
        Assert.Throws<ArgumentException>(() => ShortcutRequest.DesktopExec("/app", ["bad\nline"]));
    }

    [Fact]
    public void EmptyGuidIsAValidExistingIdentityAndDoesNotPoisonActivity()
    {
        var entry = Entry() with { Id = Guid.Empty };
        new LibraryStore(Settings).Save(new Library { Bases = [entry] });
        var activity = new ActivityStore(Settings);
        activity.SetFavorite("list", entry.Id, true);
        activity.Record("list", entry, false);
        Assert.Equal(Guid.Empty, Assert.Single(activity.Load().Favorites).Id);
        Assert.Equal(Guid.Empty, Assert.Single(activity.Load().History).Id);
        var request = new ShortcutRequest(Settings, "", Guid.Empty, false);
        Assert.Equal(request, ShortcutRequest.Parse(request.Arguments()));
        Assert.Equal(entry, request.Resolve().Entry);
    }

    [Fact]
    public void InvalidMutationCannotPoisonExistingData()
    {
        var store = new ActivityStore(Settings);
        store.SetFavorite("valid", Guid.NewGuid(), true);
        var before = File.ReadAllText(store.PathName);
        Assert.Throws<InvalidDataException>(() => store.SetFavorite("", Guid.NewGuid(), true));
        Assert.Throws<InvalidDataException>(() => store.Record("valid", Entry() with { Name = null! }, false));
        Assert.Equal(before, File.ReadAllText(store.PathName));
        Assert.Single(store.Load().Favorites);
    }

    [Fact]
    public void UnknownFieldsInsideRecordsSurviveUnrelatedAndIdempotentWrites()
    {
        var store = new ActivityStore(Settings);
        var id = Guid.NewGuid();
        File.WriteAllText(store.PathName, $$"""{"Favorites":[{"Source":"list","Id":"{{id}}","Colour":"red"}],"History":[{"Source":"list","Id":"{{id}}","Name":"old","Time":"2026-09-20T00:00:00Z","Future":17}]}""");
        store.SetFavorite("list", id, true);
        var data = store.Record("list", Entry(), true);
        Assert.Equal("red", data.Favorites[0].Extra!["Colour"].GetString());
        var reloaded = store.Load();
        Assert.Equal(17, reloaded.History[1].Extra!["Future"].GetInt32());
        Assert.True(reloaded.History[1].PersistentIdentity);
    }

    [Fact]
    public void MissingV8iIdCannotCreateAnUnstableShortcutAndSaveMakesItPersistent()
    {
        var path = Path.Combine(directory, "no-id.v8i");
        File.WriteAllText(path, "[Original]\nConnect=Srvr=\"server\";Ref=\"base\";\n");
        var source = new V8iStore(path);
        var library = source.Load(new()); var entry = Assert.Single(library.Bases);
        new LibraryStore(Settings).Save(library);
        Assert.False(source.HasPersistentId(entry.Id));
        var request = new ShortcutRequest(Settings, path, entry.Id, false);
        Assert.Throws<InvalidOperationException>(() => request.Resolve());
        var history = new ActivityStore(Settings).Record("list", entry, false, source.HasPersistentId(entry.Id));
        Assert.False(history.History[0].PersistentIdentity);
        var before = File.ReadAllBytes(path);
        source.Save(library);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(source.HasPersistentId(entry.Id));
        Assert.Throws<InvalidOperationException>(() => source.Save(library, ensureId: Guid.NewGuid()));
        Assert.Equal(before, File.ReadAllBytes(path));
        source.Save(library, ensureId: entry.Id);
        Assert.True(source.HasPersistentId(entry.Id));
        Assert.Equal(entry.Id, request.Resolve().Entry.Id);
        File.WriteAllText(path, File.ReadAllText(path).Replace("[Original]", "[Renamed]"));
        Assert.Equal(entry.Id, request.Resolve().Entry.Id);
    }

    [Fact]
    public async Task BriefLockContentionIsRetried()
    {
        var store = new ActivityStore(Settings);
        var gate = new FileStream(store.PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var started = new ManualResetEventSlim();
        var writer = Task.Run(() => { started.Set(); return store.SetFavorite("list", Guid.NewGuid(), true); });
        started.Wait();
        await Task.Delay(30); gate.Dispose();
        Assert.Single((await writer).Favorites);
    }

    [Fact]
    public void JsonErrorsHaveRussianMessageAndPreserveOriginal()
    {
        var store = new ActivityStore(Settings);
        var invalid = "{\"History\":[{\"Time\":\"invalid\"}]}";
        File.WriteAllText(store.PathName, invalid);
        var ex = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains("некорректный JSON", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
        Assert.Equal(invalid, File.ReadAllText(store.PathName));
    }

    [Fact]
    public void ListViewIsSourceScopedAndKeepsOtherData()
    {
        var store = new ActivityStore(Settings); var id = Guid.NewGuid();
        File.WriteAllText(store.PathName, "{\"future\":1,\"Views\":[{\"Source\":\"one\",\"Selected\":\"old\",\"x\":7}]}");
        store.SetFavorite("one", id, true);
        store.SaveView(new ListView("one") { Selected = id.ToString(), Collapsed = ["A", "A/B"], CollapsedSections = ["Favorites"] });
        store.SaveView(new ListView("two") { Selected = "group:C" });
        var data = new ActivityStore(Settings).Load();
        var one = data.Views.Single(x => x.Source == "one");
        Assert.Equal(id.ToString(), one.Selected);
        Assert.Equal(["A", "A/B"], one.Collapsed);
        Assert.Equal(["Favorites"], one.CollapsedSections);
        Assert.Equal(7, one.Extra!["x"].GetInt32());
        Assert.Equal("group:C", data.Views.Single(x => x.Source == "two").Selected);
        Assert.Single(data.Favorites);
        Assert.Equal(1, data.Extra!["future"].GetInt32());
    }

    [Fact]
    public void DamagedViewDoesNotBlockFavorites()
    {
        var store = new ActivityStore(Settings);
        File.WriteAllText(store.PathName, "{\"Views\":[null,{\"Source\":\"\"},{\"Source\":\"one\",\"Collapsed\":null},{\"Source\":\"two\"}]}");
        Assert.Equal("two", Assert.Single(store.Load().Views).Source);
        store.SetFavorite("one", Guid.NewGuid(), true);
        File.WriteAllText(store.PathName, "{\"Views\":null}");
        Assert.Empty(store.Load().Views);
    }

    [Theory]
    [InlineData("{}")] [InlineData("42")] [InlineData("[42]")] [InlineData("[{\"Source\":\"one\",\"Collapsed\":42}]")]
    [InlineData("[{\"Source\":\"one\",\"Selected\":{}}]")] [InlineData("[{\"Source\":\"one\",\"Collapsed\":[2]}]")]
    [InlineData("[{\"Source\":\"one\",\"Collapsed\":[null]}]")] [InlineData("[{\"Source\":\"one\",\"CollapsedSections\":\"All\"}]")]
    public void WrongViewTypesKeepFavoritesHistoryAndOtherViews(string views)
    {
        var store = new ActivityStore(Settings); var id = Guid.NewGuid();
        var neighbour = views.StartsWith('[') ? views[..^1] + ",{\"Source\":\"two\",\"Selected\":\"x\"}]" : views;
        File.WriteAllText(store.PathName, "{\"Version\":1,\"Favorites\":[{\"Source\":\"one\",\"Id\":\"" + id + "\"}]," +
            "\"History\":[{\"Source\":\"one\",\"Id\":\"" + id + "\",\"Name\":\"Base\",\"Designer\":false,\"Time\":\"2026-09-24T00:00:00Z\"}],\"Views\":" + neighbour + "}");
        var data = store.Load();
        Assert.Equal(id, Assert.Single(data.Favorites).Id);
        Assert.Equal("Base", Assert.Single(data.History).Name);
        Assert.DoesNotContain(data.Views, x => x.Source == "one");
        Assert.Equal(views.StartsWith('['), data.Views.Any(x => x.Source == "two" && x.Selected == "x"));
        data = store.SaveView(new ListView("one") { Selected = id.ToString() });
        Assert.Equal(id.ToString(), data.Views.Single(x => x.Source == "one").Selected);
        Assert.Single(store.Load().History);
    }
}
