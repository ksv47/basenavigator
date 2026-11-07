using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>Stage 11, part 3: what the window «Сценарий резервирования», the settings section «Сценарии резервирования» and the
/// base's page «Резервное копирование» do with scenarios.</summary>
public sealed class BackupScenarioEditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-backup-edit-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "library.json");
    public BackupScenarioEditTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private static decimal? Number(string? text) => text is null ? null : decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    private static BackupScenario Valid() => BackupScenarioEdit.New() with { Name = "Ежедневно" };

    [Fact] public void NewScenarioHasTheOriginalsNameAndThePartOneDefaults()
    {
        var first = BackupScenarioEdit.New(); var second = BackupScenarioEdit.New();
        Assert.Equal("Новый сценарий", first.Name);
        Assert.Equal("", first.Description);
        Assert.Equal("Новый сценарий", first.ShownDescription);
        Assert.Equal(BackupName.DefaultTemplate, first.Template);
        Assert.Equal(BackupFormat.Dt, first.Format);
        Assert.True(first.UseFolder1); Assert.Equal("", first.Folder1);
        Assert.Equal(0, first.KeepCopies);
        Assert.NotEqual(Guid.Empty, first.Id); Assert.NotEqual(first.Id, second.Id);
        Assert.Null(BackupScenarioEdit.Problem(first, windows: true));
    }

    [Theory]
    [InlineData("", "Старое", "Новое", "Новое")]
    [InlineData("Старое", "Старое", "Новое", "Новое")]
    [InlineData("Своё описание", "Старое", "Новое", "Своё описание")]
    [InlineData("Ст", "Старое", "Новое", "Ст")]
    public void DescriptionFollowsTheNameOnlyWhileItIsEmptyOrRepeatsIt(string description, string previous, string name, string expected) =>
        Assert.Equal(expected, BackupScenarioEdit.FollowName(description, previous, name));

    [Fact] public void TemplateCharactersAreNamedAsTheOriginalDoes()
    {
        Assert.Null(BackupScenarioEdit.TemplateMessage("{Prefix}_{IBName}_{Date4}"));
        Assert.Equal(":*", BackupScenarioEdit.InvalidTemplateCharacters("a:b*c:"));
        Assert.Equal("Шаблон имени файла содержит некорректные символы! ( : * )", BackupScenarioEdit.TemplateMessage("a:b*c:"));
        Assert.Equal("Шаблон имени файла содержит некорректные символы! ( \\x09 / )", BackupScenarioEdit.TemplateMessage("a\tb/c"));
        // The characters of Windows on every system, as BackupName removes them.
        Assert.Equal("\"<>?\\|", BackupScenarioEdit.InvalidTemplateCharacters("\"<>?\\|"));
    }

    [Fact] public void FolderCharactersOfWindowsAreRefusedOnlyOnWindows()
    {
        Assert.False(BackupScenarioEdit.InvalidFolder(@"\\nas\backup\{IBName}", windows: true));
        Assert.False(BackupScenarioEdit.InvalidFolder(@"D:\Архив\{Y4}-{MM}", windows: true));
        Assert.True(BackupScenarioEdit.InvalidFolder(@"D:\a|b", windows: true));
        Assert.True(BackupScenarioEdit.InvalidFolder(@"D:\a?", windows: true));
        Assert.False(BackupScenarioEdit.InvalidFolder("/srv/a|b?", windows: false));
        Assert.True(BackupScenarioEdit.InvalidFolder("/srv/a\nb", windows: false));
    }

    [Fact] public void ProblemsAreShownInTheirSections()
    {
        Assert.Equal(0, BackupScenarioEdit.Problem(Valid() with { Name = "  " }, true)!.Section);
        Assert.Equal(0, BackupScenarioEdit.Problem(Valid() with { Description = "a\nb" }, true)!.Section);
        Assert.Equal(1, BackupScenarioEdit.Problem(Valid() with { Template = "" }, true)!.Section);
        var template = BackupScenarioEdit.Problem(Valid() with { Template = "{IBName}?" }, true)!;
        Assert.Equal(1, template.Section); Assert.Contains("( ? )", template.Message);
        Assert.Equal(2, BackupScenarioEdit.Problem(Valid() with { Folder1 = @"D:\a<b" }, true)!.Section);
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { Folder1 = "/srv/a<b" }, false));
        // A checked «2.»/«3. Каталог» needs a folder; it does not matter while the copy to the computer is off or the flag is clear.
        var empty = BackupScenarioEdit.Problem(Valid() with { UseFolder3 = true, Folder3 = " " }, true)!;
        Assert.Equal(2, empty.Section); Assert.StartsWith("3. Каталог", empty.Message);
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { UseFolder1 = false, UseFolder2 = true }, true));
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { Folder2 = "", UseFolder2 = false }, true));
        Assert.Equal(3, BackupScenarioEdit.Problem(Valid() with { Format = BackupFormat.Zip, Split = BackupSplit.Custom, VolumeSize = 0 }, true)!.Section);
        Assert.Equal(3, BackupScenarioEdit.Problem(Valid() with { Format = BackupFormat.Zip, Split = BackupSplit.Custom, VolumeSize = BackupScenario.MaxVolumeSize + 1 }, true)!.Section);
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { Split = BackupSplit.Custom, VolumeSize = 700 }, true));
        // A size typed for «Определить» stays when another preset is chosen.
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { Split = BackupSplit.Mb100, VolumeSize = 700 }, true));
        Assert.Equal(3, BackupScenarioEdit.Problem(Valid() with { KeepCopies = -1 }, true)!.Section);
        Assert.Equal(3, BackupScenarioEdit.Problem(Valid() with { KeepCopies = BackupScenario.MaxKeepCopies + 1 }, true)!.Section);
        Assert.Null(BackupScenarioEdit.Problem(Valid() with { KeepCopies = 7 }, true));
        // Whatever the model refuses is refused here too, never saved.
        Assert.NotNull(BackupScenarioEdit.Problem(Valid() with { Format = (BackupFormat)9 }, true));
    }

    // Codex audit, part 3, P2: folders 2 and 3 count only under the master flag — the editor's check and the copies follow one rule.
    [Fact] public void FoldersTwoAndThreeNeedTheMasterFlagAndTheirOwn()
    {
        var both = Valid() with { UseFolder2 = true, Folder2 = "/a", UseFolder3 = true, Folder3 = "/b" };
        Assert.True(both.CopiesToFolder2); Assert.True(both.CopiesToFolder3);
        var master = both with { UseFolder1 = false };
        Assert.False(master.CopiesToFolder2); Assert.False(master.CopiesToFolder3);
        Assert.False((both with { UseFolder2 = false }).CopiesToFolder2);
        Assert.True((both with { UseFolder2 = false }).CopiesToFolder3);
        // The empty folder of an inactive destination is not required; of an active one it is.
        Assert.Null(BackupScenarioEdit.Problem(master with { Folder2 = "", Folder3 = "" }, true));
        Assert.Equal(2, BackupScenarioEdit.Problem(both with { Folder2 = "" }, true)!.Section);
    }

    // Codex audit, part 3, P2: a fractional or empty number is refused, never truncated into another policy.
    [Theory]
    [InlineData("1.9", "100", false)]
    [InlineData("0.9", "100", false)]
    [InlineData(null, "100", false)]
    [InlineData("-1", "100", false)]
    [InlineData("10000", "100", false)]
    [InlineData("3", "100.5", true)]
    [InlineData("3", null, true)]
    [InlineData("3", "0", true)]
    [InlineData("3", "65536", true)]
    public void FractionalOrEmptyNumbersAreRefused(string? keep, string? volume, bool volumeUsed)
    {
        var problem = BackupScenarioEdit.NumberProblem(Number(keep), Number(volume), volumeUsed);
        Assert.Equal(3, problem!.Section);
        Assert.Contains("целое число", problem.Message);
    }

    [Theory]
    [InlineData("0", "1", true)]
    [InlineData("9999", "65535", true)]
    [InlineData("5", "100.5", false)]
    [InlineData("5", null, false)]
    [InlineData("5", "0", false)]
    public void WholeNumbersInRangeAndUnusedSizesPass(string? keep, string? volume, bool volumeUsed) =>
        Assert.Null(BackupScenarioEdit.NumberProblem(Number(keep), Number(volume), volumeUsed));

    // Codex audit, part 3, P3: inactive archive settings of a DT/CF scenario never block saving it.
    [Fact] public void InactiveArchiveSettingsDoNotBlockADtOrCfScenario()
    {
        var saved = Valid() with { Format = BackupFormat.Zip, Compression = BackupCompression.Fast, Split = BackupSplit.Mb100 };
        var switched = saved with { Format = BackupFormat.Cf, Split = BackupSplit.Custom, VolumeSize = 0, Compression = BackupCompression.Ultra };
        Assert.Null(BackupScenarioEdit.Problem(switched, true));
        var normal = BackupScenarioEdit.Normalize(switched, saved);
        Assert.Equal((BackupFormat.Cf, BackupCompression.Fast, BackupSplit.Mb100, 0), (normal.Format, normal.Compression, normal.Split, normal.VolumeSize));
        normal.Validate();
        // Valid inactive settings stay as chosen; a ZIP is never changed and its size is checked.
        var kept = switched with { VolumeSize = 300 };
        Assert.Same(kept, BackupScenarioEdit.Normalize(kept, saved));
        var zip = switched with { Format = BackupFormat.Zip };
        Assert.Same(zip, BackupScenarioEdit.Normalize(zip, saved));
        Assert.Equal(3, BackupScenarioEdit.Problem(zip, true)!.Section);
        Assert.Equal(BackupScenario.MaxVolumeSize, BackupScenarioEdit.Normalize(switched with { Format = BackupFormat.Dt, VolumeSize = -1 }, saved with { VolumeSize = BackupScenario.MaxVolumeSize }).VolumeSize);
    }

    // Grok audit, part 3: a problem names its field (focused, cleared when it changes); a disabled «2./3. Каталог» keeps its text and
    // never blocks saving; «1. Каталог» is checked whatever its flag (the default folder takes it).
    [Fact] public void ProblemsNameTheirFieldsAndInactiveFoldersAreNotChecked()
    {
        Assert.Equal(BackupScenarioField.Name, BackupScenarioEdit.Problem(Valid() with { Name = "" }, true)!.Field);
        Assert.Equal(BackupScenarioField.Description, BackupScenarioEdit.Problem(Valid() with { Description = "a\tb" }, true)!.Field);
        Assert.Equal(BackupScenarioField.Template, BackupScenarioEdit.Problem(Valid() with { Template = " " }, true)!.Field);
        Assert.Equal(BackupScenarioField.Template, BackupScenarioEdit.Problem(Valid() with { Template = "a|b" }, true)!.Field);
        Assert.Equal(BackupScenarioField.Folder1, BackupScenarioEdit.Problem(Valid() with { UseFolder1 = false, Folder1 = @"D:\a|b" }, true)!.Field);
        var bad = Valid() with { UseFolder2 = true, Folder2 = @"D:\a|b", UseFolder3 = true, Folder3 = @"D:\c?" };
        Assert.Equal(BackupScenarioField.Folder2, BackupScenarioEdit.Problem(bad, true)!.Field);
        Assert.Equal(BackupScenarioField.Folder3, BackupScenarioEdit.Problem(bad with { UseFolder2 = false }, true)!.Field);
        Assert.Null(BackupScenarioEdit.Problem(bad with { UseFolder2 = false, UseFolder3 = false }, true));
        Assert.Null(BackupScenarioEdit.Problem(bad with { UseFolder1 = false }, true));
        Assert.Equal(BackupScenarioField.VolumeSize, BackupScenarioEdit.Problem(Valid() with { Format = BackupFormat.Zip, Split = BackupSplit.Custom }, true)!.Field);
        Assert.Equal(BackupScenarioField.KeepCopies, BackupScenarioEdit.Problem(Valid() with { KeepCopies = -3 }, true)!.Field);
        Assert.Equal(BackupScenarioField.KeepCopies, BackupScenarioEdit.NumberProblem(10000m, 1m, false)!.Field);
        Assert.Equal(BackupScenarioField.VolumeSize, BackupScenarioEdit.NumberProblem(1m, 70000m, true)!.Field);
    }

    // Codex recheck, part 3: a control character in an inactive folder is named with that folder, never as the name; the window drops
    // control characters as they are typed, so its text reaches the check without them.
    [Fact] public void ControlCharactersInAnyFolderAreNamedWithTheirFolder()
    {
        var inactive = BackupScenarioEdit.New() with { UseFolder2 = false, Folder2 = "a\tb" };
        var problem = BackupScenarioEdit.Problem(inactive, windows: true)!;
        Assert.Equal((2, BackupScenarioField.Folder2), (problem.Section, problem.Field));
        Assert.StartsWith("2. Каталог", problem.Message);
        Assert.Equal(BackupScenarioField.Folder3, BackupScenarioEdit.Problem(BackupScenarioEdit.New() with { UseFolder1 = false, UseFolder3 = true, Folder3 = "x\ny" }, false)!.Field);
        Assert.Equal(BackupScenarioField.Folder1, BackupScenarioEdit.Problem(BackupScenarioEdit.New() with { Folder1 = "\u0001" }, false)!.Field);
        // As the window reads it: without the tab, the inactive folder no longer matters.
        Assert.Null(BackupScenarioEdit.Problem(inactive with { Folder2 = BackupScenarioEdit.WithoutControls(inactive.Folder2) }, true));
        Assert.Equal("ab", BackupScenarioEdit.WithoutControls("a\tb"));
        Assert.Equal("Имя", BackupScenarioEdit.WithoutControls("Им\r\nя"));
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 2, 3, 3, 3 }, Enum.GetValues<BackupScenarioField>().Select(BackupScenarioEdit.Section));
    }

    [Fact] public void FormatsAreShownAsTheOriginalsMasks()
    {
        Assert.Equal("*.DT", BackupScenarioEdit.FormatText(BackupFormat.Dt));
        Assert.Equal("*.CF", BackupScenarioEdit.FormatText(BackupFormat.Cf));
        Assert.Equal("*.ZIP", BackupScenarioEdit.FormatText(BackupFormat.Zip));
        Assert.Equal("<Неизвестный формат>", BackupScenarioEdit.FormatText((BackupFormat)5));
    }

    [Fact] public void PutReplacesInPlaceKeepingUnknownFieldsOrAddsAtTheEnd()
    {
        var unknown = new Dictionary<string, JsonElement> { ["Ftp"] = JsonDocument.Parse("\"ftp://x\"").RootElement };
        var original = BackupScenarioSet.Original();
        var set = original with { Scenarios = [original.Scenarios[0] with { Extra = unknown }, .. original.Scenarios.Skip(1)] };
        var changed = BackupScenarioEdit.Put(set, set.Scenarios[0] with { Name = "DT ежедневно", Extra = null });
        Assert.Equal(3, changed.Scenarios.Count);
        Assert.Equal("DT ежедневно", changed.Scenarios[0].Name);
        Assert.Same(unknown, changed.Scenarios[0].Extra);
        Assert.Equal(set.DefaultId, changed.DefaultId);
        var added = BackupScenarioEdit.Put(changed, Valid());
        Assert.Equal(4, added.Scenarios.Count); Assert.Equal("Ежедневно", added.Scenarios[3].Name);
        // The set given is not changed.
        Assert.Equal(3, changed.Scenarios.Count);
    }

    [Fact] public void RemovingTheDefaultMakesTheFirstOfTheRestTheDefault()
    {
        var set = BackupScenarioSet.Original();
        var withoutDump = BackupScenarioEdit.Remove(set, BackupScenarioSet.DumpId);
        Assert.Equal([BackupScenarioSet.ConfigurationId, BackupScenarioSet.ArchiveId], withoutDump.Scenarios.Select(x => x.Id));
        Assert.Equal(BackupScenarioSet.ConfigurationId, withoutDump.DefaultId);
        var withoutArchive = BackupScenarioEdit.Remove(set, BackupScenarioSet.ArchiveId);
        Assert.Equal(BackupScenarioSet.DumpId, withoutArchive.DefaultId);
        // A stored default that is gone (the list marks the first) is fixed on the next removal.
        var stale = set with { DefaultId = Guid.NewGuid() };
        Assert.Equal(BackupScenarioSet.ConfigurationId, BackupScenarioEdit.Remove(stale, BackupScenarioSet.DumpId).DefaultId);
        Assert.Equal(BackupScenarioSet.DumpId, BackupScenarioEdit.Remove(stale, BackupScenarioSet.ArchiveId).DefaultId);
        var single = BackupScenarioEdit.Remove(withoutDump, BackupScenarioSet.ArchiveId);
        Assert.Contains("единственный", Assert.Throws<InvalidOperationException>(() => BackupScenarioEdit.Remove(single, BackupScenarioSet.ConfigurationId)).Message);
        Assert.Throws<InvalidOperationException>(() => BackupScenarioEdit.Remove(set, Guid.NewGuid()));
    }

    [Fact] public void DefaultIsSetOnlyForAScenarioOfTheSet()
    {
        var set = BackupScenarioEdit.WithDefault(BackupScenarioSet.Original(), BackupScenarioSet.ArchiveId);
        Assert.Equal(BackupScenarioSet.ArchiveId, set.Default!.Id);
        Assert.Throws<InvalidOperationException>(() => BackupScenarioEdit.WithDefault(set, Guid.NewGuid()));
    }

    [Fact] public void PageChoicesStartWithTheDefaultAndKeepAScenarioThatIsGone()
    {
        var scenarios = BackupScenarioSet.Original().Scenarios;
        var choices = BackupScenarioEdit.Choices(scenarios, null);
        Assert.Equal(4, choices.Count);
        Assert.Equal((null, "<Использовать сценарий по умолчанию>"), choices[0]);
        Assert.Equal(scenarios.Select(x => (Guid?)x.Id), choices.Skip(1).Select(x => x.Id));
        Assert.Equal(scenarios[1].Name, choices[2].Text);
        Assert.Equal(4, BackupScenarioEdit.Choices(scenarios, BackupScenarioSet.ArchiveId).Count);
        var gone = Guid.NewGuid();
        var withGone = BackupScenarioEdit.Choices(scenarios, gone);
        Assert.Equal(5, withGone.Count);
        Assert.Equal(gone, withGone[4].Id);
        Assert.StartsWith("<Сценарий не найден", withGone[4].Text);
    }

    [Fact] public void EditsAreWrittenAndReadBackThroughTheStore()
    {
        var store = new BackupScenarioStore(Settings);
        var set = store.Load();
        var added = Valid() with { Format = BackupFormat.Zip, Split = BackupSplit.Custom, VolumeSize = 650, KeepCopies = 5, UseFolder2 = true, Folder2 = @"\\nas\copies\{IBName}" };
        store.Save(BackupScenarioEdit.WithDefault(BackupScenarioEdit.Put(set, added), added.Id));
        store.Save(BackupScenarioEdit.Remove(store.Load(), BackupScenarioSet.ConfigurationId));
        var read = new BackupScenarioStore(Settings).Load();
        Assert.Equal(added.Id, read.Default!.Id);
        Assert.Equal(added, read.Scenarios.Single(x => x.Id == added.Id) with { Extra = null });
        Assert.DoesNotContain(read.Scenarios, x => x.Id == BackupScenarioSet.ConfigurationId);
    }

    [Fact] public void AnEditOverAnotherWindowsChangeIsRefusedAndChangesNothing()
    {
        var mine = new BackupScenarioStore(Settings); var other = new BackupScenarioStore(Settings);
        var set = mine.Load();
        other.Save(BackupScenarioEdit.WithDefault(other.Load(), BackupScenarioSet.ArchiveId));
        var before = File.ReadAllBytes(mine.PathName);
        var refused = Assert.Throws<IOException>(() => mine.Save(BackupScenarioEdit.Remove(set, BackupScenarioSet.ConfigurationId)));
        Assert.Contains("другим окном", refused.Message);
        Assert.Equal(before, File.ReadAllBytes(mine.PathName));
        // «Обновить» reads the other window's change, and the edit then goes through.
        var fresh = mine.Load();
        Assert.Equal(BackupScenarioSet.ArchiveId, fresh.Default!.Id);
        mine.Save(BackupScenarioEdit.Remove(fresh, BackupScenarioSet.ConfigurationId));
        Assert.Equal(2, other.Load().Scenarios.Count);
    }
}

