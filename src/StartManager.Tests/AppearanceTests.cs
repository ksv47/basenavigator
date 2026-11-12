using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class AppearanceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "appearance-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "library.json");
    public AppearanceTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void OldSettingsLoadDefaultsAndLocalAndSharedListsRetainAppearance()
    {
        File.WriteAllText(Settings, "{\"Version\":1}");
        var store = new LibraryStore(Settings); var original = store.Load();
        Assert.Equal(13, original.Appearance.List.Size);
        Assert.Equal(11, original.Appearance.Interface.Size);
        Assert.Equal(12, original.Appearance.Headers.Size);
        Assert.Equal(AppearanceSettings.DefaultDialogSize, original.Appearance.Dialogs.Size);
        Assert.Equal(12, AppearanceSettings.DefaultDialogSize);
        var list = Path.Combine(directory, "shared.v8i");
        File.WriteAllText(list, $"[Example]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={Guid.NewGuid()}\nUnknown=keep\n");
        var bytes = File.ReadAllBytes(list);
        var appearance = new AppearanceSettings { Theme = AppearanceTheme.Dark, List = new() { Family = "Verdana", Size = 20 } };
        var shared = new V8iStore(list).Load(LibrarySources.Select(original with { Appearance = appearance }, Settings, list));
        store.Save(shared);
        var reopened = store.Load();
        Assert.Equal(appearance, reopened.Appearance);
        Assert.Equal(appearance, LibrarySources.Select(reopened, Settings, "").Appearance);
        Assert.Equal(bytes, File.ReadAllBytes(list));
    }

    [Fact]
    public void SavedDialogSizeOfEarlierBuildsIsKeptAndOnlyResetTakesTheNewDefault()
    {
        // 14 was the default before 11.11.2026: a profile that saved it keeps it through load and save, no migration.
        File.WriteAllText(Settings, """{"Version":1,"Appearance":{"Dialogs":{"Family":"","Size":14}}}""");
        var store = new LibraryStore(Settings); var loaded = store.Load();
        Assert.Equal(14, loaded.Appearance.Dialogs.Size);
        store.Save(loaded);
        Assert.Equal(14, store.Load().Appearance.Dialogs.Size);
        Assert.Equal(AppearanceSettings.DefaultDialogSize, loaded.Appearance.Reset().Dialogs.Size);
        // A profile without a dialog size, or without appearance at all, gets the new default.
        File.WriteAllText(Settings, """{"Version":1,"Appearance":{"Dialogs":{"Family":"Verdana"}}}""");
        Assert.Equal(AppearanceSettings.DefaultDialogSize, store.Load().Appearance.Dialogs.Size);
    }

    [Fact]
    public void UnknownAppearanceParametersSurviveSaveAndReset()
    {
        File.WriteAllText(Settings, """{"Appearance":{"Future":{"x":1},"List":{"Family":"missing","Size":18,"FutureWeight":600}}}""");
        var store = new LibraryStore(Settings); var original = store.Load();
        store.Save(original with { Appearance = original.Appearance.Reset() });
        var loaded = store.Load();
        Assert.Equal(1, loaded.Appearance.Extra!["Future"].GetProperty("x").GetInt32());
        Assert.Equal(600, loaded.Appearance.List.Extra!["FutureWeight"].GetInt32());
        Assert.Equal("", loaded.Appearance.List.Family);
    }

    [Fact]
    public void ConflictingAppearanceSaveDoesNotOverwriteExternalChanges()
    {
        var store = new LibraryStore(Settings); var initial = store.Load(); store.Save(initial);
        var other = new LibraryStore(Settings); var changed = other.Load();
        other.Save(changed with { Executable = "external-client" }); var bytes = File.ReadAllBytes(Settings);
        Assert.Throws<IOException>(() => store.Save(initial with { Appearance = new() { Theme = AppearanceTheme.Dark } }));
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
    }

    [Theory]
    [InlineData(9)] [InlineData(25)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidFontSizes(double size) => Assert.Throws<InvalidDataException>(() =>
        LibraryStore.Validate(new Library { Appearance = new() { List = new() { Size = size } } }));

    [Theory]
    [InlineData(19)] [InlineData(65)] [InlineData(double.NaN)]
    public void RejectsInvalidRowHeight(double size) => Assert.Throws<InvalidDataException>(() =>
        LibraryStore.Validate(new Library { Appearance = new() { MinimumRowHeight = size } }));

    [Fact]
    public void RejectsInvalidThemeNullRolesAndFontNames()
    {
        Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { Appearance = null! }));
        Assert.Throws<InvalidDataException>(() => new AppearanceSettings { Theme = (AppearanceTheme)99 }.Validate());
        foreach (var appearance in new[] { new AppearanceSettings { List = null! }, new AppearanceSettings { Interface = null! },
                     new AppearanceSettings { Headers = null! }, new AppearanceSettings { Dialogs = null! } })
            Assert.Throws<InvalidDataException>(appearance.Validate);
        foreach (var name in new[] { null, "bad\nfont", new string('x', 257) })
            Assert.Throws<InvalidDataException>(() => new FontPreference { Family = name! }.Validate());
    }

    [Fact]
    public void MissingFontUsesHostFallbackWithoutChangingStoredPreference()
    {
        var preference = new FontPreference { Family = "Verdana", Size = 18 };
        Assert.Equal("DejaVu Sans", FontSelection.Resolve(preference.Family, ["Liberation Sans", "DejaVu Sans"], false, "default"));
        Assert.Equal("Verdana", preference.Family);
        Assert.Equal("Verdana", FontSelection.Resolve("verdana", ["Verdana", "VERDANA"], true, "default"));
        Assert.Equal("Segoe UI", FontSelection.Resolve("", ["DejaVu Sans", "Segoe UI"], true, "default"));
        Assert.Equal("default", FontSelection.Resolve("missing", [], false, "default"));
    }

    [Theory]
    [InlineData("{\"List\":{\"Size\":26}}")]
    [InlineData("{\"Theme\":7}")]
    [InlineData("{\"MinimumRowHeight\":100}")]
    [InlineData("{\"List\":null}")]
    [InlineData("{\"Interface\":{\"Family\":null}}")]
    [InlineData("{\"Headers\":{\"Size\":\"bad\"}}")]
    [InlineData("{\"Dialogs\":{\"Family\":\"bad\\nname\"}}")]
    [InlineData("{\"Theme\":\"future\"}")]
    [InlineData("{\"MinimumRowHeight\":1e999}")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void UnsupportedCosmeticDataDoesNotBlockBasesAndSurvivesUnrelatedSave(string appearance)
    {
        var entry = new BaseEntry { Name = "Keep base", Location = directory };
        File.WriteAllText(Settings, "{\"Bases\":[" + JsonSerializer.Serialize(entry) + "],\"Appearance\":" + appearance + "}");
        var store = new LibraryStore(Settings); var library = store.Load();
        Assert.Equal(entry, Assert.Single(library.Bases));
        Assert.NotNull(library.Appearance.LoadWarning);
        LibraryStore.Validate(library);
        store.Save(library with { Executable = "new-client" });
        using (var json = JsonDocument.Parse(File.ReadAllText(Settings)))
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(appearance).RootElement, json.RootElement.GetProperty("Appearance")));
        var refreshed = store.Load();
        store.Save(refreshed with { Appearance = refreshed.Appearance.Reset() });
        var recovered = store.Load();
        Assert.Null(recovered.Appearance.LoadWarning);
        Assert.Equal(entry, Assert.Single(recovered.Bases));
        Assert.Equal("new-client", recovered.Executable);
    }

    [Fact]
    public void PartialListUsesListDefaultAndMixedCaseKnownKeysAreCanonicalized()
    {
        File.WriteAllText(Settings, """{"appearance":{"list":{"family":"Verdana"}}}""");
        Assert.Equal(AppearanceSettings.DefaultListSize, new LibraryStore(Settings).Load().Appearance.List.Size);
        File.WriteAllText(Settings, """{"FutureRoot":{"keep":true},"appearance":{"theme":2,"list":{"size":22,"futureWeight":600}}}""");
        var store = new LibraryStore(Settings); var library = store.Load();
        Assert.Equal(AppearanceTheme.Dark, library.Appearance.Theme);
        Assert.Equal(22, library.Appearance.List.Size);
        store.Save(library);
        using var json = JsonDocument.Parse(File.ReadAllText(Settings));
        Assert.True(json.RootElement.GetProperty("FutureRoot").GetProperty("keep").GetBoolean());
        var saved = json.RootElement.GetProperty("Appearance");
        Assert.False(saved.TryGetProperty("theme", out _));
        Assert.False(saved.GetProperty("List").TryGetProperty("size", out _));
        Assert.Equal(600, saved.GetProperty("List").GetProperty("futureWeight").GetInt32());
    }
}
