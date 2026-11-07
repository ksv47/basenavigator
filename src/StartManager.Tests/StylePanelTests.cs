using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// The switches of the original's style panel: «Интенсивность выделения», «Отображать всплывающие подсказки», «Отображать группы сверху».
public sealed class StylePanelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-style-" + Guid.NewGuid().ToString("N"));
    public StylePanelTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact] public void DefaultsKeepTheListAsItWas()
    {
        var settings = new AppearanceSettings();
        Assert.Equal(128, settings.SelectionIntensity);
        Assert.True(settings.ListTooltips);
        Assert.True(settings.GroupsOnTop);
        // 128 is the selection colour used before the setting existed.
        Assert.Equal("#FFF09B", AppearanceSettings.SelectionColor(false, 128));
        Assert.Equal("#665726", AppearanceSettings.SelectionColor(true, 128));
        Assert.Equal("#FFFFFF", AppearanceSettings.SelectionColor(false, 0));
        Assert.Equal("#FFE138", AppearanceSettings.SelectionColor(false, 255));
    }

    // The selected row stays readable at every step of the slider in both themes (4.5:1, ordinary text).
    [Fact] public void SelectedTextKeepsItsContrastAtEveryIntensity()
    {
        foreach (var dark in new[] { false, true })
            for (var intensity = 0; intensity <= 255; intensity++)
            {
                var ratio = AppearanceSettings.Contrast(AppearanceSettings.SelectedTextColor(dark, intensity), AppearanceSettings.SelectionColor(dark, intensity));
                Assert.True(ratio >= 4.5, $"{(dark ? "dark" : "light")} {intensity}: {ratio:0.00}");
            }
        Assert.Equal("#FFF6D8", AppearanceSettings.SelectedTextColor(true, 128)); // the colours used before stay as they were
        Assert.Equal("#242820", AppearanceSettings.SelectedTextColor(false, 128));
        Assert.Equal("#000000", AppearanceSettings.SelectedTextColor(true, 255));
    }

    [Fact] public void SettingsSurviveSaveAndReloadAndResetRestoresThem()
    {
        var store = new LibraryStore(Path.Combine(folder, "settings.json"));
        var changed = new AppearanceSettings { SelectionIntensity = 200, ListTooltips = false, GroupsOnTop = false };
        store.Save(new Library { Appearance = changed });
        var loaded = store.Load().Appearance;
        Assert.Equal((200d, false, false), (loaded.SelectionIntensity, loaded.ListTooltips, loaded.GroupsOnTop));
        Assert.Null(loaded.LoadWarning);
        var reset = loaded.Reset();
        Assert.Equal((128d, true, true), (reset.SelectionIntensity, reset.ListTooltips, reset.GroupsOnTop));
    }

    [Fact] public void AnOldProfileWithoutTheSwitchesLoadsTheDefaults()
    {
        var read = JsonSerializer.Deserialize<AppearanceSettings>("""{"Theme":1,"MinimumRowHeight":24}""")!;
        Assert.Equal((128d, true, true), (read.SelectionIntensity, read.ListTooltips, read.GroupsOnTop));
        Assert.Null(read.LoadWarning);
    }

    [Theory]
    [InlineData("""{"SelectionIntensity":300}""")]
    [InlineData("""{"SelectionIntensity":"high"}""")]
    [InlineData("""{"ListTooltips":"no"}""")]
    [InlineData("""{"GroupsOnTop":1}""")]
    [InlineData("""{"SelectionIntensity":128.25,"Theme":7}""")]
    public void AWrongValueFallsBackAndKeepsTheOriginalText(string json)
    {
        var read = JsonSerializer.Deserialize<AppearanceSettings>(json)!;
        Assert.Equal((128d, true, true), (read.SelectionIntensity, read.ListTooltips, read.GroupsOnTop));
        Assert.NotNull(read.LoadWarning);
        // An unrelated save writes back what was there, as for the other cosmetic settings.
        Assert.Equal(JsonDocument.Parse(json).RootElement.GetRawText(), JsonSerializer.Serialize(read));
    }

    [Fact] public void GroupsGoFirstUnlessMixedByName()
    {
        BaseEntry Base(string name) => new() { Name = name, Kind = ConnectionKind.File, Location = "C:\\" + name };
        string[] groups = ["Архив/Бухгалтерия", "Архив/Зарплата"];
        BaseEntry[] bases = [Base("Аптека"), Base("Склад"), Base("Бухгалтерия")];
        static string Show((string? Group, BaseEntry? Entry) x) => x.Group is { } group ? "[" + group.Split('/')[^1] + "]" : x.Entry!.Name;
        Assert.Equal(["[Бухгалтерия]", "[Зарплата]", "Аптека", "Склад", "Бухгалтерия"], Groups.Level(groups, bases, false, false).Select(Show));
        // Mixed: by name; of equal names the group first.
        Assert.Equal(["Аптека", "[Бухгалтерия]", "Бухгалтерия", "[Зарплата]", "Склад"], Groups.Level(groups, bases, true, false).Select(Show));
        Assert.Equal(["Склад", "[Зарплата]", "[Бухгалтерия]", "Бухгалтерия", "Аптека"], Groups.Level(groups, bases, true, true).Select(Show));
    }
}
