using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>Stage 11, part 1: backup scenarios, the name of a backup, the default folder and the import from the original.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-backup-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "profile", "library.json");
    private string Absolute(params string[] parts) => Path.Combine([root, .. parts]);
    public BackupTests() => Directory.CreateDirectory(Path.Combine(root, "profile"));
    public void Dispose() => Directory.Delete(root, true);

    private static readonly DateTime Morning = new(2026, 3, 5, 7, 8, 9);
    private static BackupMacroValues Values(string name = "База", string prefix = "", DateTime? time = null) => new(name, prefix, "PC1", time ?? Morning);

    // ---- The name of a backup (_Unit186.pas:25645-26371) ----

    [Fact] public void EveryMacroIsReplacedInTheOriginalsFormats()
    {
        var template = string.Join(" ", BackupName.Macros);
        Assert.Equal("База P PC1 260305 20260305 26 2026 03 3 05 5 07 7 08 8 09 9", BackupName.Expand(template, Values(prefix: "P")));
        // 24-hour clock, two-digit fields keep their zero, single ones do not.
        Assert.Equal("17 17 45 45 2031 1", BackupName.Expand("{HH} {H} {NN} {N} {Y4} {M}", Values(time: new DateTime(2031, 1, 30, 17, 45, 0))));
    }

    [Fact] public void MacrosAreReplacedRegardlessOfCaseAndOneAfterAnother()
    {
        Assert.Equal("База_20260305", BackupName.Expand("{ibname}_{Date4}", Values()));
        // As StringReplace one macro after another: a macro the base name brings in is replaced by a later one.
        Assert.Equal("Отчёт 2026", BackupName.Expand("{IBName}", Values(name: "Отчёт {Y4}")));
        Assert.Equal("{Unknown}", BackupName.Expand("{Unknown}", Values()));
    }

    [Fact] public void FileNameFollowsTheOriginal()
    {
        Assert.Equal("База_20260305.dt", BackupName.FileName(BackupName.DefaultTemplate, Values(), BackupFormat.Dt));
        Assert.Equal("pre_База_20260305.cf", BackupName.FileName(BackupName.DefaultTemplate, Values(prefix: "pre"), BackupFormat.Cf));
        // Dots become «-» everywhere (the prefix and the template too); the extension is the format's.
        Assert.Equal("v1-2_Бух-3-0_20260305.zip", BackupName.FileName(BackupName.DefaultTemplate, Values("Бух.3.0", "v1.2"), BackupFormat.Zip));
        Assert.Equal("a-b-20260305.dt", BackupName.FileName("a.b.{Date4}", Values(), BackupFormat.Dt));
        // Characters a Windows file name may not have are removed, on every system.
        Assert.Equal("abcdefghi.dt", BackupName.FileName("{IBName}", Values("a/b\\c:d*e?f\"g<h>i|\t"), BackupFormat.Dt));
        // One leading «_» goes only when the prefix is empty.
        Assert.Equal("_База.dt", BackupName.FileName("__{IBName}", Values(), BackupFormat.Dt));
        Assert.Equal("_База.dt", BackupName.FileName("_{IBName}", Values(prefix: "x"), BackupFormat.Dt));
        Assert.Equal("База_.dt", BackupName.FileName("{IBName}_", Values(), BackupFormat.Dt));
        Assert.Throws<ArgumentException>(() => BackupName.FileName("{Prefix}_", Values(), BackupFormat.Dt));
        Assert.Throws<ArgumentException>(() => BackupName.FileName("///", Values(), BackupFormat.Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackupName.Extension((BackupFormat)7));
    }

    [Fact] public void FolderMacrosKeepThePathAndCleanTheValues()
    {
        var folder = Absolute("copies", "{IBName}", "{Y4}-{MM}", "{Other}");
        Assert.Equal(Absolute("copies", "abc.d", "2026-03", "{Other}"), BackupName.Folder(folder, Values("a/b\\c.d")));
        Assert.Equal("", BackupName.Folder("", Values()));
    }

    // Codex audit, part 1, P2: a macro never makes a folder level of dots that leaves the folder.
    [Theory]
    [InlineData("/safe/backups/{IBName}/daily", "..", "")]
    [InlineData("/safe/backups/{IBName}/daily", "../", "")]
    [InlineData("C:\\safe\\{IBName}\\daily", "..\\", "")]
    [InlineData("C:\\safe\\{IBName}\\daily", ".", "")]
    [InlineData("C:\\safe\\{IBName}", ". .", "")]
    [InlineData("/safe/{IBName}{Prefix}/daily", ".", ".")]
    [InlineData("\\\\nas\\share\\{Prefix}.\\x", "a", ".")]
    [InlineData("/safe/.{IBName}", ".", "")]
    // Codex recheck: a macro that gives nothing (empty, or cleaned to nothing) still makes its level checked.
    [InlineData("/safe/backups/.{Prefix}./daily", "a", "")]
    [InlineData("/safe/backups/.{Prefix}./daily", "a", "/")]
    [InlineData("C:\\safe\\.{Prefix}.\\daily", "a", "\\")]
    [InlineData("\\\\nas\\share\\{Prefix}..\\x", "a", "")]
    [InlineData("C:\\safe\\{Prefix}.\\x", "a", ":*?")]
    [InlineData("/safe/{IBName}{Prefix}", "..", "")]
    public void FolderMacrosNeverLeaveTheFolder(string folder, string name, string prefix)
    {
        var error = Assert.Throws<ArgumentException>(() => BackupName.Folder(folder, Values(name, prefix)));
        Assert.Contains("за пределы каталога", error.Message);
    }

    [Theory]
    [InlineData("/safe/{IBName}/daily", "Бух.3.0", "/safe/Бух.3.0/daily")]
    [InlineData("/safe/{IBName}/daily", "...a", "/safe/...a/daily")]
    [InlineData("C:\\safe\\..\\{IBName}", "a", "C:\\safe\\..\\a")]
    [InlineData("/safe/{Prefix}/x", "a", "/safe//x")]
    public void FolderMacrosKeepOrdinaryDotsAndTheUsersOwnPath(string folder, string name, string expected) =>
        Assert.Equal(expected, BackupName.Folder(folder, Values(name)));

    // ---- Scenarios ----

    [Fact] public void ANewProfileHasTheOriginalsReadyScenarios()
    {
        var set = BackupScenarioSet.Original();
        Assert.Equal([BackupFormat.Dt, BackupFormat.Cf, BackupFormat.Zip], set.Scenarios.Select(x => x.Format));
        Assert.Equal([BackupScenarioSet.DumpId, BackupScenarioSet.ConfigurationId, BackupScenarioSet.ArchiveId], set.Scenarios.Select(x => x.Id));
        Assert.Equal(BackupScenarioSet.DumpId, set.Default!.Id);
        Assert.All(set.Scenarios, x => Assert.Equal("{Prefix}_{IBName}_{Date4}", x.Template));
        Assert.Equal("Выгрузка данных информационной базы (*.dt)", set.Scenarios[0].Name);
        Assert.All(set.Scenarios, x => { x.Validate(); Assert.Equal(0, x.KeepCopies); Assert.True(x.UseFolder1); Assert.Null(x.VolumeBytes); });
        set.Validate();
        Assert.Equal("A", new BackupScenario { Name = "A" }.ShownDescription);
        Assert.Equal("A", new BackupScenario { Name = "A" }.ToString());
        Assert.Equal("d", new BackupScenario { Name = "A", Description = "d" }.ShownDescription);
    }

    [Fact] public void TheScenarioOfABaseFallsBackToTheDefault()
    {
        var set = BackupScenarioSet.Original() with { DefaultId = BackupScenarioSet.ArchiveId };
        Assert.Equal(BackupScenarioSet.ConfigurationId, set.For(BackupScenarioSet.ConfigurationId)!.Id);
        Assert.Equal(BackupScenarioSet.ArchiveId, set.For(Guid.NewGuid())!.Id);
        Assert.Equal(BackupScenarioSet.ArchiveId, set.For(null)!.Id);
        Assert.Equal(BackupScenarioSet.DumpId, (set with { DefaultId = Guid.NewGuid() }).Default!.Id);
        Assert.Null(new BackupScenarioSet().For(null));
    }

    [Fact] public void ScenarioValidationNamesTheField()
    {
        void Bad(BackupScenario scenario) => Assert.Throws<ArgumentException>(scenario.Validate);
        var good = new BackupScenario { Name = "A" };
        Bad(good with { Name = " " });
        Bad(good with { Template = "" });
        Bad(good with { Folder2 = "a\nb" });
        Bad(good with { Name = null! });
        Bad(good with { Format = (BackupFormat)9 });
        Bad(good with { Split = BackupSplit.Custom, VolumeSize = 0 });
        Bad(good with { Split = BackupSplit.Custom, VolumeSize = BackupScenario.MaxVolumeSize + 1 });
        Bad(good with { VolumeSize = -1 });
        Bad(good with { KeepCopies = -1 });
        Bad(good with { KeepCopies = BackupScenario.MaxKeepCopies + 1 });
        (good with { Split = BackupSplit.Custom, VolumeSize = 700, KeepCopies = 3 }).Validate();
        Assert.Equal(700L << 20, (good with { Split = BackupSplit.Custom, VolumeSize = 700 }).VolumeBytes);
        Assert.Equal([50L << 20, 100L << 20, 200L << 20, 500L << 20],
            new[] { BackupSplit.Mb50, BackupSplit.Mb100, BackupSplit.Mb200, BackupSplit.Mb500 }.Select(x => (good with { Split = x }).VolumeBytes!.Value));
    }

    [Fact] public void SetValidationRefusesDamage()
    {
        var good = new BackupScenario { Name = "A" };
        Assert.Throws<InvalidDataException>(() => new BackupScenarioSet { Version = 2 }.Validate());
        Assert.Throws<InvalidDataException>(() => new BackupScenarioSet { Scenarios = [null!] }.Validate());
        Assert.Throws<InvalidDataException>(() => new BackupScenarioSet { Scenarios = [good, good with { Name = "B" }] }.Validate());
        Assert.Contains("«»", Assert.Throws<InvalidDataException>(() => new BackupScenarioSet { Scenarios = [good with { Name = "" }] }.Validate()).Message);
    }

    [Fact] public void SameSettingsIgnoresIdAndRepeatedSpaces()
    {
        var a = new BackupScenario { Name = "Архивирование в формате  7Zip", Format = BackupFormat.Zip };
        Assert.True(a.SameSettings(a with { Id = Guid.NewGuid(), Name = "Архивирование в формате 7Zip" }));
        Assert.True(a.SameSettings(a with { VolumeSize = 5 }));
        Assert.False(a.SameSettings(a with { Folder2 = "x" }));
        Assert.False(a.SameSettings(a with { Compression = BackupCompression.Ultra }));
        Assert.False((a with { Split = BackupSplit.Custom, VolumeSize = 5 }).SameSettings(a with { Split = BackupSplit.Custom, VolumeSize = 6 }));
    }

    // ---- The store of scenarios ----

    [Fact] public void StoreGivesTheReadyScenariosWithoutWriting()
    {
        var store = new BackupScenarioStore(Settings);
        Assert.Equal(Path.Combine(root, "profile", "backup-scenarios.json"), store.PathName);
        Assert.Equal(3, store.Load().Scenarios.Count);
        Assert.False(store.Saved); Assert.False(File.Exists(store.PathName));
    }

    [Fact] public void StoreRoundTripKeepsUnknownFieldsAndMakesABackup()
    {
        var store = new BackupScenarioStore(Settings);
        store.Load();
        store.Save(BackupScenarioSet.Original());
        // Fields of a newer version, in the set and in a scenario, are written by hand.
        var json = JsonNode.Parse(File.ReadAllText(store.PathName))!;
        json["future"] = 1; json["Scenarios"]![1]!["futureField"] = "keep";
        File.WriteAllText(store.PathName, json.ToJsonString());
        var before = File.ReadAllBytes(store.PathName);
        var loaded = store.Load();
        // Enumerations are written by name.
        Assert.Contains("\"Zip\"", Encoding.UTF8.GetString(before));
        var changed = loaded with { DefaultId = BackupScenarioSet.ArchiveId, Scenarios = [.. loaded.Scenarios.Select(x => x with { KeepCopies = 2 }),
            new BackupScenario { Name = "Ежедневно", Format = BackupFormat.Zip, Compression = BackupCompression.Ultra, Split = BackupSplit.Custom, VolumeSize = 100,
                UseFolder2 = true, Folder2 = Absolute("{IBName}") }] };
        store.Save(changed);
        Assert.Equal(before, File.ReadAllBytes(store.PathName + ".bak"));
        var again = new BackupScenarioStore(Settings).Load();
        Assert.Equal(1, again.Extra!["future"].GetInt32());
        Assert.Equal("keep", again.Scenarios[1].Extra!["futureField"].GetString());
        Assert.Equal(BackupScenarioSet.ArchiveId, again.DefaultId);
        Assert.Equal(4, again.Scenarios.Count);
        Assert.True(again.Scenarios[3].SameSettings(changed.Scenarios[3]));
        Assert.Equal([2, 2, 2, 0], again.Scenarios.Select(x => x.KeepCopies));
        Assert.Contains("\"Ultra\"", File.ReadAllText(store.PathName));
    }

    [Fact] public void StoreRefusesAChangeMadeByAnotherWindow()
    {
        var first = new BackupScenarioStore(Settings); var second = new BackupScenarioStore(Settings);
        first.Load(); var stale = second.Load();
        first.Save(BackupScenarioSet.Original() with { DefaultId = BackupScenarioSet.ConfigurationId });
        var written = File.ReadAllBytes(first.PathName);
        Assert.Throws<IOException>(second.EnsureUnchanged);
        Assert.Throws<IOException>(() => second.Save(stale));
        Assert.Equal(written, File.ReadAllBytes(first.PathName));
        first.EnsureUnchanged();
        Assert.Equal(BackupScenarioSet.ConfigurationId, second.Load().DefaultId);
    }

    [Fact] public void StoreNeverOverwritesADamagedFile()
    {
        var store = new BackupScenarioStore(Settings);
        File.WriteAllText(store.PathName, "{ not json");
        Assert.Throws<InvalidDataException>(() => store.Load());
        // A store that never read the file still checks it before writing.
        Assert.Throws<InvalidDataException>(() => new BackupScenarioStore(Settings).Save(BackupScenarioSet.Original()));
        Assert.Equal("{ not json", File.ReadAllText(store.PathName));
        File.WriteAllText(store.PathName, "null");
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(store.PathName, "{\"Scenarios\":[{\"Name\":\"A\",\"Format\":\"Rar\"}]}");
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(store.PathName, "{\"Scenarios\":[{\"Name\":\"\"}]}");
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new BackupScenarioSet { Version = 3 }));
    }

    // Codex audit, part 1, P3: a stored scenario without its ID is damage, not a new scenario with a new ID on every read.
    [Fact] public void AStoredScenarioMustHaveItsId()
    {
        var store = new BackupScenarioStore(Settings);
        File.WriteAllText(store.PathName, "{\"Version\":1,\"Scenarios\":[{\"Name\":\"A\"}]}");
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(store.PathName, $"{{\"Version\":1,\"Scenarios\":[{{\"Id\":\"{Guid.Empty}\",\"Name\":\"A\"}}]}}");
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => new BackupScenarioStore(Settings).Save(BackupScenarioSet.Original()));
        Assert.Contains(Guid.Empty.ToString(), File.ReadAllText(store.PathName));
        // A scenario made by the program gets its own ID.
        Assert.NotEqual(Guid.Empty, new BackupScenario { Name = "A" }.Id);
        Assert.NotEqual(new BackupScenario().Id, new BackupScenario().Id);
    }

    [Fact] public void AFailedWriteLeavesTheScenariosAsTheyWere()
    {
        var store = new BackupScenarioStore(Settings);
        store.Load(); store.Save(BackupScenarioSet.Original());
        var before = File.ReadAllBytes(store.PathName);
        Directory.CreateDirectory(store.PathName + ".bak");
        Assert.ThrowsAny<Exception>(() => store.Save(BackupScenarioSet.Original() with { DefaultId = BackupScenarioSet.ArchiveId }));
        Assert.Equal(before, File.ReadAllBytes(store.PathName));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.PathName)!, "*.tmp"));
    }

    // ---- The page «Резервное копирование» of a base: local fields ----

    private static BaseEntry Base(Guid id, string prefix = "") => new() { Id = id, Name = "A", Kind = ConnectionKind.Server, Location = "srv\\a",
        BackupScriptId = BackupScenarioSet.ConfigurationId, BackupFolder = "copies", BackupPrefix = prefix };

    [Fact] public void BackupFieldsAreLocalAndSurviveAReload()
    {
        var id = Guid.NewGuid();
        var read = new BaseEntry { Id = id, Name = "A", Kind = ConnectionKind.Server, Location = "srv\\a" };
        var kept = Assert.Single(BaseEdit.KeepLocal([read], [Base(id, "p")]));
        Assert.Equal((BackupScenarioSet.ConfigurationId, "copies", "p"), (kept.BackupScriptId, kept.BackupFolder, kept.BackupPrefix));

        var list = Absolute("ibases.v8i");
        File.WriteAllText(list, $"[A]\r\nConnect=Srvr=\"srv\";Ref=\"a\";\r\nID={id}\r\n");
        var store = new V8iStore(list);
        var loaded = store.Load(new Library { Bases = [Base(id, "p")] });
        Assert.Equal("p", Assert.Single(loaded.Bases).BackupPrefix);
        Assert.Equal(BackupScenarioSet.ConfigurationId, loaded.Bases[0].BackupScriptId);
        store.Save(loaded with { Bases = [loaded.Bases[0] with { BackupPrefix = "q", BackupFolder = Absolute("x") }] });
        Assert.DoesNotContain("Backup", File.ReadAllText(list));
        Assert.Empty(new V8iStore(list).Load(new Library()).Bases[0].BackupPrefix);
    }

    [Fact] public void ADialogEditOfBackupFieldsGoesOverTheCurrentRecord()
    {
        var id = Guid.NewGuid();
        var original = Base(id, "a");
        var now = original with { BackupFolder = "elsewhere" };
        var result = BaseEdit.OnCurrent([now], original, original with { BackupPrefix = "b", BackupScriptId = null })!;
        Assert.Equal(("b", "elsewhere", (Guid?)null), (result.BackupPrefix, result.BackupFolder, result.BackupScriptId));
        var trimmed = BaseEdit.AsDialogReturns(original with { BackupFolder = " f ", BackupPrefix = " p " });
        Assert.Equal(("f", "p"), (trimmed.BackupFolder, trimmed.BackupPrefix));
    }

    [Fact] public void BackupFieldsAreValidatedAndStoredInTheProfile()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => (Base(id) with { BackupPrefix = "a\r" }).Validate());
        Assert.Throws<ArgumentException>(() => (Base(id) with { BackupFolder = null! }).Validate());
        Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { BackupFolder = "a\nb" }));
        Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { BackupFolder = null! }));
        var store = new LibraryStore(Settings);
        store.Save(new Library { BackupFolder = Absolute("all"), Bases = [Base(id, "p")] });
        var loaded = new LibraryStore(Settings).Load();
        Assert.Equal(Absolute("all"), loaded.BackupFolder);
        Assert.Equal((BackupScenarioSet.ConfigurationId, "copies", "p"), (loaded.Bases[0].BackupScriptId, loaded.Bases[0].BackupFolder, loaded.Bases[0].BackupPrefix));
        // A profile of an older build has no such fields.
        File.WriteAllText(Settings, "{\"Bases\":[{\"Name\":\"A\",\"Kind\":1,\"Location\":\"srv\\\\a\"}]}");
        var old = new LibraryStore(Settings).Load();
        Assert.Equal(("", (Guid?)null, "", ""), (old.BackupFolder, old.Bases[0].BackupScriptId, old.Bases[0].BackupFolder, old.Bases[0].BackupPrefix));
    }

    // ---- The default folder (TFormScriptExecute.PathUpdate) ----

    [Fact] public void DefaultFolderFollowsTheOriginalsOrder()
    {
        var general = Absolute("general");
        var fileFolder = Absolute("base");
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var file = new BaseEntry { Id = id, Name = "F", Kind = ConnectionKind.File, Location = fileFolder };
        var server = new BaseEntry { Id = id, Name = "S", Kind = ConnectionKind.Server, Location = "srv\\s" };
        var scenario = new BackupScenario { Name = "A", Folder1 = Absolute("scenario", "{IBName}") };
        bool Exists(string x) => x == fileFolder;
        // 1. The base's own folder; a relative one inside the general folder.
        Assert.Equal(Absolute("own"), BackupTarget.DefaultFolder(file with { BackupFolder = " " + Absolute("own") + " " }, scenario, general, Exists));
        Assert.Equal(Path.Combine(general, "sub"), BackupTarget.DefaultFolder(file with { BackupFolder = "sub" }, scenario, general, Exists));
        Assert.Equal(Path.Combine(general, "sub"), BackupTarget.DefaultFolder(server with { BackupFolder = "\\sub" }, null, general, Exists));
        Assert.Contains("Каталог по-умолчанию", Assert.Throws<ArgumentException>(() => BackupTarget.DefaultFolder(file with { BackupFolder = "sub" }, scenario, "", Exists)).Message);
        // 2. «1. Каталог» of the scenario, macros still in it.
        Assert.Equal(scenario.Folder1, BackupTarget.DefaultFolder(server, scenario, general, Exists));
        // 3. «Backup» in the folder of a file base that exists (the general folder is not used before it, as in the original).
        Assert.Equal(Path.Combine(fileFolder, "Backup"), BackupTarget.DefaultFolder(file, null, general, Exists));
        Assert.Equal(Path.Combine(fileFolder, "Backup"), BackupTarget.DefaultFolder(file, new BackupScenario { Name = "B" }, general, Exists));
        // 4. The base's folder in the general one: the last 11 characters of its ID.
        Assert.Equal(Path.Combine(general, "456789abcdef"[1..]), BackupTarget.DefaultFolder(server, null, general, Exists));
        Assert.Equal(Path.Combine(general, "456789abcdef"[1..]), BackupTarget.DefaultFolder(file, null, general, _ => false));
        // Nothing left: a clear refusal instead of a guessed place.
        Assert.Contains("свойствах базы", Assert.Throws<ArgumentException>(() => BackupTarget.DefaultFolder(server, null, " ", Exists)).Message);
        Assert.Contains("Каталог базы не найден", Assert.Throws<ArgumentException>(() => BackupTarget.DefaultFolder(file, null, "", _ => false)).Message);
        Directory.CreateDirectory(fileFolder);
        Assert.Equal(Path.Combine(fileFolder, "Backup"), BackupTarget.DefaultFolder(file, null, ""));
    }

    // Grok audit, part 1, P2-1/P3-1: a full path of either system is kept as it is on both (a profile shared by Windows and Linux).
    [Theory]
    [InlineData("/var/backups")]
    [InlineData("//server/share/backups")]
    [InlineData("D:\\Архив")]
    [InlineData("d:/Архив")]
    [InlineData("\\\\server\\share\\backups")]
    public void AFullPathOfEitherSystemIsKeptAsItIs(string folder)
    {
        Assert.True(BackupTarget.IsAbsolute(folder));
        var server = new BaseEntry { Name = "S", Kind = ConnectionKind.Server, Location = "srv\\s", BackupFolder = folder };
        Assert.Equal(folder, BackupTarget.DefaultFolder(server, null, "/srv/general", _ => false));
        Assert.Equal(folder, BackupTarget.DefaultFolder(server, null, "E:\\Общий", _ => false));
    }

    [Fact] public void TheGeneralFolderOfEitherSystemKeepsItsOwnSeparator()
    {
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        BaseEntry Server(string folder = "") => new() { Id = id, Name = "S", Kind = ConnectionKind.Server, Location = "srv\\s", BackupFolder = folder };
        Assert.Equal("/srv/general/56789abcdef", BackupTarget.DefaultFolder(Server(), null, "/srv/general/", _ => false));
        Assert.Equal("E:\\Общий\\56789abcdef", BackupTarget.DefaultFolder(Server(), null, "E:\\Общий", _ => false));
        Assert.Equal("\\\\nas\\copies\\56789abcdef", BackupTarget.DefaultFolder(Server(), null, "\\\\nas\\copies", _ => false));
        Assert.Equal("/sub", BackupTarget.DefaultFolder(Server("sub"), null, "/", _ => false));
        Assert.Equal("E:\\Общий\\a\\b", BackupTarget.DefaultFolder(Server("\\a\\b"), null, "E:\\Общий", _ => false));
        Assert.Equal("/srv/general/a/b", BackupTarget.DefaultFolder(Server("a/b"), null, "/srv/general", _ => false));
        // Neither a root without a drive nor a drive without a root is a full path; a relative general folder is refused.
        Assert.False(BackupTarget.IsAbsolute("\\sub")); Assert.False(BackupTarget.IsAbsolute("C:sub")); Assert.False(BackupTarget.IsAbsolute("sub"));
        Assert.Contains("полным путём", Assert.Throws<ArgumentException>(() => BackupTarget.DefaultFolder(Server(), null, "relative", _ => false)).Message);
        Assert.Contains("полным путём", Assert.Throws<ArgumentException>(() => BackupTarget.DefaultFolder(Server("sub"), null, "relative", _ => false)).Message);
        // An unused relative general folder does not block a destination chosen before it (Codex recheck).
        Assert.Equal("/safe/backups", BackupTarget.DefaultFolder(Server("/safe/backups"), null, "relative", _ => false));
        Assert.Equal("D:\\s", BackupTarget.DefaultFolder(Server(), new BackupScenario { Name = "A", Folder1 = "D:\\s" }, "relative", _ => false));
        var file = new BaseEntry { Id = id, Name = "F", Kind = ConnectionKind.File, Location = Absolute("fb") };
        Assert.Equal(Path.Combine(Absolute("fb"), "Backup"), BackupTarget.DefaultFolder(file, null, "relative", _ => true));
    }

    // Grok audit, part 1, P3-2: as AnsiPos before StringReplace — {IBName} and {Prefix} in any case, the others only when their
    // exact spelling is there (then every spelling of them goes).
    [Fact] public void OnlyIBNameAndPrefixAreFoundInAnyCase()
    {
        Assert.Equal("База_P_{date4}_{comp_name}", BackupName.Expand("{ibname}_{PREFIX}_{date4}_{comp_name}", Values(prefix: "P")));
        Assert.Equal("20260305-20260305", BackupName.Expand("{Date4}-{DATE4}", Values()));
        Assert.Equal("{y4}", BackupName.Expand("{y4}", Values()));
        // Folders follow the same rule (Codex recheck): an exact spelling enables every spelling, none leaves them as text.
        Assert.Equal(Absolute("База", "2026", "2026"), BackupName.Folder(Absolute("{ibname}", "{y4}", "{Y4}"), Values()));
        Assert.Equal("/safe/20260305-20260305", BackupName.Folder("/safe/{Date4}-{DATE4}", Values()));
        Assert.Equal(Absolute("{y4}", "{date4}"), BackupName.Folder(Absolute("{y4}", "{date4}"), Values()));
    }

    // ---- Import from the original (synthetic samples, no personal data) ----

    private const string DumpLegacy = "DDAEEA9B6A18", ConfigurationLegacy = "B357EE8141B9", ArchiveLegacy = "76C97D75F874", OwnLegacy = "0123456789AB";
    private readonly Guid fileBase = Guid.NewGuid(), serverBase = Guid.NewGuid(), plainBase = Guid.NewGuid();
    private string Original => Path.Combine(root, "SMSettings");
    private string Scripts => Path.Combine(Original, "BackupScripts");

    private void WriteOriginal(string? list = null, Dictionary<string, string>? files = null, string smc = "", string cnf = "")
    {
        Directory.CreateDirectory(Scripts);
        var bom = new UTF8Encoding(true);
        files ??= new()
        {
            ["SA9B6A18.bks"] = Bks("Выгрузка данных информационной базы (*.dt)", "Выгрузка данных информационной базы в DT-формате.", DumpLegacy, "0"),
            ["SE8141B9.bks"] = Bks("Выгрузка конфигурации информационной базы (*.cf)", "Выгрузка конфигурации информационной базы в CF-формате", ConfigurationLegacy, "1"),
            ["SD75F874.bks"] = Bks("Архивирование данных инф. базы в формате  7Zip", "Только для файлового варианта информационной базы", ArchiveLegacy, "2"),
            ["S6789AB0.bks"] = Bks("Свой RAR", "", OwnLegacy, "3", target: "UseLocalFolder1=1\r\nLocalFolder1=D:\\copies\r\nUseLocalFolder2=1\r\nLocalFolder2=\\\\nas\\{IBName}\r\nUseLocalFolder3=0\r\nLocalFolder3=\r\n",
                archive: "CompressMethod=3\r\nSplitVolumePreset=1\r\nSplitVolume=700\r\nVolumUnit=mb\r\n")
        };
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(Scripts, name), text, bom);
        list ??= string.Concat(files.Keys.Select(x => $"[{Legacy(x)}]\r\nFILE={x}\r\nUPDATE=19.12.2015 19:24:56\r\n\r\n")) + $"[MAIN]\r\nID={DumpLegacy}\r\n";
        File.WriteAllText(Path.Combine(Scripts, "bckscripts.lst"), list, bom);
        File.WriteAllText(Path.Combine(Original, "v8config.smc"), smc);
        if (cnf.Length > 0) File.WriteAllText(Path.Combine(Original, "settings.cnf"), cnf);
    }

    private static string Legacy(string file) => file switch
    {
        "SA9B6A18.bks" => DumpLegacy, "SE8141B9.bks" => ConfigurationLegacy, "SD75F874.bks" => ArchiveLegacy, "S6789AB0.bks" => OwnLegacy, _ => Path.GetFileNameWithoutExtension(file)
    };

    private static string Bks(string name, string description, string id, string format, string? target = null, string? archive = null, string extra = "") =>
        $"[INFO]\r\nName={name}\r\nDescr={description}\r\n\r\n[FILE]\r\nFName={{Prefix}}_{{IBName}}_{{Date4}}\r\nID={id}\r\nFormatId={format}\r\n{extra}\r\n[TARGET]\r\n" +
        (target ?? "UseLocalFolder1=1\r\nLocalFolder1=\r\nUseLocalFolder2=0\r\nLocalFolder2=\r\nUseLocalFolder3=0\r\nLocalFolder3=\r\n") +
        "\r\n[ARCHIVE]\r\n" + (archive ?? "CompressMethod=0\r\nSplitVolumePreset=0\r\nSplitVolume=0\r\nVolumUnit=mb\r\n");

    private string Smc(string? own = OwnLegacy) =>
        $"[{fileBase}]\r\nBackupScriptID={own}\r\nBackupFolder=D:\\Архив\r\nBackupPrefix=ERP\r\n[{serverBase}]\r\nBackupScriptID={DumpLegacy}\r\nBackupFolder=\r\nBackupPrefix=\r\n";

    private string List => Path.Combine(root, "ibases.v8i");

    private LegacyImportPreview Preview(BackupScenarioStore? store, LegacyImportOptions? options = null, Library? local = null)
    {
        File.WriteAllText(List, $"[Файловая]\r\nID={fileBase}\r\nConnect=File=\"{Absolute("fb")}\";\r\n[Серверная]\r\nID={serverBase}\r\nConnect=Srvr=\"srv\";Ref=\"erp\";\r\n" +
            $"[Без настроек]\r\nID={plainBase}\r\nConnect=Srvr=\"srv\";Ref=\"plain\";\r\n");
        return LegacyImport.Preview(local ?? new Library(), List, Original, options ?? new(), store);
    }

    [Fact] public void ImportBringsScenariosAndThePageOfEachBase()
    {
        WriteOriginal(smc: Smc(), cnf: "<Settings><Backup><BackupFolder>E:\\Общий</BackupFolder><AppRarFile>C:\\rar.exe</AppRarFile><RarCmdAdding></RarCmdAdding></Backup></Settings>");
        var store = new BackupScenarioStore(Settings);
        var plan = Preview(store);
        // The ready scenarios of the original are the ones this program already has; the RAR one comes as ZIP.
        var result = plan.Backup!.Result;
        Assert.Equal(4, result.Scenarios.Count);
        var own = result.Scenarios[3];
        Assert.Equal(LegacyImport.LegacyBackupId(OwnLegacy), own.Id);
        Assert.Equal(("Свой RAR", BackupFormat.Zip, BackupCompression.Normal, BackupSplit.Custom, 700), (own.Name, own.Format, own.Compression, own.Split, own.VolumeSize));
        Assert.Equal((true, "D:\\copies", true, "\\\\nas\\{IBName}", false), (own.UseFolder1, own.Folder1, own.UseFolder2, own.Folder2, own.UseFolder3));
        Assert.Equal(BackupScenarioSet.DumpId, result.DefaultId);
        Assert.Contains(plan.Notices, x => x.Contains("RAR") && x.Contains("ZIP"));
        Assert.Contains(plan.Notices, x => x.Contains("Добавятся сценарии резервирования: Свой RAR"));
        Assert.Contains(plan.Notices, x => x.Contains("NORMAL"));
        var bases = plan.Added.ToDictionary(x => x.Id);
        Assert.Equal((own.Id, "D:\\Архив", "ERP"), (bases[fileBase].BackupScriptId, bases[fileBase].BackupFolder, bases[fileBase].BackupPrefix));
        Assert.Equal((BackupScenarioSet.DumpId, "", ""), (bases[serverBase].BackupScriptId, bases[serverBase].BackupFolder, bases[serverBase].BackupPrefix));
        Assert.Null(bases[plainBase].BackupScriptId);
        Assert.Equal("E:\\Общий", plan.Result.BackupFolder);
        // Only the RAR settings are named as not transferred.
        Assert.DoesNotContain(plan.Notices, x => x.Contains("BackupScriptID") || x.Contains("BackupPrefix") || x.Contains("Backup/BackupFolder"));
        Assert.Contains(plan.Notices, x => x.Contains("Backup/AppRarFile") && x.Contains("Backup/RarCmdAdding"));
        Assert.False(store.Saved);

        var library = new LibraryStore(Settings);
        var applied = LegacyImport.Apply(plan, library, new ActivityStore(Settings));
        Assert.Null(applied.FavoriteError);
        var saved = new BackupScenarioStore(Settings).Load();
        Assert.Equal(4, saved.Scenarios.Count); Assert.Equal(BackupScenarioSet.DumpId, saved.DefaultId);
        Assert.Equal(own.Id, library.Load().Bases.Single(x => x.Id == fileBase).BackupScriptId);

        // A repeat adds nothing and changes no scenario.
        var repeat = Preview(new BackupScenarioStore(Settings), local: library.Load());
        Assert.Null(repeat.Backup);
        Assert.Empty(repeat.Added);
    }

    [Fact] public void ImportWithoutTheStoreOrWithTheOptionOffNamesWhatIsNotTransferred()
    {
        WriteOriginal(smc: Smc());
        var withoutStore = Preview(null);
        Assert.Null(withoutStore.Backup);
        Assert.Contains(withoutStore.Notices, x => x.Contains("не переносятся") && x.Contains("BackupScriptID") && !x.Contains("BackupPrefix"));
        var entry = withoutStore.Added.Single(x => x.Id == fileBase);
        Assert.Equal(((Guid?)null, "ERP"), (entry.BackupScriptId, entry.BackupPrefix));

        var off = Preview(new BackupScenarioStore(Settings), new LegacyImportOptions(Backup: false));
        Assert.Null(off.Backup);
        Assert.Contains(off.Notices, x => x.Contains("не переносятся") && x.Contains("BackupScriptID") && x.Contains("BackupPrefix"));
        Assert.Equal("", off.Added.Single(x => x.Id == fileBase).BackupPrefix);
    }

    [Fact] public void ImportExplainsWhatItCannotRead()
    {
        var files = new Dictionary<string, string>
        {
            ["S0000001.bks"] = Bks("Неизвестный формат", "", "000000000001", "7"),
            ["S0000002.bks"] = Bks("", "", "000000000002", "1", archive: "SplitVolumePreset=1\r\nSplitVolume=0\r\nVolumUnit=mb\r\n", extra: "Future=1\r\n"),
            ["S0000003.bks"] = Bks("Гигабайты", "", "000000000003", "2", target: "LocalFolder1=\r\n", archive: "SplitVolumePreset=1\r\nSplitVolume=2\r\nVolumUnit=gb\r\n")
        };
        var list = "[000000000001]\r\nFILE=S0000001.bks\r\n[000000000002]\r\nFILE=S0000002.bks\r\n[000000000003]\r\nFILE=S0000003.bks\r\n" +
            "[000000000004]\r\nFILE=..\\outside.bks\r\n[000000000005]\r\nFILE=missing.bks\r\n[000000000002]\r\nFILE=S0000002.bks\r\n[MAIN]\r\nID=000000000003\r\n";
        WriteOriginal(list, files, smc: Smc("FFFFFFFFFFFF"));
        // The destination saved its scenarios before: the original's default does not replace its own.
        var store = new BackupScenarioStore(Settings);
        store.Load(); store.Save(BackupScenarioSet.Original());
        var plan = Preview(store);
        var notices = string.Join("\n", plan.Notices);
        Assert.Contains("«Неизвестный формат»: неизвестный формат выгрузки", notices);
        Assert.Contains("Сценарий 000000000002", notices);
        Assert.Contains("FILE/Future", notices);
        Assert.Contains("размер тома не распознан", notices);
        Assert.Contains("недопустимое имя файла «..\\outside.bks»", notices);
        Assert.Contains("missing.bks", notices);
        Assert.Contains("повторяющаяся или пустая запись", notices);
        Assert.Contains("сценарий резервирования FFFFFFFFFFFF не найден", notices);
        Assert.Contains("по умолчанию назначения сохранён", notices);
        var result = plan.Backup!.Result;
        Assert.Equal(BackupScenarioSet.DumpId, result.DefaultId);
        Assert.Equal(["Сценарий 000000000002", "Гигабайты"], result.Scenarios.Skip(3).Select(x => x.Name));
        Assert.All(result.Scenarios.Skip(3), x => Assert.Equal(BackupSplit.None, x.Split));
        Assert.True(result.Scenarios[4].UseFolder1);
        Assert.Null(plan.Added.Single(x => x.Id == fileBase).BackupScriptId);
    }

    [Fact] public void ImportReadsShortAndOddScenarioFiles()
    {
        var files = new Dictionary<string, string>
        {
            // No [TARGET] and [ARCHIVE] at all, no template: the first folder, no split and the default template.
            ["S0000010.bks"] = "[INFO]\r\nName=Короткий\r\n[FILE]\r\nFormatId=\r\n",
            ["S0000011.bks"] = Bks("Без первого каталога", "", "000000000011", "1", target: "UseLocalFolder1=0\r\nLocalFolder1=\r\n", archive: "SplitVolumePreset=9\r\n"),
            ["S0000012.bks"] = Bks("Тома 200", "", "000000000012", "2", archive: "SplitVolumePreset=4\r\nSplitVolume=0\r\n")
        };
        WriteOriginal("[S0000010]\r\nFILE=S0000010.bks\r\n[S0000011]\r\nFILE=S0000011.bks\r\n[S0000012]\r\nFILE=S0000012.bks\r\n[MAIN]\r\nID=UNKNOWN\r\n", files,
            smc: $"[{fileBase}]\r\nBackupScriptID=\r\nBackupPrefix=  П  \r\n");
        var plan = Preview(new BackupScenarioStore(Settings));
        var added = plan.Backup!.Result.Scenarios.Skip(3).ToArray();
        Assert.Equal(BackupScenarioSet.DumpId, plan.Backup.Result.DefaultId);
        Assert.Equal((BackupFormat.Dt, BackupName.DefaultTemplate, true, BackupSplit.None), (added[0].Format, added[0].Template, added[0].UseFolder1, added[0].Split));
        Assert.Equal((false, BackupSplit.None), (added[1].UseFolder1, added[1].Split));
        Assert.Equal(200L << 20, added[2].VolumeBytes);
        var entry = plan.Added.Single(x => x.Id == fileBase);
        Assert.Equal(((Guid?)null, "", "П"), (entry.BackupScriptId, entry.BackupFolder, entry.BackupPrefix));
    }

    [Fact] public void ImportOfAFreshProfileTakesTheOriginalsDefault()
    {
        WriteOriginal(list: $"[{OwnLegacy}]\r\nFILE=S6789AB0.bks\r\n[MAIN]\r\nID={OwnLegacy}\r\n", smc: "");
        var plan = Preview(new BackupScenarioStore(Settings));
        Assert.Equal(LegacyImport.LegacyBackupId(OwnLegacy), plan.Backup!.Result.DefaultId);
        // No BackupScripts folder: nothing about scenarios at all.
        Directory.Delete(Scripts, true);
        var none = Preview(new BackupScenarioStore(Settings));
        Assert.Null(none.Backup);
        Assert.DoesNotContain(none.Notices, x => x.Contains("bckscripts"));
    }

    [Fact] public void ImportKeepsTheDestinationsGeneralFolderAndADamagedStore()
    {
        WriteOriginal(smc: Smc(), cnf: "<Settings><Backup><BackupFolder>E:\\Общий</BackupFolder></Backup></Settings>");
        var plan = Preview(new BackupScenarioStore(Settings), local: new Library { BackupFolder = Absolute("mine") });
        Assert.Equal(Absolute("mine"), plan.Result.BackupFolder);
        Assert.Contains(plan.Notices, x => x.Contains("Общий каталог резервных копий назначения уже задан"));
        Assert.DoesNotContain(Preview(new BackupScenarioStore(Settings), local: new Library { BackupFolder = "E:\\Общий" }).Notices, x => x.Contains("Общий каталог резервных копий"));

        File.WriteAllText(new BackupScenarioStore(Settings).PathName, "damaged");
        var damaged = Preview(new BackupScenarioStore(Settings));
        Assert.Null(damaged.Backup);
        Assert.Contains(damaged.Notices, x => x.Contains("Сценарии резервирования не перенесены"));
        Assert.Contains(damaged.Notices, x => x.Contains("не переносятся") && x.Contains("BackupScriptID"));
    }

    [Fact] public void ADamagedSettingsFileStopsTheImportOnlyWithTheClients()
    {
        WriteOriginal(smc: Smc(), cnf: "not xml");
        var plan = Preview(new BackupScenarioStore(Settings), new LegacyImportOptions(Clients: false));
        Assert.Contains(plan.Notices, x => x.Contains("общий каталог резервных копий не перенесён"));
        Assert.Equal("", plan.Result.BackupFolder);
        File.WriteAllText(Path.Combine(Original, "settings.cnf"), "<Other/>");
        Assert.Contains(Preview(null, new LegacyImportOptions(Clients: false)).Notices, x => x.Contains("общий каталог резервных копий не перенесён"));
        Assert.Throws<InvalidDataException>(() => Preview(null));
        File.WriteAllText(Path.Combine(Original, "settings.cnf"), "not xml");
        Assert.Contains("Отключите перенос клиентов", Assert.Throws<InvalidDataException>(() => Preview(null)).Message);
    }

    [Fact] public void ApplyRefusesScenariosChangedAfterThePreview()
    {
        WriteOriginal(smc: Smc());
        var store = new BackupScenarioStore(Settings);
        var plan = Preview(store);
        // Another window saves the scenarios meanwhile.
        var other = new BackupScenarioStore(Settings); other.Load(); other.Save(BackupScenarioSet.Original() with { DefaultId = BackupScenarioSet.ArchiveId });
        Assert.Throws<IOException>(() => LegacyImport.Apply(plan, new LibraryStore(Settings), new ActivityStore(Settings)));
        Assert.False(File.Exists(Settings));
        Assert.Equal(BackupScenarioSet.ArchiveId, new BackupScenarioStore(Settings).Load().DefaultId);
    }

    // Codex audit, part 1, P2: an import that only maps a base to an existing scenario still refuses a removal made meanwhile.
    [Fact] public void ApplyRefusesAMappedScenarioRemovedAfterThePreview()
    {
        WriteOriginal(list: $"[{ConfigurationLegacy}]\r\nFILE=SE8141B9.bks\r\n[MAIN]\r\nID={DumpLegacy}\r\n",
            files: new() { ["SE8141B9.bks"] = Bks("Выгрузка конфигурации информационной базы (*.cf)", "Выгрузка конфигурации информационной базы в CF-формате", ConfigurationLegacy, "1") },
            smc: $"[{fileBase}]\r\nBackupScriptID={ConfigurationLegacy}\r\n");
        var store = new BackupScenarioStore(Settings);
        store.Load(); store.Save(BackupScenarioSet.Original());
        var plan = Preview(store);
        Assert.Equal(BackupScenarioSet.ConfigurationId, plan.Added.Single(x => x.Id == fileBase).BackupScriptId);
        Assert.False(plan.Backup!.Write);
        // Another window removes the CF scenario.
        var other = new BackupScenarioStore(Settings); var now = other.Load();
        other.Save(now with { Scenarios = [.. now.Scenarios.Where(x => x.Id != BackupScenarioSet.ConfigurationId)] });
        var removed = File.ReadAllBytes(other.PathName);
        Assert.Throws<IOException>(() => LegacyImport.Apply(plan, new LibraryStore(Settings), new ActivityStore(Settings)));
        Assert.False(File.Exists(Settings));
        Assert.Equal(removed, File.ReadAllBytes(other.PathName));
    }

    [Fact] public void AMappingWithoutChangesSavesTheBasesAndLeavesTheScenarios()
    {
        WriteOriginal(list: $"[{ConfigurationLegacy}]\r\nFILE=SE8141B9.bks\r\n",
            files: new() { ["SE8141B9.bks"] = Bks("Выгрузка конфигурации информационной базы (*.cf)", "Выгрузка конфигурации информационной базы в CF-формате", ConfigurationLegacy, "1") },
            smc: $"[{fileBase}]\r\nBackupScriptID={ConfigurationLegacy}\r\n");
        var store = new BackupScenarioStore(Settings);
        store.Load(); store.Save(BackupScenarioSet.Original());
        var before = File.ReadAllBytes(store.PathName);
        var plan = Preview(store);
        Assert.Null(LegacyImport.Apply(plan, new LibraryStore(Settings), new ActivityStore(Settings)).FavoriteError);
        Assert.Equal(before, File.ReadAllBytes(store.PathName));
        Assert.False(File.Exists(store.PathName + ".bak"));
        Assert.Equal(BackupScenarioSet.ConfigurationId, new LibraryStore(Settings).Load().Bases.Single(x => x.Id == fileBase).BackupScriptId);
    }

    [Fact] public void AFailedScenarioWriteLeavesTheBasesWithAWarning()
    {
        WriteOriginal(smc: Smc());
        var store = new BackupScenarioStore(Settings);
        store.Load(); store.Save(BackupScenarioSet.Original());
        var plan = Preview(store);
        Directory.CreateDirectory(store.PathName + ".bak");
        var applied = LegacyImport.Apply(plan, new LibraryStore(Settings), new ActivityStore(Settings));
        Assert.Contains("сценарии резервирования не перенесены", applied.FavoriteError);
        Assert.Equal(3, new BackupScenarioStore(Settings).Load().Scenarios.Count);
        Assert.Equal(LegacyImport.LegacyBackupId(OwnLegacy), new LibraryStore(Settings).Load().Bases.Single(x => x.Id == fileBase).BackupScriptId);
    }
}
