using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>The tests that run a backup or a restore share the one-operation guard of the process: they never run in parallel.</summary>
[CollectionDefinition("Backup operation", DisableParallelization = true)]
public sealed class BackupOperationCollection;

/// <summary>Stage 11, part 2: making a backup (DT/CF by the configurator, ZIP of the files), the journal and the rotation, the restore,
/// the busy check and def.usr. A fake client script stands for 1C.</summary>
[Collection("Backup operation")]
public sealed class BackupRunTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-backup-run-" + Guid.NewGuid().ToString("N"));
    private string Client => Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8.cmd" : "1cv8");
    private string Record => Path.Combine(root, "record.txt");
    private string BaseFolder => Path.Combine(root, "my base");
    private string Copies => Path.Combine(root, "my backups");
    private string Settings => Path.Combine(root, "profile", "library.json");
    private static readonly DateTime Morning = new(2026, 3, 5, 7, 8, 9);

    public BackupRunTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(BaseFolder);
        Directory.CreateDirectory(Path.Combine(root, "profile"));
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "base data");
        // The fake client: records its arguments, writes BN_FAKE_LOG to the /Out file, writes the file of /DumpIB or /DumpCfg, sleeps or
        // fails by BN_FAKE_MODE (sleepmade: writes the dump, then sleeps; nofile: succeeds without a dump). ASCII texts only (cmd code page).
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Client, string.Join("\r\n", "@echo off", "echo %*>>\"%BN_FAKE_RECORD%\"", "set \"out=\"", "set \"dump=\"", ":loop", "if \"%~1\"==\"\" goto done",
                "if /i \"%~1\"==\"/Out\" set \"out=%~2\"", "if /i \"%~1\"==\"/DumpIB\" set \"dump=%~2\"", "if /i \"%~1\"==\"/DumpCfg\" set \"dump=%~2\"", "shift", "goto loop", ":done",
                "if defined out if defined BN_FAKE_LOG echo %BN_FAKE_LOG%>\"%out%\"",
                "if defined BN_FAKE_TOUCH echo modified>\"%BN_FAKE_TOUCH%\"",
                "if \"%BN_FAKE_MODE%\"==\"sleepmade\" if defined dump echo partial>\"%dump%\"",
                "if \"%BN_FAKE_MODE%\"==\"sleepmade\" ping -n 40 127.0.0.1 >nul", "if \"%BN_FAKE_MODE%\"==\"sleep\" ping -n 40 127.0.0.1 >nul",
                "if \"%BN_FAKE_MODE%\"==\"fail\" exit /b 1", "if \"%BN_FAKE_MODE%\"==\"nofile\" exit /b 0",
                "if defined dump echo dump-data>\"%dump%\"", "exit /b 0"));
        else
        {
            File.WriteAllText(Client, string.Join("\n", "#!/bin/sh", "echo \"$@\" >> \"$BN_FAKE_RECORD\"", "out=''", "dump=''", "while [ $# -gt 0 ]; do",
                "  case \"$1\" in /Out) out=\"$2\";; /DumpIB|/DumpCfg) dump=\"$2\";; esac; shift; done",
                "[ -n \"$out\" ] && [ -n \"$BN_FAKE_LOG\" ] && echo \"$BN_FAKE_LOG\" > \"$out\"",
                "[ -n \"$BN_FAKE_TOUCH\" ] && echo modified > \"$BN_FAKE_TOUCH\"",
                "[ \"$BN_FAKE_MODE\" = sleepmade ] && [ -n \"$dump\" ] && echo partial > \"$dump\"",
                "[ \"$BN_FAKE_MODE\" = sleepmade ] && sleep 40", "[ \"$BN_FAKE_MODE\" = sleep ] && sleep 40",
                "[ \"$BN_FAKE_MODE\" = fail ] && exit 1", "[ \"$BN_FAKE_MODE\" = nofile ] && exit 0",
                "[ -n \"$dump\" ] && echo dump-data > \"$dump\"", "exit 0") + "\n");
            File.SetUnixFileMode(Client, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private readonly Guid baseId = Guid.NewGuid();
    private BaseEntry FileBase(string name = "Base") => new() { Id = baseId, Name = name, Location = BaseFolder };
    private static BaseEntry ServerBase => new() { Name = "Srv", Kind = ConnectionKind.Server, Location = "srv 1\\buh base" };
    private static BackupScenario Scenario(BackupFormat format = BackupFormat.Dt) => new() { Name = "S", Format = format };
    private BackupRequest Request(BackupScenario? scenario = null, BaseEntry? entry = null, string? folder = null) => new()
    {
        Client = Client, Base = entry ?? FileBase(), Scenario = scenario ?? Scenario(), Folder = folder ?? Copies,
        Values = new BackupMacroValues((entry ?? FileBase()).Name, "", "PC", Morning)
    };
    private BackupPlan Prepared(BackupRequest request, string mode = "", string log = "")
    {
        var plan = BackupRun.Plan(request);
        plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_MODE"] = mode; plan.Environment["BN_FAKE_LOG"] = log;
        return plan;
    }
    // The journal is required by the run; a test that does not look at it gets the journal of its temporary profile.
    private Task<BackupResult> RunBackup(BackupPlan plan, BackupJournalStore? journal = null, Action<BackupProgress>? progress = null,
        TimeSpan? timeout = null, CancellationToken cancel = default) =>
        BackupRun.Run(plan, journal ?? new BackupJournalStore(Settings), progress, timeout, cancel);
    private string[] Leftovers(string folder) => Directory.Exists(folder)
        ? [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(x => x.Contains(".bn-") || x.StartsWith("DumpLog_"))] : [];

    // ---- The plan ----

    [Fact] public void PlanBuildsTheConfiguratorArgumentsForEveryLogin()
    {
        var file = BackupRun.Plan(Request());
        Assert.Equal(["DESIGNER", "/F", BaseFolder, "/DisableStartupDialogs", "/DumpIB"], file.Arguments);
        Assert.Equal(Path.Combine(Copies, "Base_20260305.dt"), file.Target);
        Assert.Empty(file.Existing); Assert.Empty(file.Copies);
        var user = BackupRun.Plan(Request() with { Credentials = new("Иванов И.", "p \"a' b") });
        Assert.Equal(["DESIGNER", "/F", BaseFolder, "/DisableStartupDialogs", "/N", "Иванов И.", "/P", "p \"a' b", "/WA-", "/DumpIB"], user.Arguments);
        Assert.DoesNotContain("p \"a' b", user.ShownArguments); Assert.Contains("***", user.ShownArguments);
        Assert.Equal(["/N", "u", "/WA-"], BackupRun.Plan(Request() with { Credentials = new("u") }).Arguments.Skip(4).Take(3));
        Assert.Equal("/WA+", BackupRun.Plan(Request() with { Credentials = new("u", "p", true) }).Arguments[4]);
        // «Запрашивать имя и пароль» without a user: /WA- alone, so 1C does not log in by the OS account; OS authentication wins.
        Assert.Equal(["/WA-", "/DumpIB"], BackupRun.Plan(Request() with { Credentials = new(AskCredentials: true) }).Arguments.Skip(4));
        Assert.Equal(["/WA+", "/DumpIB"], BackupRun.Plan(Request() with { Credentials = new(OsAuthentication: true, AskCredentials: true) }).Arguments.Skip(4));
        Assert.Equal(["/DumpIB"], BackupRun.Plan(Request() with { Credentials = new() }).Arguments.Skip(4));
        // A server base: server\name as it is, CF unloads the configuration.
        var server = BackupRun.Plan(Request(Scenario(BackupFormat.Cf), ServerBase));
        Assert.Equal(["DESIGNER", "/S", "srv 1\\buh base", "/DisableStartupDialogs", "/DumpCfg"], server.Arguments);
        Assert.EndsWith(".cf", server.FileName);
        // ZIP needs no client and no arguments.
        Assert.Empty(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }).Arguments);
    }

    [Fact] public void CredentialsComeFromTheDesignerLoginOfTheBase()
    {
        var entry = FileBase() with { Authentication = new AuthenticationSettings { DesignerLogin = true, DesignerUser = "Admin", EnterpriseLogin = true, EnterpriseUser = "Other" } };
        Assert.Equal(new BackupCredentials("Admin", "secret"), BackupCredentials.FromBase(entry, x => x == PasswordRole.Designer ? "secret" : "wrong"));
        Assert.Equal(new BackupCredentials("Admin", ""), BackupCredentials.FromBase(entry, _ => null));
        Assert.Equal(new BackupCredentials(OsAuthentication: true), BackupCredentials.FromBase(entry with { Authentication = entry.Authentication! with { OsAuthentication = true } }, _ => "x"));
        Assert.Equal(new BackupCredentials(), BackupCredentials.FromBase(FileBase(), _ => "x"));
        Assert.Equal(new BackupCredentials(AskCredentials: true), BackupCredentials.FromBase(FileBase() with { AskCredentials = true }, _ => "x"));
        Assert.Equal(new BackupCredentials("Admin", "s", AskCredentials: true), BackupCredentials.FromBase(entry with { AskCredentials = true }, _ => "s"));
        Assert.Equal(new BackupCredentials(), BackupCredentials.FromBase(entry with { Authentication = entry.Authentication! with { DesignerLogin = false } }, _ => "x"));
    }

    [Fact] public void PlanResolvesNamesAndFoldersAndRefusesWhatCannotRun()
    {
        void Refused(BackupRequest request, string fragment) => Assert.Contains(fragment, Assert.Throws<ArgumentException>(() => BackupRun.Plan(request)).Message);
        // Macros in the folder; a typed name gets the extension of the format.
        var plan = BackupRun.Plan(Request(folder: Path.Combine(root, "{IBName}", "{Y4}")) with { FileName = "my copy" });
        Assert.Equal(Path.Combine(root, "Base", "2026", "my copy.dt"), plan.Target);
        Assert.Equal("x.DT", BackupRun.Plan(Request() with { FileName = "x.DT" }).FileName);
        Refused(Request() with { FileName = "a/b" }, "недопустимые");
        Refused(Request() with { FileName = ".." }, "недопустимые");
        Refused(Request(folder: "relative"), "полный путь");
        Refused(Request(folder: ""), "Выгрузить в каталог");
        Refused(Request() with { Client = Path.Combine(root, "none.exe") }, "Клиент 1С не найден");
        var thin = Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "x");
        Refused(Request() with { Client = thin }, "толстый клиент");
        Refused(Request(Scenario(BackupFormat.Zip), ServerBase), "только для файлового");
        Refused(Request(entry: new BaseEntry { Name = "W", Kind = ConnectionKind.Web, Location = "https://srv/b" }), "веб-сервере");
        Refused(Request(folder: BaseFolder), "не должен совпадать");
        Refused(Request(entry: FileBase() with { Location = Path.Combine(root, "missing") }), "не найден");
        File.Delete(Path.Combine(BaseFolder, "1Cv8.1CD"));
        Refused(Request(), "нет файла информационной базы");
    }

    // ---- DT and CF ----

    [Fact] public async Task DumpWritesUnderATemporaryNameAndRenamesIt()
    {
        var plan = Prepared(Request(), log: "dump done");
        var shown = new List<BackupProgress>();
        var result = await RunBackup(plan, progress: shown.Add);
        Assert.True(result.Success, result.Message);
        Assert.Equal("dump-data", File.ReadAllText(plan.Target).Trim());
        Assert.Equal([plan.Target], result.Files);
        Assert.Contains("dump done", result.Log);
        Assert.Empty(Leftovers(Copies));
        Assert.Contains(shown, x => x.Stage.StartsWith("Выгрузка информационной базы"));
        // 1C got a temporary name with the extension of the format in the target folder; its /Out log goes to a private temporary
        // folder (stage 12, Codex audit of the lock fix), not next to the backups.
        var recorded = File.ReadAllText(Record);
        Assert.Contains("/DumpIB", recorded); Assert.Contains("Base_20260305.bn-partial-", recorded);
        Assert.DoesNotContain("DumpLog_", recorded); Assert.Contains("bn-1c-", recorded);
        Assert.Contains("my backups", recorded); Assert.Contains("my base", recorded);
    }

    [Fact] public async Task DumpFailureKeepsTheLogWithoutThePassword()
    {
        var plan = Prepared(Request() with { Credentials = new("Admin", "Secret42") }, "fail", "Error: wrong password Secret42");
        var result = await RunBackup(plan);
        Assert.False(result.Success);
        Assert.DoesNotContain("Secret42", result.Message + result.Log);
        Assert.Contains("***", result.Message);
        Assert.NotNull(result.LogPath);
        Assert.DoesNotContain("Secret42", File.ReadAllText(result.LogPath!));
        Assert.False(File.Exists(plan.Target));
        Assert.DoesNotContain(Leftovers(Copies), x => x.Contains(".bn-"));
        // Without a log the exit code is told; without a file, that 1C made none.
        File.Delete(result.LogPath!);
        Assert.Contains("кодом 1", (await RunBackup(Prepared(Request(), "fail"))).Message);
        Assert.Contains("не создала файл", (await RunBackup(Prepared(Request(), "nofile"))).Message);
        Assert.False(File.Exists(plan.Target));
    }

    [Fact] public async Task CancelAndTimeoutStop1CAndLeaveNoPartialFile()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var plan = Prepared(Request(), "sleepmade");
        var cancelled = await RunBackup(plan, cancel: cancel.Token);
        Assert.False(cancelled.Success); Assert.Contains("отменена", cancelled.Message);
        Assert.False(File.Exists(plan.Target)); Assert.Empty(Leftovers(Copies));
        var timedOut = await RunBackup(Prepared(Request(), "sleep"), timeout: TimeSpan.FromSeconds(1));
        Assert.False(timedOut.Success); Assert.Contains("не завершила работу", timedOut.Message);
        Assert.Empty(Leftovers(Copies));
        // Cancelled before the start: nothing runs.
        Assert.Contains("отменена", (await RunBackup(Prepared(Request()), cancel: new CancellationToken(true))).Message);
    }

    [Fact] public async Task AnExistingFileIsReplacedOnlyWhenAgreed()
    {
        Directory.CreateDirectory(Copies);
        var target = Path.Combine(Copies, "Base_20260305.dt");
        File.WriteAllText(target, "old");
        var plan = Prepared(Request());
        Assert.Equal([target], plan.Existing);
        var refused = await RunBackup(plan);
        Assert.False(refused.Success); Assert.True(refused.Exists);
        Assert.Equal("old", File.ReadAllText(target));
        var replaced = await RunBackup(Prepared(Request() with { Overwrite = true }));
        Assert.True(replaced.Success, replaced.Message);
        Assert.Equal("dump-data", File.ReadAllText(target).Trim());
        Assert.Empty(Leftovers(Copies));
    }

    [Fact] public void AFailedRenamePutsTheOldFilesBack()
    {
        Directory.CreateDirectory(Copies);
        var (first, second) = (Path.Combine(Copies, "a.zip.001"), Path.Combine(Copies, "a.zip.002"));
        File.WriteAllText(first, "old one"); File.WriteAllText(second, "old two");
        var temporary = Path.Combine(Copies, "new.tmp"); File.WriteAllText(temporary, "new one");
        var warnings = new List<string>();
        // The second temporary file is missing: the first new file goes and both old ones stay.
        Assert.Throws<FileNotFoundException>(() => BackupRun.Commit([(temporary, first), (Path.Combine(Copies, "missing.tmp"), second)], true, warnings));
        Assert.Equal(("old one", "old two"), (File.ReadAllText(first), File.ReadAllText(second)));
        Assert.Empty(warnings);
        Assert.DoesNotContain(Directory.EnumerateFiles(Copies), x => x.Contains(".bn-old-"));
        // Without the agreement an existing name is refused before anything moves.
        File.WriteAllText(temporary, "new one");
        Assert.Throws<FileExistsException>(() => BackupRun.Commit([(temporary, first)], false, warnings));
        Assert.Equal("old one", File.ReadAllText(first));
    }

    [Fact] public async Task TheBackupIsCopiedIntoTheFoldersTwoAndThree()
    {
        var second = Path.Combine(root, "second {IBName}");
        var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "a file where the folder should be");
        var scenario = Scenario() with { UseFolder2 = true, Folder2 = second, UseFolder3 = true, Folder3 = blocked };
        var journal = new BackupJournalStore(Settings);
        var plan = Prepared(Request(scenario));
        Assert.Equal([Path.Combine(root, "second Base"), blocked], plan.Copies);
        var result = await RunBackup(plan, journal);
        Assert.True(result.Success, result.Message);
        var copy = Path.Combine(root, "second Base", "Base_20260305.dt");
        Assert.Equal("dump-data", File.ReadAllText(copy).Trim());
        Assert.Equal([plan.Target, copy], result.Files);
        // A copy that fails is a warning: the backup itself is made.
        Assert.Contains(result.Warnings, x => x.Contains(blocked));
        var record = Assert.Single(journal.Load().Records);
        Assert.Equal([plan.Target, copy], record.Files);
        Assert.Equal((plan.Request.Base.Id, scenario.Id, BackupFormat.Dt), (record.BaseId, record.ScenarioId, record.Format));
        Assert.Equal(new FileInfo(plan.Target).Length, record.Size);
        Assert.Equal(new DateTimeOffset(Morning), record.Time);
        Assert.Empty(Leftovers(Path.Combine(root, "second Base")));
        // Folders 2 and 3 that are the folder of the backup or each other are not copied twice.
        Assert.Empty(BackupRun.Plan(Request(scenario with { Folder2 = Copies, Folder3 = Copies + Path.DirectorySeparatorChar })).Copies);
        Assert.Single(BackupRun.Plan(Request(scenario with { Folder3 = second })).Copies);
        Assert.Empty(BackupRun.Plan(Request(scenario with { UseFolder2 = false, UseFolder3 = false })).Copies);
        // Codex audit, part 3: under a cleared «Выполнить выгрузку на компьютер или сетевой диск» folders 2 and 3 are not copied to,
        // as the editor shows them disabled; the backup itself still goes to its folder.
        var off = BackupRun.Plan(Request(scenario with { UseFolder1 = false }));
        Assert.Empty(off.Copies);
        Assert.Equal(plan.Target, off.Target);
    }

    [Fact] public async Task OnlyOneOperationRunsAtATime()
    {
        using (var lease = BackupOperation.TryBegin())
        {
            Assert.NotNull(lease); Assert.True(BackupOperation.Running);
            Assert.Null(BackupOperation.TryBegin());
            Assert.Equal(BackupOperation.BusyMessage, (await RunBackup(Prepared(Request()))).Message);
            var dump = Path.Combine(root, "a.dt"); File.WriteAllText(dump, "x");
            Assert.Equal(BackupOperation.BusyMessage, (await BackupRestore.Run(BackupRestore.Plan(new RestoreRequest { Client = Client, Base = FileBase(), File = dump }))).Message);
        }
        Assert.False(BackupOperation.Running);
        Assert.True((await RunBackup(Prepared(Request()))).Success);
    }

    // ---- ZIP ----

    [Theory]
    [InlineData(BackupCompression.Save, CompressionLevel.NoCompression)]
    [InlineData(BackupCompression.Fast, CompressionLevel.Fastest)]
    [InlineData(BackupCompression.Normal, CompressionLevel.Optimal)]
    [InlineData(BackupCompression.Maximum, CompressionLevel.SmallestSize)]
    [InlineData(BackupCompression.Ultra, CompressionLevel.SmallestSize)]
    public void CompressionFollowsTheScenario(BackupCompression compression, CompressionLevel level) => Assert.Equal(level, BackupRun.Level(compression));

    [Fact] public async Task ZipHoldsTheFilesOfTheBaseWithoutItsBackupFolder()
    {
        Directory.CreateDirectory(Path.Combine(BaseFolder, "sub"));
        File.WriteAllText(Path.Combine(BaseFolder, "sub", "a.txt"), new string('a', 10000));
        var inside = Path.Combine(BaseFolder, "Backup");
        Directory.CreateDirectory(inside); File.WriteAllText(Path.Combine(inside, "older.zip"), "older");
        var journal = new BackupJournalStore(Settings);
        var plan = BackupRun.Plan(Request(Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Maximum }, folder: inside) with { Client = "" });
        var shown = new List<BackupProgress>();
        var result = await RunBackup(plan, journal, shown.Add);
        Assert.True(result.Success, result.Message);
        using (var archive = ZipFile.OpenRead(plan.Target))
        {
            Assert.Equal(["1Cv8.1CD", "sub/a.txt"], archive.Entries.Select(x => x.FullName).Order(StringComparer.Ordinal));
            using var reader = new StreamReader(archive.GetEntry("sub/a.txt")!.Open());
            Assert.Equal(new string('a', 10000), reader.ReadToEnd());
            // MAXIMUM compresses: 10000 equal bytes take far less.
            Assert.True(archive.GetEntry("sub/a.txt")!.CompressedLength < 1000);
        }
        Assert.Equal(BackupFormat.Zip, Assert.Single(journal.Load().Records).Format);
        Assert.Contains(shown, x => x.Total > 0 && x.Done == x.Total);
        Assert.Empty(Leftovers(inside));
        // SAVE stores the bytes as they are.
        var stored = BackupRun.Plan(Request(Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Save }) with { Client = "" });
        Assert.True((await RunBackup(stored)).Success);
        using (var archive = ZipFile.OpenRead(stored.Target)) Assert.Equal(10000, archive.GetEntry("sub/a.txt")!.CompressedLength);
    }

    [Fact] public async Task SplitZipIsWrittenInVolumesThatJoinIntoTheArchive()
    {
        var data = new byte[(int)(2.5 * 1024 * 1024)];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD"), data);
        var scenario = Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Save, Split = BackupSplit.Custom, VolumeSize = 1 };
        Directory.CreateDirectory(Copies);
        // A fourth volume of an earlier backup of the same name would be joined with the new ones: it goes when replacing.
        var stale = Path.Combine(Copies, "Base_20260305.zip.004"); File.WriteAllText(stale, "stale");
        var plan = BackupRun.Plan(Request(scenario) with { Client = "", Overwrite = true });
        Assert.Equal([stale], plan.Existing);
        var result = await RunBackup(plan);
        Assert.True(result.Success, result.Message);
        var volumes = Enumerable.Range(1, 3).Select(x => Path.Combine(Copies, $"Base_20260305.zip.{x:000}")).ToList();
        Assert.Equal(volumes, result.Files);
        Assert.False(File.Exists(stale)); Assert.False(File.Exists(Path.Combine(Copies, "Base_20260305.zip")));
        Assert.Equal(1024 * 1024, new FileInfo(volumes[0]).Length);
        Assert.Empty(Leftovers(Copies));
        // Joined byte by byte (copy /b, cat) the volumes are the archive.
        var joined = Path.Combine(root, "joined.zip");
        using (var output = File.Create(joined)) foreach (var volume in volumes) using (var input = File.OpenRead(volume)) input.CopyTo(output);
        using (var archive = ZipFile.OpenRead(joined))
        using (var entry = archive.GetEntry("1Cv8.1CD")!.Open())
        using (var copy = new MemoryStream()) { entry.CopyTo(copy); Assert.Equal(data, copy.ToArray()); }
        // Without replacing, the volumes there refuse the run.
        Assert.True((await RunBackup(BackupRun.Plan(Request(scenario) with { Client = "" }))).Exists);
    }

    [Fact] public void VolumeStreamsSeekAcrossVolumeBoundaries()
    {
        var prefix = Path.Combine(root, "tiny.zip");
        var texts = Enumerable.Range(0, 40).ToDictionary(x => $"dir/file{x}.txt", x => new string((char)('a' + x % 26), x * 37));
        string[] parts;
        using (var stream = new VolumeWriteStream(prefix, 100))
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in texts) using (var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open())) writer.Write(text);
            parts = [.. stream.Parts];
        }
        Assert.True(parts.Length > 10);
        Assert.All(parts.SkipLast(1), x => Assert.Equal(100, new FileInfo(x).Length));
        Assert.Equal(prefix + ".001", parts[0]);
        using var read = new ZipArchive(new VolumeReadStream(parts), ZipArchiveMode.Read);
        foreach (var (name, text) in texts) using (var reader = new StreamReader(read.GetEntry(name)!.Open())) Assert.Equal(text, reader.ReadToEnd());
    }

    [Fact] public async Task ZipOfABusyBaseIsRefused()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var plan = BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" });
        BackupResult result;
        using (new FileStream(Path.Combine(BaseFolder, "1Cv8.1CD"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            result = await RunBackup(plan);
        Assert.False(result.Success);
        Assert.Contains("сеансы 1С", result.Message);
        Assert.False(File.Exists(plan.Target)); Assert.Empty(Leftovers(Copies));
    }

    // ---- The journal and the rotation ----

    private static BackupRecord Made(Guid baseId, Guid scenario, int day, params string[] files) =>
        new() { BaseId = baseId, ScenarioId = scenario, Format = BackupFormat.Dt, Time = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), Files = [.. files], Size = 1 };

    private string Touch(string name) { var path = Path.Combine(Copies, name); Directory.CreateDirectory(Copies); File.WriteAllText(path, name); return path; }

    [Fact] public void JournalKeepsUnknownFieldsAndRefusesADamagedFile()
    {
        var store = new BackupJournalStore(Settings);
        Assert.Empty(store.Load().Records);
        Assert.Equal(Path.Combine(root, "profile", "backup-journal.json"), store.PathName);
        var record = Made(Guid.NewGuid(), Guid.NewGuid(), 1, Path.Combine(root, "a.dt"));
        store.Add(record);
        var json = JsonNode.Parse(File.ReadAllText(store.PathName))!;
        json["Future"] = "kept"; json["Records"]![0]!["Note"] = "kept too";
        File.WriteAllText(store.PathName, json.ToJsonString());
        store.Add(Made(record.BaseId, record.ScenarioId, 2, Path.Combine(root, "b.dt")));
        var again = JsonNode.Parse(File.ReadAllText(store.PathName))!;
        Assert.Equal("kept", again["Future"]!.GetValue<string>());
        Assert.Equal("kept too", again["Records"]![0]!["Note"]!.GetValue<string>());
        var loaded = store.Load();
        Assert.Equal(2, loaded.Records.Count);
        Assert.Equal(record with { Extra = loaded.Records[0].Extra, Files = loaded.Records[0].Files, Stamps = loaded.Records[0].Stamps }, loaded.Records[0]);
        Assert.Equal(record.Files, loaded.Records[0].Files);
        Assert.Equal(2, loaded.Of(record.BaseId).First().Time.Day);
        Assert.True(File.Exists(store.PathName + ".bak"));
        // A damaged journal is never overwritten.
        File.WriteAllText(store.PathName, "{ not json");
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Add(Made(Guid.NewGuid(), Guid.NewGuid(), 3, Path.Combine(root, "c.dt"))));
        Assert.Equal("{ not json", File.ReadAllText(store.PathName));
        File.WriteAllText(store.PathName, "{\"Version\":1,\"Records\":[{\"Files\":[]}]}");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact] public void TwoWindowsAddingAtOnceKeepEveryRecord()
    {
        var first = new BackupJournalStore(Settings); var second = new BackupJournalStore(Settings);
        var baseId = Guid.NewGuid();
        // Two writers at a time, as two windows: 20 unbounded workers queued longer than the lock's 1.5 s on a slow CI disk
        // (Actions ubuntu 09.11: «Файл занят другим окном»), which the product never meets.
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i => (i % 2 == 0 ? first : second).Add(Made(baseId, Guid.Empty, 1 + i, Path.Combine(root, $"{i}.dt"))));
        Assert.Equal(20, first.Load().Records.Count);
    }

    [Fact] public void AReplacedFileLeavesItsOlderRecord()
    {
        var store = new BackupJournalStore(Settings);
        var baseId = Guid.NewGuid();
        var a = Touch("a.dt"); var b = Touch("b.dt");
        store.Add(Made(baseId, Guid.Empty, 1, a, b));
        store.Add(Made(baseId, Guid.Empty, 2, a));
        var records = store.Load().Records;
        Assert.Equal([[b], [a]], records.Select(x => x.Files));
        store.Add(Made(baseId, Guid.Empty, 3, b));
        Assert.Equal([[a], [b]], store.Load().Records.Select(x => x.Files));
    }

    [Fact] public void RotationRemovesTheOldestCopiesOfThisBaseAndScenarioOnly()
    {
        var store = new BackupJournalStore(Settings);
        var (baseId, scenario, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var files = Enumerable.Range(1, 4).Select(x => Touch($"{x}.dt")).ToList();
        var copy = Touch("1-copy.dt");
        store.Add(Made(baseId, scenario, 1, files[0], copy));
        store.Add(Made(baseId, scenario, 3, files[2]));
        store.Add(Made(baseId, scenario, 2, files[1]));
        store.Add(Made(baseId, scenario, 4, files[3]));
        var otherBase = Made(other, scenario, 1, Touch("other.dt")); store.Add(otherBase);
        var otherScenario = Made(baseId, other, 1, Touch("scenario.dt")); store.Add(otherScenario);
        Assert.Empty(store.Rotate(baseId, scenario, 2));
        Assert.Equal([false, false, true, true, false], files.Append(copy).Select(File.Exists));
        var left = store.Load().Records;
        Assert.Equal([3, 4], left.Where(x => x.BaseId == baseId && x.ScenarioId == scenario).Select(x => x.Time.Day).Order());
        Assert.Equal(4, left.Count);
        Assert.Contains(left, x => x.Id == otherBase.Id); Assert.Contains(left, x => x.Id == otherScenario.Id);
        Assert.True(File.Exists(otherBase.Files[0]) && File.Exists(otherScenario.Files[0]));
        // 0 keeps all; nothing to remove writes nothing.
        Assert.Empty(store.Rotate(baseId, scenario, 0));
        Assert.Empty(store.Rotate(baseId, scenario, 5));
    }

    [Fact] public void RotationReportsWhatCannotBeRemovedAndDropsWhatIsGone()
    {
        var store = new BackupJournalStore(Settings);
        var (baseId, scenario) = (Guid.NewGuid(), Guid.NewGuid());
        var gone = Path.Combine(Copies, "gone.dt");
        var unreachable = Path.Combine(root, "offline share", "x.dt");
        var held = Touch("held.dt");
        store.Add(Made(baseId, scenario, 1, gone));
        store.Add(Made(baseId, scenario, 2, unreachable));
        store.Add(Made(baseId, scenario, 3, held));
        store.Add(Made(baseId, scenario, 4, Touch("new.dt")));
        IReadOnlyList<string> warnings;
        using (OperatingSystem.IsWindows() ? new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
            warnings = store.Rotate(baseId, scenario, 1);
        Assert.Contains(warnings, x => x.Contains("недоступен") && x.Contains(unreachable));
        var left = store.Load().Records.Select(x => x.Files.Single()).ToList();
        Assert.DoesNotContain(gone, left);
        Assert.Contains(unreachable, left);
        // The journal is written before the files are removed: a file that stays is named and is no longer listed (Codex recheck).
        Assert.DoesNotContain(held, left);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(warnings, x => x.Contains(held) && x.Contains("больше не числится"));
            Assert.True(File.Exists(held));
        }
        else Assert.False(File.Exists(held));
    }

    [Fact] public void DeletingARecordRemovesItsFiles()
    {
        var store = new BackupJournalStore(Settings);
        var record = Made(Guid.NewGuid(), Guid.Empty, 1, Touch("a.zip.001"), Touch("a.zip.002"), Path.Combine(root, "gone folder", "a.zip.001"));
        var other = Made(record.BaseId, Guid.Empty, 2, Touch("b.dt"));
        store.Add(record); store.Add(other);
        Assert.Empty(store.Delete(record.Id));
        Assert.False(File.Exists(record.Files[0]) || File.Exists(record.Files[1]));
        Assert.Equal([other.Id], store.Load().Records.Select(x => x.Id));
        Assert.Empty(store.Delete(Guid.NewGuid()));
        if (!OperatingSystem.IsWindows()) return;
        IReadOnlyList<string> warnings;
        using (new FileStream(other.Files[0], FileMode.Open, FileAccess.Read, FileShare.Read)) warnings = store.Delete(other.Id);
        Assert.Contains(warnings, x => x.Contains(other.Files[0]) && x.Contains("больше не числится"));
        Assert.Empty(store.Load().Records);
        Assert.True(File.Exists(other.Files[0]));
    }

    [Fact] public async Task ASuccessfulBackupRotatesByTheScenario()
    {
        var journal = new BackupJournalStore(Settings);
        var scenario = Scenario() with { KeepCopies = 1, Template = "{IBName}_{HH}{NN}" };
        var first = await RunBackup(Prepared(Request(scenario)), journal);
        Assert.True(first.Success, first.Message);
        var second = await RunBackup(Prepared(Request(scenario) with { Values = new("Base", "", "PC", Morning.AddMinutes(1)) }), journal);
        Assert.True(second.Success, second.Message);
        Assert.False(File.Exists(first.Files[0])); Assert.True(File.Exists(second.Files[0]));
        Assert.Equal(second.Record!.Id, Assert.Single(journal.Load().Records).Id);
    }

    // ---- Restore ----

    private RestoreRequest Restore(string file, BaseEntry? entry = null) => new() { Client = Client, Base = entry ?? FileBase(), File = file };

    private string Archive(string name, params (string Entry, string Text)[] entries)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries) using (var writer = new StreamWriter(archive.CreateEntry(entry).Open())) writer.Write(text);
        return path;
    }

    [Fact] public void RestoreRefusesWithTheOriginalsMessages()
    {
        void Refused(RestoreRequest request, string text) => Assert.Contains(text, Assert.Throws<ArgumentException>(() => BackupRestore.Plan(request)).Message);
        Refused(Restore(""), "Не выбран файл!");
        Refused(Restore("  "), "Не выбран файл!");
        Refused(Restore(Path.Combine(root, "none.dt")), "Выбранный файл не существует!");
        Refused(Restore("relative.dt"), "Выбранный файл не существует!");
        var text = Path.Combine(root, "x.txt"); File.WriteAllText(text, "x");
        Refused(Restore(text), "Неизвестный формат");
        var zip = Archive("a.zip", ("1Cv8.1CD", "x"));
        Refused(Restore(zip, ServerBase), "только для файлового");
        Refused(Restore(zip, FileBase() with { Location = Path.Combine(root, "missing") }), "Каталог базы не найден");
        var second = Path.Combine(root, "v.zip.002"); File.WriteAllText(second, "x");
        Refused(Restore(second), "первый том");
        File.WriteAllText(Path.Combine(root, "v.zip.001"), "x"); File.WriteAllText(Path.Combine(root, "v.zip.004"), "x");
        Refused(Restore(second), "v.zip.003");
        Assert.Equal(BackupFormat.Zip, BackupRestore.FormatOf("a.ZIP.001"));
        Assert.Null(BackupRestore.FormatOf("a.001"));
    }

    [Fact] public async Task RestoreRunsTheConfiguratorForDtAndCf()
    {
        var dump = Path.Combine(root, "data dump.dt"); File.WriteAllText(dump, "x");
        var dt = BackupRestore.Plan(Restore(dump) with { Credentials = new("Admin", "pw") });
        Assert.Equal(["DESIGNER", "/F", BaseFolder, "/DisableStartupDialogs", "/N", "Admin", "/P", "pw", "/WA-", "/RestoreIB", dump], dt.Arguments);
        var cf = Path.Combine(root, "conf.cf"); File.WriteAllText(cf, "x");
        var cfPlan = BackupRestore.Plan(Restore(cf, ServerBase));
        Assert.Equal(["DESIGNER", "/S", "srv 1\\buh base", "/DisableStartupDialogs", "/LoadCfg", cf, "/UpdateDBCfg"], cfPlan.Arguments);
        foreach (var plan in new[] { dt, cfPlan }) { plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_LOG"] = "loaded"; }
        var result = await BackupRestore.Run(dt);
        Assert.True(result.Success, result.Message); Assert.Contains("loaded", result.Log);
        Assert.True((await BackupRestore.Run(cfPlan)).Success);
        var recorded = File.ReadAllText(Record);
        Assert.Contains("/RestoreIB", recorded); Assert.Contains("/LoadCfg", recorded); Assert.Contains("/UpdateDBCfg", recorded);
        var failing = BackupRestore.Plan(Restore(dump) with { Credentials = new("Admin", "Secret42") });
        failing.Environment["BN_FAKE_RECORD"] = Record; failing.Environment["BN_FAKE_MODE"] = "fail"; failing.Environment["BN_FAKE_LOG"] = "bad password Secret42";
        var failed = await BackupRestore.Run(failing);
        Assert.False(failed.Success); Assert.DoesNotContain("Secret42", failed.Message + failed.Log);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetTempPath(), "bn-restore-*.log"), x => File.ReadAllText(x).Contains("Secret42"));
    }

    [Fact] public async Task ZipRestoreReplacesTheFilesOfTheArchiveAndKeepsTheOthers()
    {
        File.WriteAllText(Path.Combine(BaseFolder, "keep.log"), "mine");
        var zip = Archive("backup.zip", ("1Cv8.1CD", "restored"), ("sub/inner.bin", "inner"));
        var shown = new List<BackupProgress>();
        var result = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)), shown.Add);
        Assert.True(result.Success, result.Message);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(BaseFolder, "sub", "inner.bin")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(BaseFolder, "keep.log")));
        Assert.Contains(shown, x => x.Stage.StartsWith("Распаковка"));
        // Nothing is left next to the base.
        Assert.DoesNotContain(Directory.EnumerateDirectories(root), x => Path.GetFileName(x).StartsWith('.'));
    }

    [Fact] public async Task ZipRestoreJoinsTheVolumesMadeByABackup()
    {
        var data = new byte[1500 * 1024]; new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD"), data);
        var scenario = Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Save, Split = BackupSplit.Custom, VolumeSize = 1 };
        var made = await RunBackup(BackupRun.Plan(Request(scenario) with { Client = "" }));
        Assert.True(made.Success, made.Message); Assert.Equal(2, made.Files.Count);
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "changed later");
        var plan = BackupRestore.Plan(Restore(made.Files[1]));
        Assert.Equal(made.Files, plan.Files);
        var result = await BackupRestore.Run(plan);
        Assert.True(result.Success, result.Message);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    [Fact] public async Task AFailedZipRestoreLeavesTheBaseAsItWas()
    {
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "original");
        Directory.CreateDirectory(Path.Combine(BaseFolder, "zz"));
        async Task Fails(string zip, string fragment)
        {
            var result = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)));
            Assert.False(result.Success); Assert.Contains(fragment, result.Message);
            Assert.Equal("original", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), x => Path.GetFileName(x).StartsWith('.'));
        }
        await Fails(Archive("nobase.zip", ("other.txt", "x")), "нет файла информационной базы");
        await Fails(Archive("slip.zip", ("1Cv8.1CD", "x"), ("../evil.txt", "x")), "Недопустимое имя");
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
        await Fails(Archive("abs.zip", ("1Cv8.1CD", "x"), ("C:/evil.txt", "x")), "Недопустимое имя");
        var broken = Path.Combine(root, "broken.zip"); File.WriteAllText(broken, "not a zip");
        await Fails(broken, "Загрузка не выполнена");
        // A file of the archive where the base has a folder: the replacement stops and puts back what it moved.
        await Fails(Archive("conflict.zip", ("1Cv8.1CD", "new"), ("zz", "file")), "каталог");
        Assert.False(File.Exists(Path.Combine(BaseFolder, "zz")) && !Directory.Exists(Path.Combine(BaseFolder, "zz")));
        // A busy base is refused before anything is unpacked.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            using (new FileStream(Path.Combine(BaseFolder, "1Cv8.1CD"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await Fails(Archive("good.zip", ("1Cv8.1CD", "new")), BaseBusyCheck.BusyForLoad);
        // Cancelled: nothing changes.
        var cancelled = await BackupRestore.Run(BackupRestore.Plan(Restore(Archive("good2.zip", ("1Cv8.1CD", "new")))), cancel: new CancellationToken(true));
        Assert.False(cancelled.Success); Assert.Contains("отменена", cancelled.Message);
        Assert.Equal("original", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    // Codex full audit 24.10, P2-5: an archive made on Windows may name the data file «1cv8.1cd». It was accepted without regard to
    // case and unpacked under its own name: on Linux next to the real 1Cv8.1CD, which stayed old while the restore reported success.
    // The one data file of the root is put back as 1Cv8.1CD; two spellings of it refuse the archive before anything changes.
    [Theory]
    [InlineData("1cv8.1cd")]
    [InlineData("1CV8.1CD")]
    [InlineData("1Cv8.1CD")]
    public async Task TheDataFileOfAnArchiveIsRestoredUnderItsOwnName(string spelling)
    {
        var zip = Archive("case.zip", (spelling, "restored"), ("1Cv8Log/1Cv8.lgf", "log"));
        var result = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)));
        Assert.True(result.Success, result.Message);
        Assert.Equal(["1Cv8.1CD"], Directory.GetFiles(BaseFolder).Select(Path.GetFileName));
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    [Fact] public async Task AnArchiveWithTwoSpellingsOfTheDataFileIsRefused()
    {
        var zip = Archive("both.zip", ("1cv8.1cd", "lower"), ("1Cv8.1CD", "upper"), ("1Cv8Log/1Cv8.lgf", "log"));
        var result = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)));
        Assert.False(result.Success);
        Assert.Contains("несколько файлов информационной базы", result.Message);
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.False(Directory.Exists(Path.Combine(BaseFolder, "1Cv8Log")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(root), x => Path.GetFileName(x).StartsWith('.'));
    }

    [Theory]
    [InlineData("1Cv8.1CD", true)]
    [InlineData("sub/a.bin", true)]
    [InlineData("sub\\a.bin", true)]
    [InlineData("../a", false)]
    [InlineData("sub/../../a", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/a", false)]
    [InlineData("a//b", false)]
    [InlineData(". /a", false)]
    public void ArchiveNamesThatLeaveTheFolderAreRefused(string name, bool allowed)
    {
        if (allowed) Assert.NotEmpty(BackupRestore.EntryPath(name));
        else Assert.Throws<InvalidDataException>(() => BackupRestore.EntryPath(name));
    }

    // ---- Busy check and def.usr ----

    [Fact] public void BusyCheckSeesAnOpenBaseFile()
    {
        Assert.Equal(BaseBusyState.NotChecked, BaseBusyCheck.Check(ServerBase).State);
        Assert.Equal(BaseBusyState.NotChecked, BaseBusyCheck.Check(FileBase() with { Location = Path.Combine(root, "missing") }).State);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        Assert.Equal(BaseBusyState.Free, BaseBusyCheck.Check(FileBase()).State);
        using (new FileStream(Path.Combine(BaseFolder, "1Cv8.1CD"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var busy = BaseBusyCheck.Check(FileBase());
            Assert.Equal(BaseBusyState.Busy, busy.State); Assert.NotEmpty(busy.Detail);
        }
        Assert.Equal(BaseBusyState.Free, BaseBusyCheck.Check(FileBase()).State);
        // The check removes nothing.
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    [Theory]
    [InlineData("\uFEFF{\"Администратор\"}", "Администратор")]
    [InlineData("{\"Иванов \"\"И\"\"\"}\r\n", "Иванов \"И\"")]
    [InlineData("{\"Admin\",1}", "Admin")]
    [InlineData("{Admin,1}", "Admin")]
    [InlineData("\r\n  \"Admin\"  \r\n", "Admin")]
    [InlineData("{\"\"}", null)]
    [InlineData("", null)]
    [InlineData("{}", null)]
    public void DefUsrGivesTheLastUser(string text, string? user) => Assert.Equal(user, LastUser.Parse(text));

    [Fact] public void DefUsrIsReadFromTheFolderOfTheBase()
    {
        var id = Guid.NewGuid();
        // Not in root itself: on Linux root/1cv8 is the fake client file (found by the Linux run of part 5).
        var first = Path.Combine(root, "settings", "1cv8"); var second = Path.Combine(root, "settings", "1Cv82");
        Assert.Null(LastUser.Read(id, [first, second]));
        Directory.CreateDirectory(Path.Combine(second, id.ToString()));
        File.WriteAllBytes(Path.Combine(second, id.ToString(), "def.usr"), [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{\"Бухгалтер\"}")]);
        Assert.Equal("Бухгалтер", LastUser.Read(id, [first, second]));
        Directory.CreateDirectory(Path.Combine(first, id.ToString()));
        File.WriteAllText(Path.Combine(first, id.ToString(), "def.usr"), "{\"Admin\"}");
        Assert.Equal("Admin", LastUser.Read(id, [first, second]));
        Assert.NotEmpty(LastUser.Roots());
    }

    // ---- Fixes of the Codex audit, part 2 ----

    private RestorePlan Touching(RestorePlan plan, string mode)
    {
        plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_MODE"] = mode;
        plan.Environment["BN_FAKE_TOUCH"] = Path.Combine(BaseFolder, "1Cv8.1CD");
        return plan;
    }
    private string[] Aside() => [.. Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(x => x.StartsWith('.'))];

    [Fact] public async Task AFailedDtOrCfLoadPutsTheFileBaseBack()
    {
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.cgr"), "cgr");
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        var cf = Path.Combine(root, "c.cf"); File.WriteAllText(cf, "x");
        var plan = BackupRestore.Plan(Restore(dump));
        Assert.True(plan.Recoverable);
        // 1C changes the base, then fails: the base comes back.
        var failed = await BackupRestore.Run(Touching(plan, "fail"));
        Assert.False(failed.Success); Assert.Contains("возвращена в прежнее состояние", failed.Message); Assert.Null(failed.Kept);
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Aside());
        // Cancelled and timed out while 1C works: the same.
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var cancelled = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(cf)), "sleep"), cancel: cancel.Token);
            Assert.False(cancelled.Success); Assert.Contains("отменена", cancelled.Message);
        }
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        var timedOut = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), "sleep"), timeout: TimeSpan.FromSeconds(1));
        Assert.False(timedOut.Success); Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Equal("cgr", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.cgr")));
        Assert.Empty(Aside());
        // Success keeps what 1C made and removes the copy.
        var loaded = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), ""));
        Assert.True(loaded.Success, loaded.Message);
        Assert.Equal("modified", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")).Trim());
        Assert.Empty(Aside());
    }

    [Fact] public async Task ALoadIsRefusedWhenTheSafetyCopyCannotBeMade()
    {
        if (!OperatingSystem.IsWindows()) return;
        var other = Path.Combine(BaseFolder, "1Cv8.cgr"); File.WriteAllText(other, "cgr");
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        RestoreResult result;
        using (new FileStream(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), ""));
        Assert.False(result.Success); Assert.Contains("страховочную копию", result.Message);
        // 1C was not started and the base is as it was.
        Assert.False(File.Exists(Record));
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Aside());
    }

    [Fact] public async Task AServerLoadCannotBeUndoneAndSaysSo()
    {
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        var plan = BackupRestore.Plan(Restore(dump, ServerBase));
        Assert.False(plan.Recoverable);
        Assert.Contains("необратима", BackupRestore.ServerNotice);
        plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_MODE"] = "fail";
        var result = await BackupRestore.Run(plan);
        Assert.False(result.Success); Assert.Contains("Серверная база могла измениться", result.Message);
    }

    // A folder link inside the base: a junction on Windows (made without privileges), a symbolic link elsewhere.
    private static bool MakeFolderLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return true; }
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit(10000);
        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    [Fact] public async Task ZipRestoreNeverWritesThroughALinkInTheBase()
    {
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "important.txt"), "keep me");
        Assert.True(MakeFolderLink(Path.Combine(BaseFolder, "sub"), outside));
        var zip = Archive("linked.zip", ("1Cv8.1CD", "new"), ("sub/important.txt", "evil"));
        var result = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)));
        Assert.False(result.Success); Assert.Contains("ссылка", result.Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "important.txt")));
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Aside());
        Directory.Delete(Path.Combine(BaseFolder, "sub"));
    }

    [Fact] public async Task TheBaseIsHeldForTheWholeZipBackupAndRestore()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        File.WriteAllText(Path.Combine(BaseFolder, "later.bin"), new string('x', 3 << 20));
        var data = Path.Combine(BaseFolder, "1Cv8.1CD");
        FileStream? session = null;
        var refused = 0;
        // A «session» tries to open the base while the files are read.
        void Try(BackupProgress step)
        {
            if (session is not null || step.Done == 0 && step.Total > 0) return;
            try { session = new FileStream(data, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (IOException) { refused++; }
        }
        BackupResult made;
        try { made = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }), progress: Try); }
        finally { session?.Dispose(); }
        if (OperatingSystem.IsWindows()) { Assert.True(made.Success, made.Message); Assert.True(refused > 0); }
        // Linux has no mandatory lock: the session that opened the base meanwhile is found after the archive and fails it.
        else { Assert.False(made.Success); Assert.Contains("открыта", made.Message); }
        if (!OperatingSystem.IsWindows()) return;
        // The replacement of a restore holds it too.
        session = null; refused = 0;
        var restored = await BackupRestore.Run(BackupRestore.Plan(Restore(made.Files[0])), step => { if (step.Stage.StartsWith("Замена")) Try(step); });
        session?.Dispose();
        Assert.True(restored.Success, restored.Message); Assert.Equal(1, refused);
    }

    [Fact] public async Task ALockedSurplusVolumeKeepsTheWholeOldSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = new byte[(int)(2.5 * 1024 * 1024)]; new Random(9).NextBytes(data);
        File.WriteAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD"), data);
        var scenario = Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Save, Split = BackupSplit.Custom, VolumeSize = 1 };
        var first = await RunBackup(BackupRun.Plan(Request(scenario) with { Client = "" }));
        Assert.Equal(3, first.Files.Count);
        var before = first.Files.Select(File.ReadAllBytes).ToList();
        File.WriteAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD"), data[..(1500 * 1024)]);
        BackupResult second;
        using (new FileStream(first.Files[2], FileMode.Open, FileAccess.Read, FileShare.Read))
            second = await RunBackup(BackupRun.Plan(Request(scenario) with { Client = "", Overwrite = true }));
        Assert.False(second.Success);
        Assert.Equal(before, first.Files.Select(File.ReadAllBytes).ToList());
        Assert.Empty(Leftovers(Copies));
    }

    [Fact] public void JournalComparesFilesByIdentityNotSpelling()
    {
        var store = new BackupJournalStore(Settings);
        var (id, scenario) = (Guid.NewGuid(), Guid.NewGuid());
        var a = Touch("a.dt"); var b = Touch("b.dt");
        store.Add(Made(id, scenario, 1, a, b));
        // The same file written another way leaves the older record.
        var alias = Path.Combine(Copies, ".", "sub", "..", OperatingSystem.IsWindows() ? "A.DT" : "a.dt");
        store.Add(Made(id, scenario, 2, alias));
        Assert.Equal([[b], [alias]], store.Load().Records.Select(x => x.Files));
        Assert.Empty(store.Rotate(id, scenario, 1));
        Assert.False(File.Exists(b)); Assert.True(File.Exists(a));
    }

    [Fact] public void ARollbackPutsTheOldestFileBackLast()
    {
        Directory.CreateDirectory(Copies);
        var final = Path.Combine(Copies, "x.dt"); File.WriteAllText(final, "old");
        var (t1, t2) = (Path.Combine(Copies, "t1"), Path.Combine(Copies, "t2"));
        File.WriteAllText(t1, "one"); File.WriteAllText(t2, "two");
        var warnings = new List<string>();
        // The second rename sets the first new file aside under the same name; the rollback goes in reverse and replaces, so the
        // original ends up under its name (Grok audit, part 2).
        Assert.Throws<FileNotFoundException>(() => BackupRun.Commit([(t1, final), (t2, final), (Path.Combine(Copies, "missing"), Path.Combine(Copies, "y.dt"))], true, warnings));
        Assert.Equal("old", File.ReadAllText(final));
        Assert.Empty(warnings);
        Assert.DoesNotContain(Directory.EnumerateFiles(Copies), x => x.Contains(".bn-old-"));
    }

    [Fact] public async Task TheLockOfTheBaseIsNeitherArchivedNorRestored()
    {
        var lockFile = Path.Combine(BaseFolder, InfobaseLock.FileName);
        File.WriteAllText(lockFile, "current lock");
        var made = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }));
        Assert.True(made.Success, made.Message);
        using (var archive = ZipFile.OpenRead(made.Files[0])) Assert.DoesNotContain(archive.Entries, x => x.FullName == InfobaseLock.FileName);
        // An older archive with a lock: the lock the base has now stays; a base without one gets none.
        var zip = Archive("locked.zip", ("1Cv8.1CD", "restored"), (InfobaseLock.FileName, "old lock"));
        Assert.True((await BackupRestore.Run(BackupRestore.Plan(Restore(zip)))).Success);
        Assert.Equal("current lock", File.ReadAllText(lockFile));
        File.Delete(lockFile);
        Assert.True((await BackupRestore.Run(BackupRestore.Plan(Restore(zip)))).Success);
        Assert.False(File.Exists(lockFile));
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    // ---- Fixes of the Grok audit, part 2 ----

    private async Task<BackupResult> SplitBackup(byte[] data, string folder, BackupJournalStore? journal)
    {
        File.WriteAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD"), data);
        var scenario = Scenario(BackupFormat.Zip) with { Compression = BackupCompression.Save, Split = BackupSplit.Custom, VolumeSize = 1 };
        return await RunBackup(BackupRun.Plan(Request(scenario, folder: folder) with { Client = "" }), journal);
    }

    [Fact] public async Task AVolumeSetMixedFromTwoBackupsIsRefused()
    {
        var journal = new BackupJournalStore(Settings);
        var older = new byte[1500 * 1024]; new Random(1).NextBytes(older);
        var newer = new byte[older.Length]; new Random(2).NextBytes(newer);
        var a = await SplitBackup(older, Copies, journal);
        Assert.True(a.Success, a.Message);
        Assert.Equal(a.Files.Count, a.Record!.Stamps.Count);
        var b = await SplitBackup(newer, Path.Combine(root, "other"), null);
        // The state Grok reproduced: a new first volume published, the old second one back.
        File.Copy(b.Files[0], a.Files[0], true);
        File.SetLastWriteTimeUtc(a.Files[0], DateTime.UtcNow.AddMinutes(5));
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "current");
        // With the journal: the stamps differ, nothing is unpacked.
        var refused = await BackupRestore.Run(BackupRestore.Plan(Restore(a.Files[0]) with { Journal = journal }));
        Assert.False(refused.Success); Assert.Contains("не совпадают с записью журнала", refused.Message);
        // Without it: the checksums of the archive's directory do not match the bytes.
        var unknown = await BackupRestore.Run(BackupRestore.Plan(Restore(a.Files[0])));
        Assert.False(unknown.Success);
        Assert.Equal("current", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Aside());
        // The untouched set of the journal restores.
        var c = await SplitBackup(older, Path.Combine(root, "third"), journal);
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "current");
        Assert.True((await BackupRestore.Run(BackupRestore.Plan(Restore(c.Files[1]) with { Journal = journal }))).Success);
        Assert.Equal(older, File.ReadAllBytes(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    [Fact] public async Task AFileChangedDuringTheArchiveFailsTheBackup()
    {
        File.WriteAllText(Path.Combine(BaseFolder, "a.txt"), "small");
        File.WriteAllText(Path.Combine(BaseFolder, "z.bin"), new string('z', 3 << 20));
        var journal = new BackupJournalStore(Settings);
        var scenario = Scenario(BackupFormat.Zip) with { KeepCopies = 1 };
        var earlier = Made(baseId, scenario.Id, 1, Touch("earlier.zip"));
        journal.Add(earlier);
        var changed = false;
        var first = new FileInfo(Path.Combine(BaseFolder, "1Cv8.1CD")).Length + 5;
        var result = await RunBackup(BackupRun.Plan(Request(scenario) with { Client = "" }), journal, step =>
        {
            // After a.txt is in the archive, «a session» changes it.
            if (changed || step.Done <= first) return;
            File.SetLastWriteTimeUtc(Path.Combine(BaseFolder, "a.txt"), DateTime.UtcNow.AddHours(-3));
            changed = true;
        });
        Assert.True(changed);
        Assert.False(result.Success); Assert.Contains("изменился во время архивирования", result.Message);
        Assert.Empty(Directory.EnumerateFiles(Copies, "Base_*"));
        Assert.Empty(Leftovers(Copies));
        // No rotation: the earlier copy stays.
        Assert.True(File.Exists(earlier.Files[0])); Assert.Equal([earlier.Id], journal.Load().Records.Select(x => x.Id));
    }

    [Fact] public async Task ABaseThatCannotBeCheckedIsLoadedOnlyWhenTheUserAgrees()
    {
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        var zip = Archive("u.zip", ("1Cv8.1CD", "restored"));
        foreach (var file in new[] { dump, zip })
        {
            var plan = Touching(BackupRestore.Plan(Restore(file)), "");
            plan.Busy = _ => new BaseBusy(BaseBusyState.NotChecked, "/proc недоступен.");
            var asked = await BackupRestore.Run(plan);
            Assert.False(asked.Success); Assert.True(asked.Unchecked); Assert.Contains("без проверки", asked.Message);
            Assert.False(File.Exists(Record));
            Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        }
        var agreed = Touching(BackupRestore.Plan(Restore(dump) with { ProceedWithoutBusyCheck = true }), "");
        agreed.Busy = _ => new BaseBusy(BaseBusyState.NotChecked);
        Assert.True((await BackupRestore.Run(agreed)).Success);
        var agreedZip = BackupRestore.Plan(Restore(zip) with { ProceedWithoutBusyCheck = true });
        agreedZip.Busy = _ => new BaseBusy(BaseBusyState.NotChecked);
        Assert.True((await BackupRestore.Run(agreedZip)).Success);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
    }

    [Fact] public async Task AnotherProcessWorkingWithTheBaseIsWaitedFor()
    {
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        using (var other = BackupBaseLock.TryAcquire(FileBase() with { Location = BaseFolder + Path.DirectorySeparatorChar }, out _))
        {
            Assert.NotNull(other);
            // The same base under another spelling is the same lock; another base is not.
            Assert.Null(BackupBaseLock.TryAcquire(FileBase(), out var failure)); Assert.Null(failure);
            using (var unrelated = BackupBaseLock.TryAcquire(ServerBase, out _)) Assert.NotNull(unrelated);
            Assert.Equal(BackupBaseLock.BusyMessage, (await RunBackup(Prepared(Request()))).Message);
            Assert.Equal(BackupBaseLock.BusyMessage, (await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), ""))).Message);
            Assert.False(File.Exists(Record));
        }
        Assert.True((await RunBackup(Prepared(Request()))).Success);
    }

    [Fact] public void LogsInTheSystemCodePageAreRead()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        Assert.Equal("Ошибка: пароль", BackupRun.Decode(cp1251.GetBytes("Ошибка: пароль"), cp1251));
        Assert.Equal("Ошибка", BackupRun.Decode(Encoding.UTF8.GetBytes("Ошибка")));
        Assert.Equal("Ошибка", BackupRun.Decode([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("Ошибка")]));
        Assert.Equal("Ошибка", BackupRun.Decode([.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("Ошибка")]));
        var log = Path.Combine(root, "ansi.log"); File.WriteAllBytes(log, cp1251.GetBytes(" Ошибка \r\n"));
        Assert.Equal("Ошибка", BackupRun.ReadLog(log, cp1251));
        if (System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage == 1251) Assert.Equal("Ошибка", BackupRun.ReadLog(log));
    }

    [Fact] public void ExitWithoutADisplayOnLinuxIsExplained()
    {
        static Func<string, string?> Variables(params (string Name, string Value)[] set) =>
            name => set.FirstOrDefault(x => x.Name == name).Value;
        // Linux, 255, neither DISPLAY nor WAYLAND_DISPLAY: 1C could not open GTK+ and wrote no log.
        var headless = BackupRun.ExitText(255, true, Variables(("DISPLAY", "")));
        Assert.Equal("1С завершилась с кодом 255: не удалось открыть окно — нет графического сеанса (переменная DISPLAY не задана). " +
            "Запустите программу в графическом сеансе.", headless);
        Assert.Equal("1С завершилась с кодом 255.", BackupRun.ExitText(255, true, Variables(("DISPLAY", ":0"))));
        Assert.Equal(headless, BackupRun.ExitText(255, true, Variables(("DISPLAY", "  "), ("WAYLAND_DISPLAY", "\t"))));
        Assert.Equal("1С завершилась с кодом 255.", BackupRun.ExitText(255, true, Variables(("WAYLAND_DISPLAY", "wayland-0"))));
        Assert.Equal("1С завершилась с кодом 255.", BackupRun.ExitText(255, false, Variables()));
        Assert.Equal("1С завершилась с кодом 1.", BackupRun.ExitText(1, true, Variables()));
        Assert.Equal("1С завершилась с кодом 1.", BackupRun.ExitText(1));
    }

    [Fact] public async Task ALinkInsideTheBaseRefusesTheZip()
    {
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        Assert.True(MakeFolderLink(Path.Combine(BaseFolder, "linked"), outside));
        var result = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }));
        Directory.Delete(Path.Combine(BaseFolder, "linked"));
        Assert.False(result.Success); Assert.Contains("ссылка", result.Message);
    }

    [Fact] public async Task TheBackupHoldLetsNobodyRemoveTheBase()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(BaseFolder, "z.bin"), new string('z', 3 << 20));
        var data = Path.Combine(BaseFolder, "1Cv8.1CD");
        var refused = false;
        var result = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }), progress: step =>
        {
            if (refused || step.Done == 0) return;
            try { File.Move(data, data + ".gone"); }
            catch (IOException) { refused = true; }
        });
        Assert.True(result.Success, result.Message); Assert.True(refused); Assert.True(File.Exists(data));
    }

    [Fact] public async Task AVolumeNumberTooLongIsNotAVolume()
    {
        Directory.CreateDirectory(Copies);
        var odd = Path.Combine(Copies, "Base_20260305.zip.99999999999999999999999"); File.WriteAllText(odd, "x");
        Assert.Null(BackupRun.VolumeNumber(odd));
        Assert.Equal(12, BackupRun.VolumeNumber("a.zip.012"));
        var result = await SplitBackup(new byte[10], Copies, null);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(odd)); Assert.Single(result.Files);
    }

    // ---- Fixes of the Codex recheck, part 2 ----

    [Fact] public async Task ASafetyCopyChangedWhileCopiedRefusesTheLoad()
    {
        File.WriteAllText(Path.Combine(BaseFolder, "z.bin"), new string('z', 3 << 20));
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        var data = Path.Combine(BaseFolder, "1Cv8.1CD");
        var first = new FileInfo(data).Length;
        var changed = false;
        var result = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), ""), step =>
        {
            // «A session» changes the data file once it is copied, while the rest is copied.
            if (changed || !step.Stage.StartsWith("Страховочная") || step.Done <= first) return;
            File.SetLastWriteTimeUtc(data, DateTime.UtcNow.AddHours(-2));
            changed = true;
        });
        Assert.True(changed);
        Assert.False(result.Success); Assert.Contains("изменился во время страховочного копирования", result.Message);
        Assert.False(File.Exists(Record));
        Assert.Empty(Aside());
    }

    [Fact] public async Task TheReplacementFollowsTheUsersAgreementWhenProcCannotBeRead()
    {
        var zip = Archive("p.zip", ("1Cv8.1CD", "restored"));
        RestorePlan Plan(bool agreed, Func<string, IReadOnlyList<FileHolder>> probe)
        {
            var plan = BackupRestore.Plan(Restore(zip) with { ProceedWithoutBusyCheck = agreed });
            plan.Busy = _ => new BaseBusy(agreed ? BaseBusyState.NotChecked : BaseBusyState.Free);
            plan.OpenFilesProbe = probe;
            return plan;
        }
        IReadOnlyList<FileHolder> Unreadable(string _) => throw new IOException("/proc недоступен.");
        // Not agreed: an unreadable /proc right before the replacement refuses.
        Assert.False((await BackupRestore.Run(Plan(false, Unreadable))).Success);
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        // Agreed, but /proc names a holder: still refused.
        var held = await BackupRestore.Run(Plan(true, _ => [new FileHolder(42, "1cv8")]));
        Assert.False(held.Success); Assert.Contains("1cv8 (42)", held.Message);
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        // Agreed and unreadable: the load goes on.
        var agreed = await BackupRestore.Run(Plan(true, Unreadable));
        Assert.True(agreed.Success, agreed.Message);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Aside());
    }

    [Fact] public async Task ALockThatCannotBeMadeIsAFailureNotAnException()
    {
        var blocker = Path.Combine(root, "not a folder"); File.WriteAllText(blocker, "x");
        BackupBaseLock.FolderOverride = Path.Combine(blocker, "locks");
        try
        {
            Assert.Null(BackupBaseLock.TryAcquire(FileBase(), out var failure)); Assert.NotNull(failure);
            var made = await RunBackup(Prepared(Request()));
            Assert.False(made.Success); Assert.Contains("блокировку", made.Message);
            var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
            var loaded = await BackupRestore.Run(Touching(BackupRestore.Plan(Restore(dump)), ""));
            Assert.False(loaded.Success); Assert.Contains("блокировку", loaded.Message);
            Assert.False(File.Exists(Record));
        }
        finally { BackupBaseLock.FolderOverride = null; }
        // The folder of this user: never a name shared by all users.
        var folder = BackupBaseLock.Folder();
        if (OperatingSystem.IsWindows()) Assert.StartsWith(Path.GetTempPath(), folder);
        else Assert.True(folder.StartsWith(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "\0") || folder.Contains("basenavigator-backup-locks-"));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int size);

    [Fact] public void ShortNamesAreTheSameFileAndTheSameBase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(root, "A rather long folder name");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a rather long backup name.dt"); File.WriteAllText(file, "x");
        var buffer = new StringBuilder(1024);
        if (GetShortPathName(file, buffer, buffer.Capacity) == 0) return;
        var shortFile = buffer.ToString();
        // Short names may be disabled on the volume: nothing to check then.
        if (string.Equals(shortFile, file, StringComparison.OrdinalIgnoreCase)) return;
        Assert.Equal(BackupJournalStore.Identity(file), BackupJournalStore.Identity(shortFile), StringComparer.OrdinalIgnoreCase);
        // A missing file in a short-named folder expands too.
        Assert.Equal(BackupJournalStore.Identity(Path.Combine(folder, "missing.dt")), BackupJournalStore.Identity(Path.Combine(Path.GetDirectoryName(shortFile)!, "missing.dt")), StringComparer.OrdinalIgnoreCase);
        var store = new BackupJournalStore(Settings);
        var (id, scenario) = (Guid.NewGuid(), Guid.NewGuid());
        var other = Touch("other.dt");
        store.Add(Made(id, scenario, 1, file, other));
        store.Add(Made(id, scenario, 2, shortFile));
        Assert.Empty(store.Rotate(id, scenario, 1));
        Assert.True(File.Exists(file)); Assert.False(File.Exists(other));
        // The base by its short and long folder names is one lock.
        GetShortPathName(folder, buffer, buffer.Capacity);
        using var first = BackupBaseLock.TryAcquire(FileBase() with { Location = folder }, out _);
        Assert.NotNull(first);
        Assert.Null(BackupBaseLock.TryAcquire(FileBase() with { Location = buffer.ToString() }, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherWindowCannotLoseItsBackupWhileFilesAreRemoved(bool rotate)
    {
        var first = new BackupJournalStore(Settings); var second = new BackupJournalStore(Settings);
        var (id, scenario) = (Guid.NewGuid(), Guid.NewGuid());
        var path = Touch("backup.dt");
        var older = Made(id, scenario, 1, path) with { Stamps = [BackupFileStamp.Of(path)] };
        first.Add(older);
        if (rotate) first.Add(Made(id, scenario, 2, Touch("newest.dt")));
        Task? registering = null;
        var waited = false;
        first.BeforeErase = () =>
        {
            // Another window replaces the file with a new backup of the same name and registers it — in the window between the
            // journal write and the removal of the files.
            File.WriteAllText(path, "a newer backup");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var newer = Made(id, scenario, 3, path) with { Stamps = [BackupFileStamp.Of(path)] };
            registering = Task.Run(() => second.Add(newer));
            // Its journal change waits for the lock this removal still holds.
            waited = !SpinWait.SpinUntil(() => registering.IsCompleted, 300);
        };
        var warnings = rotate ? first.Rotate(id, scenario, 1) : first.Delete(older.Id);
        Assert.True(waited);
        await registering!.WaitAsync(TimeSpan.FromSeconds(10));
        // The new backup is neither removed nor lost from the journal; the old record is gone.
        Assert.Equal("a newer backup", File.ReadAllText(path));
        Assert.Contains(warnings, x => x.Contains(path) && x.Contains("заменён"));
        var records = second.Load().Records;
        Assert.DoesNotContain(records, x => x.Id == older.Id);
        Assert.Contains(records, x => x.Files.Contains(path));
    }

    [Fact] public async Task APublicationWaitsForTheRemovalOfTheSamePath()
    {
        var deleting = new BackupJournalStore(Settings); var publishing = new BackupJournalStore(Settings);
        var plan = BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "", Overwrite = true });
        var path = Touch(plan.FileName);
        var older = Made(baseId, Guid.NewGuid(), 1, path) with { Stamps = [BackupFileStamp.Of(path)] };
        deleting.Add(older);
        using var waiting = new ManualResetEventSlim();
        Task<BackupResult>? run = null;
        var unpublished = false;
        BackupRun.BeforePublish = waiting.Set;
        // The removal has checked the stamp of the old file and is about to remove it: another window publishes a new backup
        // under the same name now.
        deleting.BeforeDelete = file =>
        {
            run = Task.Run(() => RunBackup(plan, publishing));
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)));
            // The publication waits for the journal lock the removal holds: the old file is still the old one.
            unpublished = !SpinWait.SpinUntil(() => run.IsCompleted, 300) && File.ReadAllText(file) == plan.FileName;
        };
        try
        {
            Assert.Empty(deleting.Delete(older.Id));
            var made = await run!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(unpublished);
            Assert.True(made.Success, made.Message);
            // The removal took only the old file; the new backup is there and in the journal.
            Assert.True(File.Exists(path)); Assert.NotEqual(plan.FileName, File.ReadAllText(path));
            var records = publishing.Load().Records;
            Assert.DoesNotContain(records, x => x.Id == older.Id);
            Assert.Contains(records, x => x.Id == made.Record!.Id && x.Files.Contains(path));
        }
        finally { BackupRun.BeforePublish = null; }
    }

    [Fact] public async Task AJournalHeldTooLongPublishesNothing()
    {
        Directory.CreateDirectory(Copies);
        var target = Path.Combine(Copies, "Base_20260305.dt"); File.WriteAllText(target, "old");
        var journal = new BackupJournalStore(Settings);
        var plan = Prepared(Request() with { Overwrite = true });
        BackupResult result;
        // Another window holds the journal for longer than the publication waits.
        using (new FileStream(journal.PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = await RunBackup(plan, journal);
        Assert.False(result.Success); Assert.Contains("не опубликована", result.Message);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Empty(Leftovers(Copies));
    }

    // ---- The platform itself, only when asked for: BN_REAL_1C_EXE=<path to 1cv8.exe> ----

    [RealPlatformFact] public async Task RealPlatformDumpsAndLoadsDtAndCf()
    {
        var real = Environment.GetEnvironmentVariable("BN_REAL_1C_EXE")!;
        var location = Path.Combine(root, "real base");
        var created = await InfobaseCreation.Run(InfobaseCreation.Plan(new NewBaseRequest { Client = real, Location = location }), timeout: TimeSpan.FromMinutes(5));
        Assert.True(created.Success, created.Message);
        var entry = new BaseEntry { Name = "Real", Location = location };
        var journal = new BackupJournalStore(Settings);
        foreach (var format in new[] { BackupFormat.Dt, BackupFormat.Cf })
        {
            var plan = BackupRun.Plan(new BackupRequest { Client = real, Base = entry, Scenario = Scenario(format), Folder = Path.Combine(root, "real copies"), Values = new("Real", "", "PC", DateTime.Now) });
            var made = await RunBackup(plan, journal, timeout: TimeSpan.FromMinutes(5));
            Assert.True(made.Success, made.Message);
            Assert.True(new FileInfo(plan.Target).Length > 0);
            Assert.Empty(Leftovers(plan.Folder));
            var loaded = await BackupRestore.Run(BackupRestore.Plan(new RestoreRequest { Client = real, Base = entry, File = plan.Target }), timeout: TimeSpan.FromMinutes(5));
            Assert.True(loaded.Success, loaded.Message + loaded.Log);
        }
        Assert.Equal(2, journal.Load().Records.Count);
        var zip = await RunBackup(BackupRun.Plan(new BackupRequest { Base = entry, Scenario = Scenario(BackupFormat.Zip), Folder = Path.Combine(root, "real copies"), Values = new("Real", "", "PC", DateTime.Now) }));
        Assert.True(zip.Success, zip.Message);
        Assert.True((await BackupRestore.Run(BackupRestore.Plan(new RestoreRequest { Base = entry, File = zip.Files[0] }))).Success);
    }

    // ---- Stage 12: the lock of «Установить блокировку ИБ» with an access code, /UC, the lock removed after the run ----

    private string LockPath => InfobaseLock.PathIn(BaseFolder);
    private string LockText => File.ReadAllText(LockPath, Encoding.UTF8);
    private string Recorded => File.Exists(Record) ? File.ReadAllText(Record) : "";
    private RestorePlan Loading(RestoreRequest request, string mode = "", string log = "")
    {
        var plan = BackupRestore.Plan(request);
        plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_MODE"] = mode; plan.Environment["BN_FAKE_LOG"] = log;
        return plan;
    }

    [Fact] public async Task TheLockOfTheRunHasACodeThatEveryCallPassesAndThatNeverShows()
    {
        var (set, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        Assert.True(set); Assert.NotNull(grant);
        var code = InfobaseLock.Read(BaseFolder)!.Code;
        Assert.Equal(BackupLock.CodeLength, code.Length);
        Assert.All(code, x => Assert.True(char.IsAsciiLetterOrDigit(x)));
        Assert.Equal(code, grant!.Code);
        Assert.DoesNotContain(code, grant.ToString());
        // The plan has no code: /UC is added at the start of 1C, after the login and before the command.
        var request = Request() with { Credentials = new("Admin", "pw") };
        var plan = Prepared(request, "fail", "session refused " + code);
        Assert.DoesNotContain("/UC", plan.Arguments);
        Assert.Equal(["/N", "Admin", "/P", "pw", "/WA-", "/UC", code, "/DumpIB"], BackupLock.Arguments(plan.Arguments, plan.CommandAt, code).Skip(4));
        Assert.Equal(["/N", "Admin", "/P", "***", "/WA-", "/UC", "***", "/DumpIB"], BackupLock.Shown(BackupLock.Arguments(plan.Arguments, plan.CommandAt, code)).Skip(4));
        Assert.DoesNotContain(code, request.ToString());
        // A failed dump: the message, the result log and the DumpLog kept next to the backups have the code hidden.
        var failed = await RunBackup(plan);
        Assert.False(failed.Success);
        Assert.Contains("/UC " + code, Recorded);
        Assert.DoesNotContain(code, failed.Message + failed.Log);
        Assert.NotNull(failed.LogPath); Assert.StartsWith(Copies, failed.LogPath!);
        Assert.DoesNotContain(code, File.ReadAllText(failed.LogPath!)); Assert.Contains("***", failed.Log);
        // A successful dump passes the code too; the lock stays until the window removes it; no log is left next to the backups.
        File.Delete(failed.LogPath!);
        var made = await RunBackup(Prepared(request));
        Assert.True(made.Success, made.Message); Assert.Empty(made.Warnings);
        Assert.Empty(Leftovers(Copies));
        Assert.True(File.Exists(LockPath));
        // The load of the same run: /UC with the code; the log of a failed one has it hidden.
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        File.Delete(Record);
        var refused = await BackupRestore.Run(Loading(Restore(dump), "fail", "refused " + code));
        Assert.False(refused.Success); Assert.DoesNotContain(code, refused.Message + refused.Log);
        Assert.Contains("/UC " + code + " /RestoreIB", Recorded);
        // Removed after the run: the file is the one written.
        Assert.Equal(BackupLockRelease.Gone, BackupLock.Release(grant));
        Assert.False(File.Exists(LockPath));
        Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        // Already gone: nothing to do.
        Assert.True(BackupLock.Release(grant).Removed);
    }

    [Fact] public async Task TheCodeIsTheOneOfTheLockAtTheStartOfOneC()
    {
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        // Planned without a lock; a lock with a code set before the start: its code is passed.
        var backup = Prepared(Request());
        var load = Loading(Restore(dump));
        new InfobaseLock(true, null, null, "Регламент", "first-code").Write(BaseFolder, null);
        Assert.True((await RunBackup(backup)).Success);
        Assert.Contains("/UC first-code /DumpIB", Recorded);
        Assert.True((await BackupRestore.Run(load)).Success);
        Assert.Contains("/UC first-code /RestoreIB", Recorded);
        // Planned under that lock, the lock changed before the start: the new code, never the old one.
        backup = Prepared(Request() with { Overwrite = true });
        load = Loading(Restore(dump));
        new InfobaseLock(true, null, null, "Регламент", "second-code").Write(BaseFolder, LockText);
        File.Delete(Record);
        Assert.True((await RunBackup(backup)).Success);
        Assert.True((await BackupRestore.Run(load)).Success);
        Assert.Equal(2, Recorded.Split("/UC second-code").Length - 1); Assert.DoesNotContain("first-code", Recorded);
        // Planned under the lock, the lock removed before the start: no /UC.
        backup = Prepared(Request() with { Overwrite = true });
        load = Loading(Restore(dump));
        File.Delete(LockPath); File.Delete(Record);
        Assert.True((await RunBackup(backup)).Success);
        Assert.True((await BackupRestore.Run(load)).Success);
        Assert.DoesNotContain("/UC", Recorded);
    }

    [Fact] public void OnlyTheLockOfTheRunIsRemovedAndALockOfThePageIsUsedAsItIs()
    {
        // The lock was changed after the run set it (the page «Блокировка», another program): it stays, under its own name.
        var (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        new InfobaseLock(true, null, null, "Регламент", "user-code 1").Write(BaseFolder, LockText);
        var theirs = LockText;
        var kept = BackupLock.Release(grant!);
        Assert.False(kept.Removed); Assert.Contains("изменён другой программой", kept.Message);
        Assert.Equal(theirs, LockText);
        Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        // A lock with a code set on the page: the runs use its code and leave it in place.
        Assert.Equal("user-code 1", BackupLock.UnlockCode(FileBase()));
        // The button never replaces any record: in force, switched off, expired or damaged.
        var again = BackupForms.SetLock(BaseFolder, DateTime.Now);
        Assert.Equal((false, BackupForms.LockSet, (BackupLockGrant?)null), again);
        Assert.Equal(theirs, LockText);
        foreach (var other in new[] { new InfobaseLock(true, null, null, "x", ""), new InfobaseLock(false, null, null, "x", "off-code") })
        {
            other.Write(BaseFolder, LockText);
            Assert.Null(BackupLock.UnlockCode(FileBase()));
            Assert.DoesNotContain("/UC", BackupLock.Arguments(["DESIGNER", "/DumpIB"], 1, BackupLock.UnlockCode(FileBase())));
        }
        var expired = new InfobaseLock(true, DateTime.Now.AddHours(-3), DateTime.Now.AddHours(-2), "old", "old-code");
        expired.Write(BaseFolder, LockText);
        var refused = BackupForms.SetLock(BaseFolder, DateTime.Now);
        Assert.False(refused.Set); Assert.Equal(BackupForms.LockForeign, refused.Message);
        Assert.Equal(expired.Format(), LockText);
        File.WriteAllText(LockPath, "garbage");
        Assert.Null(BackupLock.UnlockCode(FileBase()));
        Assert.False(BackupForms.SetLock(BaseFolder, DateTime.Now).Set); Assert.Equal("garbage", File.ReadAllText(LockPath));
        // A server base has no lock file.
        Assert.Null(BackupLock.UnlockCode(ServerBase));
    }

    [Fact] public void ALockWrittenWhileOursIsRemovedIsNeverDeleted()
    {
        // Another program writes a new 1Cv8.cdn right after ours was taken aside: ours is deleted, theirs stays.
        var (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        var foreign = new InfobaseLock(true, null, null, "Другая программа", "x1");
        BackupLock.Taken = _ => foreign.Write(BaseFolder, null);
        try { Assert.True(BackupLock.Release(grant!).Removed); }
        finally { BackupLock.Taken = null; }
        Assert.Equal(foreign.Format(), LockText);
        Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        // The file was already theirs when it was taken aside, and a third one appears meanwhile: nothing is deleted, the one taken
        // aside keeps a private name and the message names it.
        File.Delete(LockPath);
        (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        foreign.Write(BaseFolder, LockText);
        var third = new InfobaseLock(true, null, null, "Третья", "x3");
        BackupLock.Taken = _ => third.Write(BaseFolder, null);
        BackupLockRelease left;
        try { left = BackupLock.Release(grant!); }
        finally { BackupLock.Taken = null; }
        Assert.False(left.Removed);
        Assert.Equal(third.Format(), LockText);
        var aside = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        Assert.Contains(aside, left.Message); Assert.Equal(foreign.Format(), File.ReadAllText(aside, Encoding.UTF8));
        // SetLock creates the file exclusively: one made at the same moment by another program is not overwritten.
        File.Delete(LockPath); File.Delete(aside);
        Assert.True(BackupForms.SetLock(BaseFolder, DateTime.Now).Set);
        Assert.Equal(new FileInfo(LockPath).Length, Encoding.UTF8.GetPreamble().Length + Encoding.UTF8.GetByteCount(InfobaseLock.Read(BaseFolder)!.Format()));
        // A folder that is gone.
        Assert.StartsWith("Блокировка не установлена:", BackupForms.SetLock(Path.Combine(root, "gone"), DateTime.Now).Message);
        Assert.True(BackupLock.Release(new BackupLockGrant(Path.Combine(root, "gone"), "c", "t")).Removed);
    }

    [Fact] public async Task AFailedOrCancelledLoadNeverPutsAnOldLockBack()
    {
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        // The lock is changed while 1C works (the fake client writes into it), then the load fails or is cancelled: the base comes
        // back, the changed lock stays (it is not in the safety copy).
        foreach (var (mode, cancelAfter) in new[] { ("fail", (TimeSpan?)null), ("sleep", TimeSpan.FromSeconds(2)) })
        {
            if (File.Exists(LockPath)) File.Delete(LockPath);
            new InfobaseLock(true, null, null, "Регламент", "load-code").Write(BaseFolder, null);
            var plan = Loading(Restore(dump), mode);
            plan.Environment["BN_FAKE_TOUCH"] = LockPath;
            using var cancel = new CancellationTokenSource();
            if (cancelAfter is { } after) cancel.CancelAfter(after);
            var result = await BackupRestore.Run(plan, cancel: cancel.Token);
            Assert.False(result.Success);
            Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
            Assert.Equal("modified", File.ReadAllText(LockPath).Trim());
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), x => Path.GetFileName(x).Contains("bn-before-restore"));
        }
    }

    [Fact] public async Task TheRawLogOfOneCIsPrivateAndALogThatStaysIsTold()
    {
        // The /Out log is in a private temporary folder, not in the folder of the backups or of the base.
        string? raw = null;
        RawLog.BeforeRemove = path => raw = path;
        try { Assert.True((await RunBackup(Prepared(Request(), log: "done"))).Success); }
        finally { RawLog.BeforeRemove = null; }
        Assert.NotNull(raw);
        Assert.False(BackupRun.Inside(raw!, Copies)); Assert.False(BackupRun.Inside(raw!, BaseFolder));
        Assert.False(File.Exists(raw)); Assert.False(Directory.Exists(Path.GetDirectoryName(raw)));
        // A log another program holds (Windows: no delete sharing): the run says it stays and where.
        if (!OperatingSystem.IsWindows()) return;
        FileStream? held = null;
        var heldPaths = new List<string>();
        RawLog.BeforeRemove = path => { heldPaths.Add(path); held?.Dispose(); held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); };
        BackupResult made; RestoreResult loaded;
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        try
        {
            made = await RunBackup(Prepared(Request() with { Overwrite = true }, log: "done"));
            held?.Dispose();
            loaded = await BackupRestore.Run(Loading(Restore(dump), log: "done"));
        }
        finally
        {
            held?.Dispose(); RawLog.BeforeRemove = null;
            foreach (var path in heldPaths) if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
        Assert.True(made.Success, made.Message);
        Assert.Contains(made.Warnings, x => x.StartsWith("Временный журнал 1С не удалён"));
        Assert.True(loaded.Success, loaded.Message); Assert.Contains("Временный журнал 1С не удалён", loaded.Message);
    }

    // ---- Codex re-check of the lock fixes (19.10) ----

    [Fact] public async Task ARetainedLockFileIsToldAndNeverArchivedOrUnpacked()
    {
        // The retained-file case: ours taken aside is foreign, a third lock appears meanwhile — the foreign one keeps a private name.
        var (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        new InfobaseLock(true, null, null, "Другая программа", "foreign-code").Write(BaseFolder, LockText);
        BackupLock.Taken = _ => new InfobaseLock(true, null, null, "Третья", "third-code").Write(BaseFolder, null);
        BackupLockRelease left;
        try { left = BackupLock.Release(grant!); }
        finally { BackupLock.Taken = null; }
        var aside = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        Assert.True(BackupLock.IsLockName(Path.GetFileName(aside))); Assert.True(BackupLock.IsLockName("1CV8.CDN"));
        Assert.False(BackupLock.IsLockName("1Cv8.1CD")); Assert.False(BackupLock.IsLockName(".bn-lock-x.txt"));
        // The diagnostic for the log and the main window: the base, the file kept and what to do; no code.
        var told = BackupLock.Diagnostic(FileBase("Бухгалтерия"), left);
        // Another lock is in 1Cv8.cdn: not «gone», the text says «не снята».
        Assert.False(left.LockGone); Assert.StartsWith("Блокировка базы «Бухгалтерия»", told); Assert.Contains("не снята", told);
        Assert.Contains("«Бухгалтерия»", told); Assert.Contains(BaseFolder, told); Assert.Contains(aside, told); Assert.Contains("1Cv8.cdn", told);
        Assert.DoesNotContain("foreign-code", told); Assert.DoesNotContain("third-code", told);
        // The program's log (no paths or names by design): the record as written has the base's ID, the reason and the private file
        // name; neither the folder, the base's name nor any code (Codex re-check 2, 19.10).
        var logs = Path.Combine(root, "logs");
        using (var journal = new DiagnosticLog(logs, DiagnosticLevel.Warning, "test"))
        {
            journal.LockKept(FileBase("Бухгалтерия").Id, left.Reason, left.KeptFile);
            Assert.True(journal.Flush());
        }
        var line = File.ReadAllLines(Assert.Single(Directory.GetFiles(logs))).Single(x => x.Contains("backup.lock.kept"));
        var data = System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("data");
        Assert.Equal(baseId.ToString("D"), data.GetProperty("baseId").GetString());
        Assert.Equal("aside", data.GetProperty("reason").GetString());
        Assert.Equal(Path.GetFileName(aside), data.GetProperty("keptFile").GetString());
        foreach (var hidden in new[] { "Бухгалтерия", BaseFolder, root, "foreign-code", "third-code" }) Assert.DoesNotContain(hidden, line);
        // A ZIP backup leaves both lock files out.
        var made = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }));
        Assert.True(made.Success, made.Message);
        using (var archive = ZipFile.OpenRead(made.Files[0]))
            Assert.DoesNotContain(archive.Entries, x => BackupLock.IsLockName(x.Name));
        // A ZIP restore never unpacks a lock file kept aside (an archive made elsewhere may hold one); the files in the base stay.
        var foreign = File.ReadAllText(aside, Encoding.UTF8);
        File.Delete(aside);
        var zip = Archive("with-lock.zip", ("1Cv8.1CD", "restored"), (".bn-lock-0123.cdn", "old record code-in-archive"), ("1Cv8.cdn", "old lock"));
        var lockBefore = LockText;
        var loaded = await BackupRestore.Run(BackupRestore.Plan(Restore(zip)));
        Assert.True(loaded.Success, loaded.Message);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        Assert.Equal(lockBefore, LockText);
        Assert.Contains("foreign-code", foreign);
    }

    [Fact] public async Task AJournalHeldTooLongKeepsTheWarningsOfTheRun()
    {
        Directory.CreateDirectory(Copies);
        var journal = new BackupJournalStore(Settings);
        var plan = Prepared(Request(), log: "done");
        var heldPaths = new List<string>();
        FileStream? held = null;
        // Windows: the raw log of 1C is held without delete sharing, so its removal fails and a warning with its place is made.
        if (OperatingSystem.IsWindows())
            RawLog.BeforeRemove = path => { heldPaths.Add(path); held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); };
        BackupResult result;
        try
        {
            using (new FileStream(journal.PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                result = await RunBackup(plan, journal);
        }
        finally
        {
            held?.Dispose(); RawLog.BeforeRemove = null;
            foreach (var path in heldPaths) if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
        Assert.False(result.Success); Assert.Contains("не опубликована", result.Message);
        if (OperatingSystem.IsWindows())
        {
            var warning = Assert.Single(result.Warnings, x => x.StartsWith("Временный журнал 1С не удалён"));
            Assert.Contains(heldPaths.Single(), warning);
            Assert.Contains("Временный журнал 1С не удалён", BackupForms.Report(result));
        }
        Assert.Empty(Leftovers(Copies));
    }

    // ---- Grok audit of the lock (19.10) ----

    [Fact] public void PuttingALockBackNeverReplacesANewOne()
    {
        // A lock file appeared at 1Cv8.cdn before the one taken aside goes back: nothing is replaced (Windows: move without replace,
        // Unix: link(2) that fails with EEXIST — never rename(2)).
        var aside = Path.Combine(BaseFolder, ".bn-lock-" + Guid.NewGuid().ToString("N") + ".cdn");
        File.WriteAllText(aside, "taken aside");
        File.WriteAllText(LockPath, "new lock");
        Assert.Equal(BackupLock.PutBackResult.Failed, BackupLock.PutBack(aside, LockPath));
        Assert.Equal("new lock", File.ReadAllText(LockPath)); Assert.Equal("taken aside", File.ReadAllText(aside));
        // With the name free it goes back and the private name is gone.
        File.Delete(LockPath);
        Assert.Equal(BackupLock.PutBackResult.Restored, BackupLock.PutBack(aside, LockPath));
        Assert.Equal("taken aside", File.ReadAllText(LockPath)); Assert.False(File.Exists(aside));
    }

    [Fact] public void AFailedWriteOfTheNewLockRemovesItAndTellsTheReason()
    {
        BackupForms.Writing = _ => throw new IOException("disk full");
        (bool Set, string Message, BackupLockGrant? Grant) result;
        try { result = BackupForms.SetLock(BaseFolder, DateTime.Now); }
        finally { BackupForms.Writing = null; }
        Assert.False(result.Set); Assert.Null(result.Grant);
        Assert.Equal("Блокировка не установлена: disk full", result.Message);
        Assert.False(File.Exists(LockPath));
        // A file another program made first: «изменился», and theirs stays.
        File.WriteAllText(LockPath, "theirs");
        Assert.False(BackupForms.SetLock(BaseFolder, DateTime.Now).Set);
        Assert.Equal("theirs", File.ReadAllText(LockPath));
    }

    // ---- Codex re-check 5 (20.10) ----

    [Fact] public void AFailedLockWriteRemovesOnlyItsOwnFileAndNamesOneThatStays()
    {
        (bool Set, string Message, BackupLockGrant? Grant) Fails(Action<string>? beforeDiscard)
        {
            BackupForms.Writing = _ => throw new IOException("disk full");
            NewLockFile.BeforeDiscard = beforeDiscard;
            try { return BackupForms.SetLock(BaseFolder, DateTime.Now); }
            finally { BackupForms.Writing = null; NewLockFile.BeforeDiscard = null; }
        }
        // The removal itself fails: the write error and the file that stays, with what to do (Unix: the private name it was written
        // under — 1Cv8.cdn was never made; Codex re-check 6).
        var failed = Fails(_ => throw new IOException("denied"));
        Assert.False(failed.Set); Assert.Null(failed.Grant);
        Assert.StartsWith("Блокировка не установлена: disk full", failed.Message);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains($"Файл блокировки {LockPath} остался", failed.Message); Assert.Contains("«Разблокировать информационную базу»", failed.Message);
            Assert.True(File.Exists(LockPath));
            File.Delete(LockPath);
        }
        else
        {
            var kept = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
            Assert.True(BackupLock.IsPrivateName(Path.GetFileName(kept)));
            Assert.Contains($"Файл блокировки {kept} остался", failed.Message); Assert.Contains("в нём код доступа", failed.Message);
            Assert.False(File.Exists(LockPath));
            File.Delete(kept);
        }
        // The name is replaced before the removal.
        var moved = Path.Combine(root, "ours-moved.cdn");
        if (OperatingSystem.IsWindows())
        {
            // Windows: impossible while our handle is held — no rename, no delete, no open by another program; our file is removed.
            var attempts = new List<Exception?>();
            var removed = Fails(path =>
            {
                attempts.Add(Record(() => File.Move(path, moved)));
                attempts.Add(Record(() => File.Delete(path)));
                attempts.Add(Record(() => File.ReadAllText(path)));
            });
            Assert.All(attempts, x => Assert.True(x is IOException or UnauthorizedAccessException, x?.GetType().Name ?? "no error"));
            Assert.Equal("Блокировка не установлена: disk full", removed.Message);
            Assert.False(File.Exists(LockPath)); Assert.False(File.Exists(moved));
        }
        else
        {
            // Unix: a 1Cv8.cdn another program makes at any moment — before the write or right before the removal — is never removed or
            // changed; only our private name goes (Codex re-check 6).
            byte[] theirs = [.. Encoding.UTF8.GetBytes("theirs"), 0, 255];
            var before = Fails2(() => File.WriteAllBytes(LockPath, theirs), null);
            Assert.Equal("Блокировка не установлена: disk full", before.Message);
            Assert.Equal(theirs, File.ReadAllBytes(LockPath)); File.Delete(LockPath);
            var atRemoval = Fails(_ => File.WriteAllBytes(LockPath, theirs));
            Assert.Equal("Блокировка не установлена: disk full", atRemoval.Message);
            Assert.Equal(theirs, File.ReadAllBytes(LockPath));
            Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        }
        (bool Set, string Message, BackupLockGrant? Grant) Fails2(Action writing, Action<string>? beforeDiscard)
        {
            BackupForms.Writing = _ => { writing(); throw new IOException("disk full"); };
            NewLockFile.BeforeDiscard = beforeDiscard;
            try { return BackupForms.SetLock(BaseFolder, DateTime.Now); }
            finally { BackupForms.Writing = null; NewLockFile.BeforeDiscard = null; }
        }
        static Exception? Record(Action action) { try { action(); return null; } catch (Exception ex) { return ex; } }
    }

    [Fact] public void ASecondNameLeftAfterTheLockWentBackIsTold()
    {
        // Unix only: the link puts the lock back, the private name cannot be removed — both results are told.
        if (OperatingSystem.IsWindows()) return;
        var (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        new InfobaseLock(true, null, null, "Другая программа", "foreign-code").Write(BaseFolder, LockText);
        var theirs = LockText;
        BackupLock.Unlinking = _ => throw new IOException("denied");
        BackupLockRelease release;
        try { release = BackupLock.Release(grant!); }
        finally { BackupLock.Unlinking = null; }
        Assert.False(release.Removed); Assert.Equal(LockKeptReason.Changed, release.Reason);
        Assert.Equal(theirs, LockText);
        var alias = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        Assert.Equal(Path.GetFileName(alias), release.KeptFile);
        Assert.Contains("изменён другой программой", release.Message); Assert.Contains(alias, release.Message);
        File.Delete(alias);
    }

    // ---- Codex re-check 6 (20.10) ----

    [Fact] public void ALockWrittenWhileAnotherProgramMadeOneIsNotSetAndTheirsStays()
    {
        // Unix only: our lock is written under a private name; 1Cv8.cdn made meanwhile makes link(2) fail with EEXIST — «изменился»,
        // theirs stays byte for byte and the private name is gone. (Windows: our 1Cv8.cdn is held without sharing — nobody can make one.)
        if (OperatingSystem.IsWindows()) return;
        byte[] theirs = [.. Encoding.UTF8.GetBytes("theirs"), 0, 255];
        BackupForms.Writing = _ => File.WriteAllBytes(LockPath, theirs);
        (bool Set, string Message, BackupLockGrant? Grant) result;
        try { result = BackupForms.SetLock(BaseFolder, DateTime.Now); }
        finally { BackupForms.Writing = null; }
        Assert.False(result.Set); Assert.Null(result.Grant);
        Assert.Equal(BackupForms.LockChanged, result.Message);
        Assert.Equal(theirs, File.ReadAllBytes(LockPath));
        Assert.Empty(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
    }

    [Fact] public void APrivateNameLeftAfterTheLockWasPublishedIsTold()
    {
        // Unix only: the link set the lock, the private name cannot be removed — the lock is set and the second name is told.
        if (OperatingSystem.IsWindows()) return;
        NewLockFile.Unlinking = _ => throw new IOException("denied");
        (bool Set, string Message, BackupLockGrant? Grant) result;
        try { result = BackupForms.SetLock(BaseFolder, DateTime.Now); }
        finally { NewLockFile.Unlinking = null; }
        Assert.True(result.Set, result.Message); Assert.NotNull(result.Grant);
        var alias = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        Assert.True(BackupLock.IsPrivateName(Path.GetFileName(alias)));
        Assert.StartsWith("Блокировка информационной базы установлена до", result.Message);
        Assert.Contains($"Второе имя того же файла {alias} не удалено", result.Message);
        Assert.Equal(File.ReadAllText(alias, Encoding.UTF8), LockText);
        // The release removes the second name too (it holds the code), not only 1Cv8.cdn (Grok audit round 2).
        Assert.Equal(BackupLockRelease.Gone, BackupLock.Release(result.Grant!));
        Assert.False(File.Exists(LockPath)); Assert.False(File.Exists(alias));
        // The second name cannot be removed at the release either: not «removed» — the result names it for the window and the log.
        NewLockFile.Unlinking = _ => throw new IOException("denied");
        try { result = BackupForms.SetLock(BaseFolder, DateTime.Now); }
        finally { NewLockFile.Unlinking = null; }
        alias = Assert.Single(Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        var once = true;
        BackupLock.Unlinking = _ => { if (once) { once = false; throw new IOException("denied"); } };
        BackupLockRelease kept;
        try { kept = BackupLock.Release(result.Grant!); }
        finally { BackupLock.Unlinking = null; }
        Assert.False(kept.Removed); Assert.Equal(LockKeptReason.Undeletable, kept.Reason);
        Assert.Equal(Path.GetFileName(alias), kept.KeptFile);
        Assert.StartsWith("Блокировка снята, но второе имя файла блокировки", kept.Message); Assert.Contains(alias, kept.Message);
        Assert.False(File.Exists(LockPath)); Assert.True(File.Exists(alias));
        Assert.Contains(alias, BackupLock.Diagnostic(FileBase("Бухгалтерия"), kept));
        // A second name changed meanwhile is not ours: it stays and is not told.
        File.WriteAllText(alias, "другая");
        Assert.Equal(BackupLockRelease.Gone, BackupLock.Release(result.Grant!));
        Assert.Equal("другая", File.ReadAllText(alias));
        File.Delete(alias);
    }

    // ---- Codex re-check 7 (20.10) ----

    [Fact] public void AFailedLinkIsJudgedByWhat1Cv8CdnHolds()
    {
        // Unix only (Windows never links: the file is 1Cv8.cdn from the start). link(2) is replaced by the hook.
        if (OperatingSystem.IsWindows()) return;
        (bool Set, string Message, BackupLockGrant? Grant) Linked(Func<string, string, int> link)
        {
            BackupLock.Linking = link;
            try { return BackupForms.SetLock(BaseFolder, DateTime.Now); }
            finally { BackupLock.Linking = null; }
        }
        IEnumerable<string> Private() => Directory.EnumerateFiles(BaseFolder, ".bn-lock-*");
        // NFS: the link is made, an error comes back — 1Cv8.cdn holds our bytes, so the lock is set with its grant and released later.
        // The new 1Cv8.cdn is readable right after the link: our stream is closed before it (flock of FileShare.None; Linux VM run).
        byte[]? seen = null;
        var made = Linked((from, to) => { if (BackupLock.link(from, to) != 0) throw new IOException("no link"); seen = File.ReadAllBytes(to); return 5; }); // EIO
        Assert.NotNull(seen);
        Assert.True(made.Set, made.Message); Assert.NotNull(made.Grant);
        Assert.Equal(made.Grant!.Written, LockText); Assert.Empty(Private());
        Assert.Equal(BackupLockRelease.Gone, BackupLock.Release(made.Grant));
        Assert.False(File.Exists(LockPath));
        // Another failure, no link: not set with the reason; no 1Cv8.cdn, no private name.
        var refused = Linked((_, _) => 1); // EPERM
        Assert.False(refused.Set); Assert.Null(refused.Grant);
        Assert.StartsWith("Блокировка не установлена: файл блокировки не удалось сделать файлом 1Cv8.cdn", refused.Message);
        Assert.Contains("нет прав или файловая система каталога базы не поддерживает жёсткие ссылки", refused.Message);
        Assert.Contains("файловая система каталога базы не поддерживает жёсткие ссылки (", Linked((_, _) => 18).Message); // EXDEV
        Assert.DoesNotContain("жёсткие ссылки", Linked((_, _) => 31).Message); // EMLINK
        Assert.False(File.Exists(LockPath)); Assert.Empty(Private());
        // EEXIST with another program's file of other bytes: «изменился», theirs byte for byte.
        byte[] theirs = [.. Encoding.UTF8.GetBytes("theirs"), 0, 255];
        var taken = Linked((_, to) => { File.WriteAllBytes(to, theirs); return 17; });
        Assert.False(taken.Set); Assert.Equal(BackupForms.LockChanged, taken.Message);
        Assert.Equal(theirs, File.ReadAllBytes(LockPath)); Assert.Empty(Private());
        File.Delete(LockPath);
        // 1Cv8.cdn that cannot be read (here a folder): not claimed as set; told that it may be ours and how to remove it; left as is.
        var unknown = Linked((_, to) => { Directory.CreateDirectory(to); return 5; });
        Assert.False(unknown.Set); Assert.Null(unknown.Grant);
        Assert.Contains($"Возможно, файл {LockPath} всё же создан этой попыткой", unknown.Message);
        Assert.Contains("«Разблокировать информационную базу»", unknown.Message);
        // EIO is not «no hard links»: the error is told as it is (Grok audit round 2).
        Assert.DoesNotContain("жёсткие ссылки", unknown.Message);
        Assert.True(Directory.Exists(LockPath)); Assert.Empty(Private());
        Directory.Delete(LockPath);
    }

    // ---- Grok audit, round 2 (20.10) ----

    [Fact] public void PuttingALockBackJudgesAFailedLinkByWhat1Cv8CdnHolds()
    {
        // Unix only. NFS: the link is made, an error comes back — 1Cv8.cdn holds the bytes of the file aside, so it is put back and the
        // private name goes; a link that failed without a link, or a different 1Cv8.cdn, is «not put back», and 1Cv8.cdn is untouched.
        if (OperatingSystem.IsWindows()) return;
        BackupLock.PutBackResult Put(string aside, Func<string, string, int> link)
        {
            BackupLock.Linking = link;
            try { return BackupLock.PutBack(aside, LockPath); }
            finally { BackupLock.Linking = null; }
        }
        string Aside() { var aside = Path.Combine(BaseFolder, BackupLock.NewPrivateName()); File.WriteAllText(aside, "taken aside"); return aside; }
        var made = Aside();
        Assert.Equal(BackupLock.PutBackResult.Restored, Put(made, (from, to) => BackupLock.link(from, to) == 0 ? 5 : throw new IOException("no link")));
        Assert.Equal("taken aside", File.ReadAllText(LockPath)); Assert.False(File.Exists(made));
        File.Delete(LockPath);
        var failed = Aside();
        Assert.Equal(BackupLock.PutBackResult.Failed, Put(failed, (_, _) => 5));
        Assert.False(File.Exists(LockPath)); Assert.Equal("taken aside", File.ReadAllText(failed));
        Assert.Equal(BackupLock.PutBackResult.Failed, Put(failed, (_, to) => { File.WriteAllText(to, "new lock"); return 17; }));
        Assert.Equal("new lock", File.ReadAllText(LockPath)); Assert.Equal("taken aside", File.ReadAllText(failed));
    }

    [Fact] public async Task AUserCodeHoldingThePasswordIsHiddenWhole()
    {
        // The code is hidden before the password: a password inside the code would split it, and a part of the code would stay.
        new InfobaseLock(true, null, null, "Регламент", "AbcSecret42Xyz").Write(BaseFolder, null);
        var failed = await RunBackup(Prepared(Request() with { Credentials = new("Admin", "Secret42") }, "fail", "refused AbcSecret42Xyz"));
        Assert.False(failed.Success);
        foreach (var part in new[] { "Abc", "Xyz", "Secret42" }) Assert.DoesNotContain(part, failed.Message + failed.Log);
        if (failed.LogPath is { } kept) Assert.DoesNotContain("Xyz", File.ReadAllText(kept));
    }

    // ---- Codex re-check 10 (20.10) ----

    // A lock with a second name left (Unix after a link whose private name could not be removed); built directly, so both OS run it.
    private (BackupLockGrant Grant, string Alias) LockWithAlias()
    {
        const string written = "{1,20261020000000,20261020010000,\"Выполняется выгрузка данных!\",\"Code\",\"\"}";
        File.WriteAllText(LockPath, written);
        var alias = Path.Combine(BaseFolder, BackupLock.NewPrivateName());
        File.WriteAllText(alias, written);
        return (new BackupLockGrant(BaseFolder, "Code", written, alias), alias);
    }

    [Fact] public void ASecondNameIsTakenAsideBeforeItIsDeletedSoAFileMadeThereStays()
    {
        // A foreign file put at the second name right before the delete: the delete is of the name taken aside, theirs stays.
        var (grant, alias) = LockWithAlias();
        byte[] theirs = [.. Encoding.UTF8.GetBytes("theirs"), 0, 255];
        BackupLock.Unlinking = _ => File.WriteAllBytes(alias, theirs);
        BackupLockRelease release;
        try { release = BackupLock.Release(grant); }
        finally { BackupLock.Unlinking = null; }
        Assert.Equal(BackupLockRelease.Gone, release);
        Assert.Equal(theirs, File.ReadAllBytes(alias)); Assert.False(File.Exists(LockPath));
        Assert.Equal([alias], Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
        File.Delete(alias);
        // Ours, but its delete fails: back under its name and told.
        (grant, alias) = LockWithAlias();
        var once = true;
        BackupLock.Unlinking = _ => { if (once) { once = false; throw new IOException("denied"); } };
        try { release = BackupLock.Release(grant); }
        finally { BackupLock.Unlinking = null; }
        Assert.False(release.Removed); Assert.Equal(Path.GetFileName(alias), release.KeptFile); Assert.Contains(alias, release.Message);
        Assert.Equal(grant.Written, File.ReadAllText(alias)); Assert.Equal([alias], Directory.EnumerateFiles(BaseFolder, ".bn-lock-*"));
    }

    [Fact] public void RecordOfAKeptReleaseGivesTheMainWindowTextAndLogsIt()
    {
        // BackupLock.Record, which the backup window calls as soon as a release returns (Codex re-check 10): a kept second name gives
        // the text for the main window's status line and «backup.lock.kept» with the private file name; a removed lock gives nothing.
        // The window itself (closing blocked until this is done, Codex re-check 11) is not run here: no UI tests in this project.
        var (grant, alias) = LockWithAlias();
        var once = true;
        BackupLock.Unlinking = _ => { if (once) { once = false; throw new IOException("denied"); } };
        BackupLockRelease release;
        try { release = BackupLock.Release(grant); }
        finally { BackupLock.Unlinking = null; }
        var logs = Path.Combine(root, "logs");
        string? status;
        using (var journal = new DiagnosticLog(logs, DiagnosticLevel.Warning, "test"))
        {
            status = BackupLock.Record(FileBase("Бухгалтерия"), release, journal.LockKept);
            Assert.Null(BackupLock.Record(FileBase("Бухгалтерия"), BackupLockRelease.Gone, journal.LockKept));
            Assert.True(journal.Flush());
        }
        Assert.NotNull(status); Assert.Contains("«Бухгалтерия»", status); Assert.Contains(alias, status); Assert.DoesNotContain("Code\"", status);
        // 1Cv8.cdn itself is gone, only the second name stays: the window may set a new lock, the main window does not say «не снята».
        Assert.False(release.Removed); Assert.True(release.LockGone); Assert.False(File.Exists(LockPath));
        Assert.DoesNotContain("не снята", status); Assert.StartsWith("База «Бухгалтерия» (", status);
        Assert.Contains("Блокировка снята, но второе имя файла блокировки", status);
        var line = Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(logs))), x => x.Contains("backup.lock.kept"));
        var data = System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("data");
        Assert.Equal(Path.GetFileName(alias), data.GetProperty("keptFile").GetString());
        Assert.Equal("undeletable", data.GetProperty("reason").GetString());
        Assert.DoesNotContain(BaseFolder, line);
    }

    [Fact] public async Task AUserCodeHoldingThePasswordIsHiddenWholeInALoad()
    {
        new InfobaseLock(true, null, null, "Регламент", "AbcSecret42Xyz").Write(BaseFolder, null);
        var dump = Path.Combine(root, "d.dt"); File.WriteAllText(dump, "x");
        var refused = await BackupRestore.Run(Loading(Restore(dump) with { Credentials = new("Admin", "Secret42") }, "fail", "refused AbcSecret42Xyz"));
        Assert.False(refused.Success);
        foreach (var part in new[] { "Abc", "Xyz", "Secret42" }) Assert.DoesNotContain(part, refused.Message + refused.Log);
    }

    [Fact] public void TheRawLogFolderIsOwnerOnlyAndAFolderThatStaysIsTold()
    {
        using (var raw = RawLog.Create())
        {
            var folder = Path.GetDirectoryName(raw.Path)!;
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
            File.WriteAllText(raw.Path, "log");
            // Something else in the folder that cannot be removed (Windows: held without delete sharing): the folder stays and is told.
            var other = Path.Combine(folder, "other.txt"); File.WriteAllText(other, "x");
            if (OperatingSystem.IsWindows())
            {
                using (new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var told = raw.Remove();
                    Assert.NotNull(told); Assert.Contains(folder, told); Assert.StartsWith("Временная папка журнала 1С не удалена", told);
                    Assert.False(File.Exists(raw.Path));
                }
                Directory.Delete(folder, true);
            }
            else Assert.Null(raw.Remove());
            Assert.False(Directory.Exists(folder));
        }
        // The fallback of an exception: a folder that stays goes to the program's log with its path (the log keeps only its checked
        // name; Grok audit round 2).
        var codes = new List<string>();
        BackupDiagnostics.RawLogKept = codes.Add;
        string? kept = null;
        try
        {
            var raw = RawLog.Create();
            kept = Path.GetDirectoryName(raw.Path)!;
            File.WriteAllText(Path.Combine(kept, "other.txt"), "x");
            if (OperatingSystem.IsWindows())
                using (new FileStream(Path.Combine(kept, "other.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) raw.Dispose();
            else raw.Dispose();
        }
        finally { BackupDiagnostics.RawLogKept = null; if (kept is not null && Directory.Exists(kept)) Directory.Delete(kept, true); }
        Assert.Equal(OperatingSystem.IsWindows() ? [kept!] : [], codes);
        if (OperatingSystem.IsWindows()) Assert.Equal(Path.GetFileName(kept), DiagnosticLog.RawLogName(kept));
    }

    [Fact] public async Task ZipUnderTheLockLeavesTheLockOutOfTheArchiveAndInTheBase()
    {
        var (_, _, grant) = BackupForms.SetLock(BaseFolder, DateTime.Now);
        var lockText = LockText;
        var made = await RunBackup(BackupRun.Plan(Request(Scenario(BackupFormat.Zip)) with { Client = "" }));
        Assert.True(made.Success, made.Message);
        using (var archive = ZipFile.OpenRead(made.Files[0]))
            Assert.DoesNotContain(archive.Entries, x => x.FullName.Equals(InfobaseLock.FileName, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "changed");
        var loaded = await BackupRestore.Run(BackupRestore.Plan(Restore(made.Files[0])));
        Assert.True(loaded.Success, loaded.Message);
        Assert.Equal("base data", File.ReadAllText(Path.Combine(BaseFolder, "1Cv8.1CD")));
        Assert.Equal(lockText, LockText);
        Assert.True(BackupLock.Release(grant!).Removed);
    }
}
