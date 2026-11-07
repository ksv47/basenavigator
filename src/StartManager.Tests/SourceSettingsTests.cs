using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class SourceSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "source-settings-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private string Settings => Path.Combine(directory, "library.json");
    public SourceSettingsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string List(string name)
    {
        var path = Path.Combine(directory, name + ".v8i");
        File.WriteAllText(path, $"[{name}]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={id}\nUnknown=keep\n");
        return path;
    }
    private Library Open(Library library, string path) => new V8iStore(path).Load(LibrarySources.Select(library, Settings, path));

    [Fact]
    public void UnknownSourceAndSharedBasePreferencesSurviveReloadAndSave()
    {
        var path = List("Unknown");
        var store = new LibraryStore(Settings);
        store.Save(Open(new Library(), path));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!;
        var sourceKey = ActivityStore.Source(Settings, path);
        json["Sources"]![sourceKey]!["futureSource"] = "preserve-source";
        json["Bases"]![0]!["futureBase"] = "preserve-base";
        File.WriteAllText(Settings, json.ToJsonString());
        var loaded = store.Load();
        store.Save(Open(loaded, path));
        var result = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal("preserve-source", result["Sources"]![sourceKey]!["futureSource"]?.GetValue<string>());
        Assert.Equal("preserve-base", result["Bases"]![0]!["futureBase"]?.GetValue<string>());
    }

    [Fact]
    public void LegacyLocalListSurvivesSwitchAndRepeatedSavesAndShortcutResolvesIt()
    {
        var entry = new BaseEntry { Name = "local", Location = directory, Executable = "local-client", Group = "local-group" };
        var local = new Library { Bases = [entry], Groups = ["local-group", "empty"] };
        File.WriteAllText(Settings, System.Text.Json.JsonSerializer.Serialize(local));
        var store = new LibraryStore(Settings);
        var shared = Open(store.Load(), List("A"));
        store.Save(shared);
        store.Save(shared with { Executable = "global-client" });
        store.Save(store.Load());
        var restored = LibrarySources.Select(store.Load(), Settings, "");
        Assert.Equal(entry, Assert.Single(restored.Bases));
        Assert.Equal(local.Groups, restored.Groups);
        Assert.Equal(entry, new ShortcutRequest(Settings, "", entry.Id, false).Resolve().Entry);
    }

    [Fact]
    public void ClientsStayWithTheirSourceAcrossRestartAndIdenticalIds()
    {
        var a = List("A"); var b = List("B");
        var original = File.ReadAllBytes(a);
        var store = new LibraryStore(Settings);
        var first = Open(new Library(), a);
        first = first with { Bases = [first.Bases[0] with { Executable = "client-A" }] };
        store.Save(first);
        var second = Open(store.Load(), b);
        Assert.Empty(second.Bases[0].Executable);
        second = second with { Bases = [second.Bases[0] with { Executable = "client-B" }] };
        store.Save(second);
        Assert.Equal("client-A", Open(store.Load(), a).Bases[0].Executable);
        Assert.Equal("client-A", new ShortcutRequest(Settings, a, id, false).Resolve().Entry.Executable);
        Assert.Equal("client-B", new ShortcutRequest(Settings, b, id, false).Resolve().Entry.Executable);
        Assert.Equal(original, File.ReadAllBytes(a));
    }

    [Fact]
    public void ReopeningAfterLoadErrorPreservesClientAndFailedSwitchDoesNotWriteSettings()
    {
        var a = List("A"); var source = new V8iStore(a);
        var library = source.Load(new Library());
        library = library with { Bases = [library.Bases[0] with { Executable = "client-A" }] };
        var store = new LibraryStore(Settings); store.Save(library);
        var settingsBefore = File.ReadAllBytes(Settings); var good = File.ReadAllText(a);
        File.AppendAllText(a, good.Replace("[A]", "[Duplicate]"));
        Assert.Throws<InvalidDataException>(() => source.Load(library));
        Assert.Throws<InvalidDataException>(() => Open(library, a));
        Assert.Equal(settingsBefore, File.ReadAllBytes(Settings));
        File.WriteAllText(a, good);
        Assert.Equal("client-A", Open(library, a).Bases[0].Executable);
    }

    [Fact]
    public void ClearingClientDoesNotRestoreArchivedValue()
    {
        var a = List("A"); var b = List("B"); var store = new LibraryStore(Settings);
        var library = Open(new Library(), a);
        store.Save(library with { Bases = [library.Bases[0] with { Executable = "old" }] });
        library = store.Load();
        library = library with { Bases = [library.Bases[0] with { Executable = "" }] };
        store.Save(Open(library, b));
        Assert.Empty(Open(store.Load(), a).Bases[0].Executable);
    }

    [Fact]
    public void InvalidArchivedSettingsAreRejectedBeforeOverwrite()
    {
        var store = new LibraryStore(Settings); store.Save(new Library());
        var before = File.ReadAllBytes(Settings);
        Assert.Throws<InvalidDataException>(() => store.Save(new Library { Sources = new() { ["local:bad"] = new() { Bases = null! } } }));
        Assert.Equal(before, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void RefreshAllowsSaveAfterConflictWithoutLosingArchive()
    {
        var first = new LibraryStore(Settings); var second = new LibraryStore(Settings);
        first.Save(new Library()); var stale = second.Load();
        first.Save(Open(first.Load(), List("A")));
        Assert.Throws<IOException>(() => second.Save(stale));
        second.Save(second.Load() with { Executable = "updated" });
        Assert.Equal(2, second.Load().Sources.Count);
    }

    [Fact]
    public void CorruptOnDiskSettingsCannotBeOverwrittenAfterFailedLoad()
    {
        File.WriteAllText(Settings, "{broken");
        var store = new LibraryStore(Settings);
        Assert.IsType<System.Text.Json.JsonException>(Assert.Throws<InvalidDataException>(() => store.Load()).InnerException);
        Assert.Throws<InvalidDataException>(() => store.Save(new Library()));
        Assert.Equal("{broken", File.ReadAllText(Settings));
        Assert.False(File.Exists(Settings + ".bak"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public void MalformedLegacySourceCanSwitchWithoutDiscardingItsSnapshot(string path)
    {
        var entry = new BaseEntry { Name = "legacy", Location = directory };
        var library = new Library { ListPath = path, Bases = [entry] };
        File.WriteAllText(Settings, System.Text.Json.JsonSerializer.Serialize(library));
        var store = new LibraryStore(Settings);
        store.Save(Open(store.Load(), List("A")));
        Assert.Contains(store.Load().Sources.Values, x => x.Bases.Any(b => b.Id == entry.Id));
    }

    [Fact]
    public void OverlongLegacySourceCanSwitchWithoutDiscardingItsSnapshot()
        => MalformedLegacySourceCanSwitchWithoutDiscardingItsSnapshot(new string('x', 40000));

    [Fact]
    public void StaleInstanceCannotEraseAnotherInstancesArchivedSource()
    {
        var first = new LibraryStore(Settings); var second = new LibraryStore(Settings);
        first.Save(new Library());
        var current = first.Load(); var stale = second.Load();
        first.Save(Open(current, List("A")));
        var before = File.ReadAllBytes(Settings);
        Assert.Throws<IOException>(() => second.Save(stale with { Executable = "changed elsewhere" }));
        Assert.Equal(before, File.ReadAllBytes(Settings));
    }
}
