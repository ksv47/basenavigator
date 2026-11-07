using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>Stage 11, part 4: the logic of «Выполнение сценария», «Список выгрузок», «Загрузка данных» and of the flows behind them.</summary>
public sealed class BackupFormsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-backup-forms-" + Guid.NewGuid().ToString("N"));
    private string BaseFolder => Path.Combine(root, "base");
    private string Copies => Path.Combine(root, "copies");

    public BackupFormsTests()
    {
        Directory.CreateDirectory(BaseFolder);
        File.WriteAllText(Path.Combine(BaseFolder, "1Cv8.1CD"), "data");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private static readonly Guid BaseId = Guid.NewGuid();
    private BaseEntry FileBase => new() { Id = BaseId, Name = "Бухгалтерия", Location = BaseFolder, BackupPrefix = "buh" };
    private static BaseEntry ServerBase => new() { Id = BaseId, Name = "Srv", Kind = ConnectionKind.Server, Location = "srv\\buh" };

    private static BackupRecord Record(BackupFormat format, int minute, params string[] files) => new()
    {
        BaseId = BaseId, ScenarioId = Guid.NewGuid(), Format = format, Time = new DateTimeOffset(new DateTime(2026, 10, 17, 9, minute, 5, DateTimeKind.Local)),
        Files = [.. files.DefaultIfEmpty($"C:\\b\\{format}-{minute}")], Size = 1024 * (minute + 1)
    };

    [Fact] public void BackupCommandsApplyToFileAndServerBasesOnly()
    {
        Assert.True(BackupCommands.Applies(FileBase));
        Assert.True(BackupCommands.Applies(ServerBase));
        Assert.False(BackupCommands.Applies(new BaseEntry { Name = "W", Kind = ConnectionKind.Web, Location = "http://x" }));
        Assert.False(BackupCommands.Applies(null));
    }

    [Fact] public void TheListGroupsTheRecordsOfTheBaseLikeTheOriginal()
    {
        var other = Record(BackupFormat.Cf, 1) with { BaseId = Guid.NewGuid() };
        var journal = new BackupJournal { Records = [Record(BackupFormat.Zip, 3), Record(BackupFormat.Dt, 1), other, Record(BackupFormat.Dt, 7)] };
        var groups = BackupList.Groups(journal, BaseId);
        // DT, then ZIP; no CF group (its only record is of another base), no RAR group at all.
        Assert.Equal([BackupFormat.Dt, BackupFormat.Zip], groups.Select(x => x.Format));
        Assert.Equal("Выгрузка данных информационной базы  [2]", groups[0].Text);
        Assert.Equal("ZIP-архивы данных информационной базы  [1]", groups[1].Text);
        Assert.Equal("Выгрузка конфигурации информационной базы", BackupList.Title(BackupFormat.Cf));
        // The newest first.
        Assert.Equal([7, 1], groups[0].Records.Select(x => x.Time.Minute));
        Assert.Equal("17.10.26 09:07:05", BackupList.Text(groups[0].Records[0]));
        Assert.Empty(BackupList.Groups(new BackupJournal(), BaseId));
        Assert.Equal("Выгрузки информационной базы не создавались!", BackupList.Empty);
    }

    [Fact] public void TheSourceIsTheMainFileOrACopyOfTheSameName()
    {
        var main = Path.Combine(root, "a", "x.dt"); var copy = Path.Combine(root, "b", "x.dt"); var foreign = Path.Combine(root, "c", "y.dt");
        var record = Record(BackupFormat.Dt, 1, main, foreign, copy);
        Assert.Equal(main, BackupList.MainFile(record));
        Assert.Equal(main, BackupList.Source(record, x => true));
        Assert.Equal(copy, BackupList.Source(record, x => x == copy || x == foreign));
        Assert.Null(BackupList.Source(record, x => x == foreign));
        Assert.Null(BackupList.Source(record with { Files = [] }, x => true));
        // On the disk.
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.WriteAllText(copy, "x");
        Assert.Equal(copy, BackupList.Source(record));
    }

    [Fact] public void TheSourceOfASplitArchiveIsTheFirstFolderWithTheWholeSet()
    {
        // Each case on the disk in folders of its own: the record lists three volumes in each of a, b, c; «present» says which of them
        // are there, «extra» adds files the record does not list.
        var round = 0;
        string? Source(Func<char, int, bool> present, params string[] extra)
        {
            var top = Path.Combine(root, "split" + round++);
            var files = "abc".SelectMany(f => Enumerable.Range(1, 3).Select(i => (Folder: f, Number: i, Path: Path.Combine(top, f.ToString(), $"x.zip.{i:000}")))).ToList();
            foreach (var f in "abc") Directory.CreateDirectory(Path.Combine(top, f.ToString()));
            foreach (var file in files.Where(x => present(x.Folder, x.Number))) File.WriteAllText(file.Path, "v");
            foreach (var file in extra) File.WriteAllText(Path.Combine(top, file), "v");
            var source = BackupList.Source(Record(BackupFormat.Zip, 1, [.. files.Select(x => x.Path)]));
            return source is null ? null : Path.GetRelativePath(top, source);
        }
        static string First(char folder) => Path.Combine(folder.ToString(), "x.zip.001");
        // Every folder complete: the main one.
        Assert.Equal(First('a'), Source((_, _) => true));
        // A middle volume lost in the main folder: folder 2, though the main first volume is there.
        Assert.Equal(First('b'), Source((f, i) => !(f == 'a' && i == 2)));
        // The final volume lost in the main folder and a middle one in folder 2: folder 3.
        Assert.Equal(First('c'), Source((f, i) => !(f == 'a' && i == 3) && !(f == 'b' && i == 2)));
        // The first volume lost in the main folder: folder 2.
        Assert.Equal(First('b'), Source((f, i) => !(f == 'a' && i == 1)));
        // A surplus volume (an older, longer set) would be joined by the load: that folder is skipped too.
        Assert.Equal(First('b'), Source((_, _) => true, Path.Combine("a", "x.zip.004")));
        // A stray volume after a gap: the load would refuse the missing «.004» (Codex recheck, part 4).
        Assert.Equal(First('b'), Source((_, _) => true, Path.Combine("a", "x.zip.005")));
        Assert.Equal(First('c'), Source((_, _) => true, Path.Combine("a", "x.zip.005"), Path.Combine("b", "x.zip.007")));
        // A volume of another archive does not count.
        Assert.Equal(First('a'), Source((_, _) => true, Path.Combine("a", "y.zip.004")));
        // No folder complete: «Файл не найден!».
        Assert.Null(Source((_, i) => i != 2));
    }

    [Fact] public void AnUnreadableFolderOfASplitArchiveIsSkippedForTheNextOne()
    {
        string[] Set(string folder) => [.. Enumerable.Range(1, 3).Select(i => Path.Combine(root, folder, $"x.zip.{i:000}"))];
        var a = Set("a"); var b = Set("b"); var c = Set("c");
        var record = Record(BackupFormat.Zip, 1, [.. a, .. b, .. c]);
        // The listing of a folder fails as Directory.EnumerateFiles does without the right to list it, or on a broken share.
        Func<string, string, List<string>> Volumes(params Exception[] failures) => (folder, _) =>
        {
            var index = Path.GetFileName(folder)[0] - 'a';
            if (index < failures.Length && failures[index] is { } failure) throw failure;
            return [.. new[] { a, b, c }[index]];
        };
        Assert.Equal(b[0], BackupList.Source(record, volumes: Volumes(new UnauthorizedAccessException("denied"))));
        Assert.Equal(c[0], BackupList.Source(record, volumes: Volumes(new IOException("gone"), new UnauthorizedAccessException("denied"))));
        Assert.Null(BackupList.Source(record, volumes: Volumes(new IOException("1"), new IOException("2"), new IOException("3"))));
    }

    [Fact] public void ACheckedBlankFileNameIsRefusedNotMadeByTheTemplate()
    {
        Assert.Equal("", BackupForms.RequestName(false, "typed.zip"));
        Assert.Equal("", BackupForms.RequestName(false, null));
        Assert.Equal("мой архив", BackupForms.RequestName(true, "  мой архив "));
        Assert.Null(BackupForms.RequestName(true, ""));
        Assert.Null(BackupForms.RequestName(true, "   "));
        Assert.Null(BackupForms.RequestName(true, null));
    }

    [Fact] public void TheConfirmationIsTheOriginalsText() =>
        Assert.Equal("Текущие данные информационной базы \"Бух\"\nбудут заменены данными из архива!\nФайл источник: D:\\a.dt\nПродолжать?",
            BackupList.Confirmation("Бух", "D:\\a.dt"));

    [Fact] public void TheWindowStartsWithTheBasesScenarioAndItsName()
    {
        var set = BackupScenarioSet.Original();
        Assert.Null(BackupForms.InitialChoice(set, FileBase));
        Assert.Equal(BackupScenarioSet.ArchiveId, BackupForms.InitialChoice(set, FileBase with { BackupScriptId = BackupScenarioSet.ArchiveId }));
        // A scenario that was removed: the default item.
        Assert.Null(BackupForms.InitialChoice(set, FileBase with { BackupScriptId = Guid.NewGuid() }));
        Assert.Equal(set.Default, BackupForms.Scenario(set, null));
        Assert.Equal(BackupScenarioSet.ConfigurationId, BackupForms.Scenario(set, BackupScenarioSet.ConfigurationId)?.Id);
        var values = BackupForms.Values(FileBase, new DateTime(2026, 10, 17, 9, 8, 7), "PC");
        Assert.Equal(new BackupMacroValues("Бухгалтерия", "buh", "PC", new DateTime(2026, 10, 17, 9, 8, 7)), values);
        Assert.Equal("buh_Бухгалтерия_20261017.dt", BackupForms.FileName(set.Default!, values));
        Assert.Equal("buh_Бухгалтерия_20261017.zip", BackupForms.FileName(set.For(BackupScenarioSet.ArchiveId)!, values));
        // A template that gives no name: an empty field (the run says why).
        Assert.Equal("", BackupForms.FileName(new BackupScenario { Name = "S", Template = "{Prefix}" }, values with { Prefix = "" }));
    }

    [Fact] public void TheLoginComesFromTheFieldsAndTheBase()
    {
        var designer = FileBase with { Authentication = new AuthenticationSettings { DesignerLogin = true, DesignerUser = "Админ" } };
        Assert.Equal(("Админ", "pw"), BackupForms.Prefill(designer, role => role == PasswordRole.Designer ? "pw" : null));
        Assert.Equal(("", ""), BackupForms.Prefill(FileBase, _ => "pw"));
        var os = FileBase with { Authentication = new AuthenticationSettings { OsAuthentication = true, DesignerLogin = true, DesignerUser = "Админ" } };
        Assert.Equal(("", ""), BackupForms.Prefill(os, _ => "pw"));

        Assert.Equal(new BackupCredentials("Иванов", "p"), BackupForms.Credentials(FileBase, " Иванов ", "p"));
        Assert.Equal(new BackupCredentials("Иванов", "p", AskCredentials: true), BackupForms.Credentials(FileBase with { AskCredentials = true }, "Иванов", "p"));
        // No user: the OS account of a base that uses it, else nobody (/WA- when the list asks for the name).
        Assert.Equal(new BackupCredentials(OsAuthentication: true), BackupForms.Credentials(os, "", "x"));
        Assert.Equal(new BackupCredentials(AskCredentials: true), BackupForms.Credentials(FileBase with { AskCredentials = true }, " ", "x"));
        Assert.Equal(new BackupCredentials(), BackupForms.Credentials(FileBase, "", ""));
        // The question of FormLoad: an empty user, unless the base logs in by the OS account.
        Assert.True(BackupForms.AsksForUser(FileBase, " "));
        Assert.False(BackupForms.AsksForUser(FileBase, "u"));
        Assert.False(BackupForms.AsksForUser(os, ""));
    }

    [Fact] public void TheBusyCheckEnablesTheButtonsAsTheOriginal()
    {
        var free = BackupForms.DumpState(FileBase, new BaseBusy(BaseBusyState.Free));
        Assert.Equal(new BackupBusyView(BaseBusyCheck.FreeForDump, true, false), free);
        // The window's own lock is told on a free base; a busy or unchecked base and a server base say what they said.
        Assert.Equal(new BackupBusyView(BackupForms.FreeLockedForDump, true, false), BackupForms.DumpState(FileBase, new BaseBusy(BaseBusyState.Free), ownLock: true));
        Assert.StartsWith(BaseBusyCheck.BusyForDump, BackupForms.DumpState(FileBase, new BaseBusy(BaseBusyState.Busy), ownLock: true).Text);
        Assert.Equal(BackupForms.ServerNotChecked, BackupForms.DumpState(ServerBase, new BaseBusy(BaseBusyState.NotChecked), ownLock: true).Text);
        var busy = BackupForms.DumpState(FileBase, new BaseBusy(BaseBusyState.Busy, "1cv8 (PID 7)"));
        Assert.False(busy.Allowed); Assert.True(busy.Alarm);
        Assert.Equal(BaseBusyCheck.BusyForDump + " 1cv8 (PID 7)", busy.Text);
        var unknown = BackupForms.DumpState(FileBase, new BaseBusy(BaseBusyState.NotChecked, "нет /proc"));
        Assert.True(unknown.Allowed); Assert.Contains("нет /proc", unknown.Text);
        Assert.Equal(new BackupBusyView(BackupForms.ServerNotChecked, true, false), BackupForms.DumpState(ServerBase, new BaseBusy(BaseBusyState.NotChecked)));

        Assert.Equal(new BackupBusyView(BaseBusyCheck.BusyForLoad, false, true), BackupForms.LoadState(FileBase, new BaseBusy(BaseBusyState.Busy)));
        Assert.Equal(new BackupBusyView(BackupForms.LoadWarning, true, true), BackupForms.LoadState(FileBase, new BaseBusy(BaseBusyState.Free)));
        // Not checked: the load asks when it starts.
        Assert.True(BackupForms.LoadState(FileBase, new BaseBusy(BaseBusyState.NotChecked)).Allowed);
        Assert.True(BackupForms.LoadState(ServerBase, new BaseBusy(BaseBusyState.NotChecked)).Allowed);
    }

    [Fact] public void TheLockButtonWritesItsLockOnlyWhereThereIsNoLockFile()
    {
        var now = new DateTime(2026, 10, 17, 9, 0, 0);
        var path = InfobaseLock.PathIn(BaseFolder);
        var (set, text, grant) = BackupForms.SetLock(BaseFolder, now);
        Assert.True(set); Assert.Contains("17.10.2026 10:00:00", text); Assert.NotNull(grant);
        var written = InfobaseLock.Read(BaseFolder)!;
        Assert.True(written.On); Assert.Equal(now, written.Start); Assert.Equal(now.AddMinutes(60), written.End);
        Assert.Equal(BackupForms.LockMessage, written.Message); Assert.True(written.Framed);
        // Stage 12: a random access code (the runs pass it as /UC); every lock gets another one.
        Assert.Equal(BackupLock.CodeLength, written.Code.Length); Assert.Equal(written.Code, grant!.Code);
        Assert.Contains(new string('=', 50) + "\r\nВыполняется выгрузка данных!\r\nДождитесь завершения операции...\r\n" + new string('=', 50),
            File.ReadAllText(path, Encoding.UTF8));

        // In force, or still to begin: kept.
        var before = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal((false, BackupForms.LockSet, (BackupLockGrant?)null), BackupForms.SetLock(BaseFolder, now.AddMinutes(30)));
        Assert.Equal((false, BackupForms.LockSet, (BackupLockGrant?)null), BackupForms.SetLock(BaseFolder, now.AddMinutes(-10)));
        Assert.Equal(before, File.ReadAllText(path, Encoding.UTF8));
        // Expired or switched off: someone else's record too, never replaced (Codex audit of the lock fix, 19.10).
        Assert.Equal((false, BackupForms.LockForeign, (BackupLockGrant?)null), BackupForms.SetLock(BaseFolder, now.AddMinutes(61)));
        Assert.Equal(before, File.ReadAllText(path, Encoding.UTF8));
        new InfobaseLock(false, null, null, "x", "").Write(BaseFolder, File.ReadAllText(path, Encoding.UTF8));
        var off = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal((false, BackupForms.LockForeign, (BackupLockGrant?)null), BackupForms.SetLock(BaseFolder, now));
        Assert.Equal(off, File.ReadAllText(path, Encoding.UTF8));
        // No file: a new lock with another code.
        var first = written.Code;
        File.Delete(path);
        Assert.True(BackupForms.SetLock(BaseFolder, now).Set);
        Assert.NotEqual(first, InfobaseLock.Read(BaseFolder)!.Code);
        // A lock without an end is in force.
        new InfobaseLock(true, null, null, "x", "").Write(BaseFolder, File.ReadAllText(path, Encoding.UTF8));
        Assert.False(BackupForms.SetLock(BaseFolder, now.AddYears(1)).Set);
        // A damaged file stays as it is.
        File.WriteAllText(path, "garbage");
        var damaged = BackupForms.SetLock(BaseFolder, now);
        Assert.False(damaged.Set); Assert.Contains("Блокировка не установлена", damaged.Message);
        Assert.Equal("garbage", File.ReadAllText(path));
        // A folder that is gone.
        var missing = BackupForms.SetLock(Path.Combine(root, "gone"), now);
        Assert.False(missing.Set); Assert.StartsWith("Блокировка не установлена:", missing.Message);
    }

    [Fact] public void TheReportListsWarningsAndTheKeptLog()
    {
        Assert.Equal("Готово.", BackupForms.Report(new BackupResult(true, "Готово.")));
        var report = BackupForms.Report(new BackupResult(true, "Готово.", Warnings: ["Не скопировано в D:\\2.", "Файл не удалён: x."]));
        Assert.Equal("Готово.\n\nПредупреждения:\n• Не скопировано в D:\\2.\n• Файл не удалён: x.", report);
        Assert.EndsWith("Журнал 1С сохранён: D:\\DumpLog_1.txt", BackupForms.Report(new BackupResult(false, "Ошибка.", LogPath: "D:\\DumpLog_1.txt")));
        var many = BackupForms.ReplaceQuestion([.. Enumerable.Range(1, 7).Select(x => $"f{x}")]);
        Assert.StartsWith("Файл резервной копии уже существует:\nf1\n", many);
        Assert.Contains("… и ещё 2", many); Assert.EndsWith("Заменить?", many);
    }

    private BackupRequest ZipRequest(BaseEntry? entry = null) => new()
    {
        Base = entry ?? FileBase, Scenario = new BackupScenario { Name = "Z", Format = BackupFormat.Zip }, Folder = Copies,
        Values = new BackupMacroValues("Бухгалтерия", "", "PC", new DateTime(2026, 10, 17))
    };

    [Fact] public async Task TheBackupFlowAsksBeforeReplacingAndPlansWithTheAnswer()
    {
        var asked = new List<string>(); var runs = new List<BackupPlan>();
        Task<BackupResult> Run(BackupPlan plan) { runs.Add(plan); return Task.FromResult(new BackupResult(true, "ok")); }
        // Nothing there: no question.
        Assert.True((await BackupFlow.Run(ZipRequest(), x => { asked.Add(x); return Task.FromResult(true); }, Run))!.Success);
        Assert.Empty(asked); Assert.False(runs.Single().Request.Overwrite);
        // The file is there: «replace?»; «Нет» runs nothing, «Да» runs with the agreement.
        Directory.CreateDirectory(Copies); var target = Path.Combine(Copies, "Бухгалтерия_20261017.zip"); File.WriteAllText(target, "old");
        runs.Clear();
        Assert.Null(await BackupFlow.Run(ZipRequest(), x => { asked.Add(x); return Task.FromResult(false); }, Run));
        Assert.Empty(runs); Assert.Contains(target, asked.Single());
        Assert.True((await BackupFlow.Run(ZipRequest(), x => Task.FromResult(true), Run))!.Success);
        Assert.True(runs.Single().Request.Overwrite);
        // A refused plan is a failed result with its reason: ZIP of a server base, a web base.
        var server = await BackupFlow.Run(ZipRequest(ServerBase), x => Task.FromResult(true), Run);
        Assert.False(server!.Success); Assert.Contains("только для файлового варианта", server.Message);
        Assert.Single(runs);
    }

    [Fact] public async Task TheBackupFlowAsksAgainWhenTheFileAppearedDuringTheRun()
    {
        var runs = new List<bool>(); var asked = 0;
        Task<BackupResult> Run(BackupPlan plan)
        {
            runs.Add(plan.Request.Overwrite);
            return Task.FromResult(plan.Request.Overwrite ? new BackupResult(true, "ok") : new BackupResult(false, "exists", Exists: true));
        }
        var result = await BackupFlow.Run(ZipRequest(), x => { asked++; return Task.FromResult(true); }, Run);
        Assert.True(result!.Success); Assert.Equal([false, true], runs); Assert.Equal(1, asked);
        runs.Clear();
        Assert.Null(await BackupFlow.Run(ZipRequest(), x => Task.FromResult(false), Run));
        Assert.Equal([false], runs);
    }

    [Fact] public async Task TheFlowsMakeEveryPlanByThePlannerAndTurnItsRefusalIntoAResult()
    {
        // The backup: the first plan, then the one after the files appeared during the run; with the file there before, the one after
        // «replace?».
        var requests = new List<BackupRequest>();
        Task<BackupPlan> Plan(BackupRequest x) { requests.Add(x); return Task.FromResult(BackupRun.Plan(x)); }
        var runs = 0;
        var result = await BackupFlow.Run(ZipRequest(), x => Task.FromResult(true),
            plan => Task.FromResult(++runs == 1 ? new BackupResult(false, "exists", Exists: true) : new BackupResult(true, "ok")), Plan);
        Assert.True(result!.Success); Assert.Equal([false, true], requests.Select(x => x.Overwrite)); Assert.Equal(2, runs);
        requests.Clear();
        Directory.CreateDirectory(Copies); File.WriteAllText(Path.Combine(Copies, "Бухгалтерия_20261017.zip"), "old");
        Assert.True((await BackupFlow.Run(ZipRequest(), x => Task.FromResult(true), plan => Task.FromResult(new BackupResult(true, "ok")), Plan))!.Success);
        Assert.Equal([false, true], requests.Select(x => x.Overwrite));
        // The planner's refusal is the result; nothing runs.
        runs = 0;
        var refused = await BackupFlow.Run(ZipRequest(), x => Task.FromResult(true), _ => { runs++; return Task.FromResult(new BackupResult(true, "")); },
            _ => Task.FromException<BackupPlan>(new ArgumentException("refused")));
        Assert.Equal("refused", refused!.Message); Assert.Equal(0, runs);
        // The default planner runs the plan on a pool thread and still refuses with the reason.
        var server = await BackupFlow.Run(ZipRequest(ServerBase), x => Task.FromResult(true), _ => Task.FromResult(new BackupResult(true, "")));
        Assert.Contains("только для файлового варианта", server!.Message);

        // The load: the first plan and the one after «load without the check?».
        var loads = new List<RestoreRequest>();
        Task<RestorePlan> Load(RestoreRequest x) { loads.Add(x); return Task.FromResult(BackupRestore.Plan(x)); }
        var loaded = await RestoreFlow.Run(new RestoreRequest { Base = FileBase, File = Archive() }, null, x => Task.FromResult(true),
            plan => Task.FromResult(plan.Request.ProceedWithoutBusyCheck ? new RestoreResult(true, "loaded") : new RestoreResult(false, "?", Unchecked: true)), Load);
        Assert.True(loaded!.Success); Assert.Equal([false, true], loads.Select(x => x.ProceedWithoutBusyCheck));
        var missing = await RestoreFlow.Run(new RestoreRequest { Base = FileBase, File = Archive() }, null, x => Task.FromResult(true),
            _ => Task.FromResult(new RestoreResult(true, "")), _ => Task.FromException<RestorePlan>(new ArgumentException("refused")));
        Assert.Equal("refused", missing!.Message);
    }

    private string Archive()
    {
        Directory.CreateDirectory(Copies);
        var file = Path.Combine(Copies, "a.zip"); File.WriteAllText(file, "zip");
        return file;
    }

    [Fact] public async Task TheRestoreFlowAsksTheConfirmationAndAgainWhenTheBaseWasNotChecked()
    {
        var file = Archive();
        var asked = new List<string>(); var runs = new List<bool>();
        Task<RestoreResult> Run(RestorePlan plan)
        {
            runs.Add(plan.Request.ProceedWithoutBusyCheck);
            return Task.FromResult(plan.Request.ProceedWithoutBusyCheck ? new RestoreResult(true, "loaded") : new RestoreResult(false, "not checked?", Unchecked: true));
        }
        var request = new RestoreRequest { Base = FileBase, File = file };
        // «Нет» to the confirmation: nothing runs.
        Assert.Null(await RestoreFlow.Run(request, "confirm?", x => { asked.Add(x); return Task.FromResult(false); }, Run));
        Assert.Equal(["confirm?"], asked); Assert.Empty(runs);
        // «Да», then «Да» to «load without the check?»: run again with the agreement.
        asked.Clear();
        var result = await RestoreFlow.Run(request, "confirm?", x => { asked.Add(x); return Task.FromResult(true); }, Run);
        Assert.True(result!.Success); Assert.Equal(["confirm?", "not checked?"], asked); Assert.Equal([false, true], runs);
        // Without a confirmation (the window warned), «Нет» to the second question stops.
        asked.Clear(); runs.Clear();
        Assert.Null(await RestoreFlow.Run(request, null, x => { asked.Add(x); return Task.FromResult(false); }, Run));
        Assert.Equal(["not checked?"], asked); Assert.Equal([false], runs);
        // A refused plan: a failed result, no question.
        asked.Clear();
        var missing = await RestoreFlow.Run(request with { File = Path.Combine(root, "none.zip") }, "confirm?", x => { asked.Add(x); return Task.FromResult(true); }, Run);
        Assert.False(missing!.Success); Assert.Equal(BackupRestore.MissingFile, missing.Message); Assert.Empty(asked);
    }

    [Fact] public async Task TheRestoreFlowWarnsThatAServerBaseCannotBeRolledBack()
    {
        Directory.CreateDirectory(Copies);
        var dump = Path.Combine(Copies, "a.dt"); File.WriteAllText(dump, "dt");
        var client = Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8"); File.WriteAllText(client, "");
        var asked = new List<string>(); var ran = 0;
        var request = new RestoreRequest { Base = ServerBase, File = dump, Client = client };
        Assert.Null(await RestoreFlow.Run(request, null, x => { asked.Add(x); return Task.FromResult(false); }, _ => { ran++; return Task.FromResult(new RestoreResult(true, "")); }));
        Assert.Single(asked); Assert.StartsWith(BackupRestore.ServerNotice, asked[0]); Assert.EndsWith("Продолжать?", asked[0]); Assert.Equal(0, ran);
        var result = await RestoreFlow.Run(request, "confirm?", x => Task.FromResult(true), _ => { ran++; return Task.FromResult(new RestoreResult(true, "done")); });
        Assert.True(result!.Success); Assert.Equal(1, ran);
        // A file base needs no such warning.
        asked.Clear();
        await RestoreFlow.Run(new RestoreRequest { Base = FileBase, File = Archive() }, null, x => { asked.Add(x); return Task.FromResult(true); },
            _ => Task.FromResult(new RestoreResult(true, "")));
        Assert.Empty(asked);
    }
}
