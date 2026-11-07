using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class GeneralSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "general-settings-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "library.json");
    public GeneralSettingsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void LegacyProfileKeepsPreviousLaunchBehavior()
    {
        File.WriteAllText(Settings, "{\"future\":{\"value\":42}}");
        var store = new LibraryStore(Settings);
        var library = store.Load();
        Assert.Equal(AfterLaunchAction.KeepWindow, library.AfterLaunch);
        Assert.Equal(DoubleClickAction.Enterprise, library.DoubleClick);
        store.Save(library with { AfterLaunch = AfterLaunchAction.Minimize, DoubleClick = DoubleClickAction.Designer });
        var restored = new LibraryStore(Settings).Load();
        Assert.Equal(AfterLaunchAction.Minimize, restored.AfterLaunch);
        Assert.Equal(DoubleClickAction.Designer, restored.DoubleClick);
        Assert.Equal(42, restored.Extra!["future"].GetProperty("value").GetInt32());
    }

    [Fact]
    public void GeneralSettingsSurviveSourceSwitchWithoutChangingV8i()
    {
        var path = Path.Combine(directory, "shared.v8i");
        File.WriteAllText(path, "[Shared]\nConnect=Srvr=\"server\";Ref=\"base\";\nUnknown=keep\n");
        var before = File.ReadAllBytes(path);
        var store = new LibraryStore(Settings);
        var original = new Library { AfterLaunch = AfterLaunchAction.Minimize, DoubleClick = DoubleClickAction.Designer };
        store.Save(original);
        store.Save(new V8iStore(path).Load(LibrarySources.Select(store.Load(), Settings, path)));
        store.Save(LibrarySources.Select(store.Load(), Settings, ""));
        Assert.Equal(original.AfterLaunch, store.Load().AfterLaunch);
        Assert.Equal(original.DoubleClick, store.Load().DoubleClick);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("AfterLaunch")]
    [InlineData("DoubleClick")]
    public void UnknownActionIsRejectedWithoutOverwritingProfile(string property)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, int> { [property] = 99 });
        File.WriteAllBytes(Settings, bytes);
        var store = new LibraryStore(Settings);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new Library()));
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void CloseAfterLaunchPersistsWithoutLosingOtherPreferences()
    {
        var store = new LibraryStore(Settings);
        var library = new Library { AfterLaunch = AfterLaunchAction.Close, DoubleClick = DoubleClickAction.Designer,
            Extra = new() { ["Future"] = JsonSerializer.SerializeToElement(42) } };
        store.Save(library);
        var loaded = new LibraryStore(Settings).Load();
        Assert.Equal(AfterLaunchAction.Close, loaded.AfterLaunch);
        Assert.Equal(DoubleClickAction.Designer, loaded.DoubleClick);
        Assert.Equal(42, loaded.Extra!["Future"].GetInt32());
    }

    [Fact]
    public void ConcurrentSaveDoesNotLoseGeneralSettings()
    {
        var first = new LibraryStore(Settings); first.Save(new Library());
        var second = new LibraryStore(Settings); var stale = second.Load();
        first.Save(first.Load() with { AfterLaunch = AfterLaunchAction.Minimize });
        Assert.Throws<IOException>(() => second.Save(stale with { DoubleClick = DoubleClickAction.Designer }));
        Assert.Equal(AfterLaunchAction.Minimize, first.Load().AfterLaunch);
        Assert.Equal(DoubleClickAction.Enterprise, first.Load().DoubleClick);
    }
}
