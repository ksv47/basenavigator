using System.Diagnostics;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>The one test of the real platform: it runs only when BN_REAL_1C_EXE points to a 1cv8; otherwise it is reported as skipped.</summary>
public sealed class RealPlatformFactAttribute : FactAttribute
{
    public RealPlatformFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BN_REAL_1C_EXE"))) Skip = "BN_REAL_1C_EXE is not set."; }
}

/// <summary>A test of folder links: skipped where this user may not make one (Windows without developer mode or elevation).</summary>
public sealed class LinksFactAttribute : FactAttribute
{
    private static readonly bool possible = Probe();
    public LinksFactAttribute() { if (!possible) Skip = "Symbolic links cannot be made here."; }
    private static bool Probe()
    {
        var target = Path.Combine(Path.GetTempPath(), "bn-link-" + Guid.NewGuid().ToString("N")); var link = target + "-link";
        try { Directory.CreateDirectory(target); Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return false; }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }
}

// Creating a base with the platform (CREATEINFOBASE, then DESIGNER for a load). A fake client script stands for 1C.
public sealed class InfobaseCreationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-create-" + Guid.NewGuid().ToString("N"));
    private string Client => Path.Combine(folder, OperatingSystem.IsWindows() ? "1cv8.cmd" : "1cv8");
    private string Record => Path.Combine(folder, "record.txt");
    public InfobaseCreationTests()
    {
        Directory.CreateDirectory(folder);
        // The fake client: records its arguments, writes the text of BN_FAKE_LOG to the /Out file, makes the data file named in
        // BN_FAKE_CREATE, sleeps or fails by BN_FAKE_MODE (failload: only the configurator fails). The log texts given to it are ASCII:
        // cmd writes them in the console code page, which differs between machines (GitHub's Windows runner lost Cyrillic); 1C itself
        // writes its log in UTF-8.
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Client, string.Join("\r\n", "@echo off", "echo %*>>\"%BN_FAKE_RECORD%\"", "set \"first=%~1\"", "set \"out=\"", ":loop", "if \"%~1\"==\"\" goto done",
                "if /i \"%~1\"==\"/Out\" set \"out=%~2\"", "shift", "goto loop", ":done", "if defined out if defined BN_FAKE_LOG echo %BN_FAKE_LOG%>\"%out%\"",
                "if \"%BN_FAKE_MODE%\"==\"sleepmade\" echo x>\"%BN_FAKE_CREATE%\"", "if \"%BN_FAKE_MODE%\"==\"sleepmade\" ping -n 40 127.0.0.1 >nul",
                "if \"%BN_FAKE_MODE%\"==\"sleep\" ping -n 40 127.0.0.1 >nul", "if \"%BN_FAKE_MODE%\"==\"fail\" exit /b 1",
                "if \"%BN_FAKE_MODE%\"==\"failmade\" echo x>\"%BN_FAKE_CREATE%\"", "if \"%BN_FAKE_MODE%\"==\"failmade\" exit /b 1",
                "if \"%BN_FAKE_MODE%\"==\"failload\" if /i \"%first%\"==\"DESIGNER\" exit /b 1",
                "if defined BN_FAKE_CREATE echo x>\"%BN_FAKE_CREATE%\"", "exit /b 0"));
        else
        {
            File.WriteAllText(Client, string.Join("\n", "#!/bin/sh", "echo \"$@\" >> \"$BN_FAKE_RECORD\"", "first=\"$1\"", "out=''", "while [ $# -gt 0 ]; do",
                "  if [ \"$1\" = \"/Out\" ]; then out=\"$2\"; fi; shift; done",
                "[ -n \"$out\" ] && [ -n \"$BN_FAKE_LOG\" ] && echo \"$BN_FAKE_LOG\" > \"$out\"",
                "[ \"$BN_FAKE_MODE\" = sleepmade ] && echo x > \"$BN_FAKE_CREATE\" && sleep 40",
                "[ \"$BN_FAKE_MODE\" = sleep ] && sleep 40", "[ \"$BN_FAKE_MODE\" = fail ] && exit 1",
                "[ \"$BN_FAKE_MODE\" = failmade ] && echo x > \"$BN_FAKE_CREATE\" && exit 1",
                "[ \"$BN_FAKE_MODE\" = failload ] && [ \"$first\" = DESIGNER ] && exit 1",
                "[ -n \"$BN_FAKE_CREATE\" ] && echo x > \"$BN_FAKE_CREATE\"", "exit 0") + "\n");
            File.SetUnixFileMode(Client, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    public void Dispose()
    {
        Directory.Delete(folder, true);
        // A plan that was not run keeps its private log folder in the temporary folder: removed here.
        foreach (var plan in plans) if (plan.PrivateLogs && Directory.Exists(plan.LogDirectory)) Directory.Delete(plan.LogDirectory, true);
    }
    private readonly List<CreationPlan> plans = [];
    private CreationPlan Planned(NewBaseRequest request) { var plan = InfobaseCreation.Plan(request); plans.Add(plan); return plan; }

    private string Target => Path.Combine(folder, "new base");
    private NewBaseRequest Request(NewBaseSource source = NewBaseSource.Empty, string file = "") =>
        new() { Client = Client, Location = Target, Source = source, SourceFile = file };
    private NewBaseRequest OnServer(string location = "", string connection = "", NewBaseSource source = NewBaseSource.Empty, string file = "") =>
        new() { Client = Client, Kind = ConnectionKind.Server, Location = location, ConnectionString = connection, Source = source, SourceFile = file };
    private string MakeFile(string name) { var path = Path.Combine(folder, name); File.WriteAllText(path, "fixture"); return path; }
    private static string[] Args(CreationStep step) => [.. step.Arguments];
    private static string[] WithoutLog(CreationStep step) => [.. Args(step)[..^1]];

    [Fact] public void ConnectionArgumentIsWrittenInSingleQuotes()
    {
        Assert.Equal($"File='{Target}';", InfobaseCreation.ConnectionArgument(Request()));
        // A single quote inside the value is doubled.
        Assert.Equal($"File='{Path.Combine(folder, "it''s a base")}';", InfobaseCreation.ConnectionArgument(Request() with { Location = Path.Combine(folder, "it's a base") }));
        Assert.Equal("Srvr='srv';Ref='buh';", InfobaseCreation.ConnectionArgument(OnServer("srv\\buh")));
        // A string written by hand in double quotes (as the window offers it) goes to 1C in single quotes: 8.3.24 read Srvr="srv" passed
        // on the Windows command line as tcp://\srv\:1541. Keys of any case are written as 1C writes them; an unknown key is kept.
        var full = "Srvr=\"srv\";Ref=\"buh\";DBMS=\"PostgreSQL\";dbsrvr=\"db\";DB=\"buh\";crsqldb=\"Y\";Other=\"x\";";
        Assert.Equal("Srvr='srv';Ref='buh';DBMS='PostgreSQL';DBSrvr='db';DB='buh';CrSQLDB='Y';other='x';", InfobaseCreation.ConnectionArgument(OnServer(connection: full)));
        Assert.Equal("Srvr='srv';Ref='it''s';DBPwd='a;b';", InfobaseCreation.ConnectionArgument(OnServer(connection: "Srvr=srv;Ref=\"it's\";DBPwd='a;b';")));
        Assert.Contains("двойную кавычку", Assert.Throws<ArgumentException>(() => InfobaseCreation.ConnectionArgument(OnServer(connection: "Srvr=srv;Ref=b;DBPwd='a\"b';"))).Message);
    }

    [Fact] public void RequestsThatCannotBeRunAreRefusedBeforeAnythingStarts()
    {
        void Refused(NewBaseRequest request, string fragment) => Assert.Contains(fragment, Assert.Throws<ArgumentException>(() => InfobaseCreation.Validate(request)).Message);
        Refused(Request() with { Client = Path.Combine(folder, "none") }, "Клиент 1С не найден");
        Refused(Request() with { Client = "1cv8" }, "Клиент 1С не найден");
        var thin = Path.Combine(folder, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "x");
        Refused(Request() with { Client = thin }, "толстый клиент");
        Refused(Request() with { Location = "relative\\base" }, "полный путь");
        Refused(Request() with { Location = "" }, "полный путь");
        Refused(Request() with { Location = Path.Combine(folder, "missing parent", "base") }, "Родительский каталог не найден");
        Refused(Request() with { Kind = ConnectionKind.Web, Location = "https://srv/x" }, "только подключается");
        Refused(OnServer("srv"), "сервер\\имя");
        Refused(OnServer(connection: "File=\"C:\\b\";"), "Srvr и Ref");
        Refused(OnServer(connection: "Srvr=\"a\";Ref=\"b\";ws=\"c\";"), "Srvr и Ref");
        Refused(OnServer(connection: "Srvr=\"\";Ref=\"b\";"), "Srvr и Ref");
        Refused(OnServer("srv\\b\nc"), "переносы");
        // 1C would make an empty base from a template that is not there: the file is checked here.
        Refused(Request(NewBaseSource.Template, Path.Combine(folder, "none.cf")), "не найден");
        Refused(Request(NewBaseSource.Template, MakeFile("t.zip")), "расширение .cf или .dt");
        Refused(Request(NewBaseSource.LoadDump, MakeFile("d.cf")), "расширение .dt");
        Refused(Request(NewBaseSource.LoadConfiguration, MakeFile("c.dt")), "расширение .cf");
        Refused(Request(NewBaseSource.LoadDump, "d.dt"), "полный путь");
        // The target: a file, a base that exists, a folder with something in it.
        File.WriteAllText(Target, "file"); Refused(Request(), "лежит файл"); File.Delete(Target);
        Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Target, "1Cv8.1CD"), "x"); Refused(Request(), "уже есть информационная база");
        File.Delete(Path.Combine(Target, "1Cv8.1CD")); File.WriteAllText(Path.Combine(Target, "other.txt"), "x"); Refused(Request(), "не пуст");
        File.Delete(Path.Combine(Target, "other.txt"));
        // An empty folder and a missing one are both fine.
        InfobaseCreation.Validate(Request()); Directory.Delete(Target); InfobaseCreation.Validate(Request());
    }

    // Codex audit: a link would send the data files wherever it points. Skipped (not passed) where links cannot be made.
    [LinksFact] public void ALinkInsteadOfTheFolderIsRefused()
    {
        var real = Path.Combine(folder, "real"); Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Target, real);
        Assert.Contains("ссылка", Assert.Throws<ArgumentException>(() => InfobaseCreation.Validate(Request())).Message);
    }

    [Fact] public void PlanIsTheCommandLineOfThePlatform()
    {
        var log = Path.Combine(folder, "logs"); Directory.CreateDirectory(log);
        var empty = Assert.Single(InfobaseCreation.Plan(Request(), log).Steps);
        Assert.Equal(["CREATEINFOBASE", $"File='{Target}';", "/Out"], WithoutLog(empty));
        Assert.StartsWith(log, Args(empty)[^1]); Assert.Equal(Args(empty)[^1], empty.LogPath);
        var template = MakeFile("shop.cf");
        Assert.Equal(["CREATEINFOBASE", $"File='{Target}';", "/UseTemplate", template, "/Out"], WithoutLog(Assert.Single(InfobaseCreation.Plan(Request(NewBaseSource.Template, template), log).Steps)));
        var dump = MakeFile("data.dt");
        var load = InfobaseCreation.Plan(Request(NewBaseSource.LoadDump, dump), log).Steps;
        Assert.Equal(2, load.Count);
        Assert.Equal(["CREATEINFOBASE", $"File='{Target}';", "/Out"], WithoutLog(load[0]));
        Assert.Equal(["DESIGNER", "/F", Target, "/DisableStartupDialogs", "/RestoreIB", dump, "/Out"], WithoutLog(load[1]));
        Assert.NotEqual(load[0].LogPath, load[1].LogPath);
        var configuration = MakeFile("conf.cf");
        var cf = InfobaseCreation.Plan(Request(NewBaseSource.LoadConfiguration, configuration), log).Steps;
        Assert.Equal(["DESIGNER", "/F", Target, "/DisableStartupDialogs", "/LoadCfg", configuration, "/UpdateDBCfg", "/Out"], WithoutLog(cf[1]));
        // A server base: the configurator connects with /S.
        var onServer = InfobaseCreation.Plan(OnServer("srv\\buh", source: NewBaseSource.LoadDump, file: dump), log).Steps;
        Assert.Equal(["CREATEINFOBASE", "Srvr='srv';Ref='buh';", "/Out"], WithoutLog(onServer[0]));
        Assert.Equal(["DESIGNER", "/S", "srv\\buh", "/DisableStartupDialogs", "/RestoreIB", dump, "/Out"], WithoutLog(onServer[1]));
        // The log of the default plan is in the temporary folder, never in the folder of the program or of the base.
        Assert.StartsWith(Path.GetTempPath(), Assert.Single(Planned(Request()).Steps).LogPath);
    }

    // Codex audit P1: the creation and the load name one base, whichever way the server base was given.
    [Fact] public void ServerBaseHasOneTargetForCreationAndLoad()
    {
        var dump = MakeFile("data.dt");
        var text = "Srvr='srv';Ref='new';DBMS='PostgreSQL';";
        // The string alone: the load step takes its server and name.
        var alone = Planned(OnServer(connection: text, source: NewBaseSource.LoadDump, file: dump)).Steps;
        Assert.Equal(text, Args(alone[0])[1]);
        Assert.Equal(["DESIGNER", "/S", "srv\\new", "/DisableStartupDialogs", "/RestoreIB", dump, "/Out"], WithoutLog(alone[1]));
        // The same base named twice (case does not matter): fine, and the entry is the one target.
        var same = OnServer("SRV\\NEW", text, NewBaseSource.LoadDump, dump);
        Assert.Equal("srv\\new", Planned(same).Steps[1].Arguments[2], ignoreCase: true);
        Assert.Equal("srv\\new", InfobaseCreation.ToEntry(OnServer(connection: text), "N", "", "8.3").Location);
        // Two different bases: refused, never created.
        Assert.Contains("разные базы", Assert.Throws<ArgumentException>(() => InfobaseCreation.Plan(OnServer("srv\\existing", text, NewBaseSource.LoadDump, dump))).Message);
    }

    // Codex audit P2: a password of the connection string is never in what Run returns.
    [Fact] public async Task PasswordsOfTheConnectionStringAreHiddenInTheResult()
    {
        var text = "Srvr='srv';Ref='new';DBMS='MSSQLServer';DBUID='sa';DBPwd='s3cret-db';Pwd='s3cret-user';";
        var request = OnServer(connection: text);
        var plan = Prepared(request, "fail", "failed with s3cret-db and s3cret-user and sa");
        var result = await InfobaseCreation.Run(plan);
        Assert.False(result.Success);
        Assert.DoesNotContain("s3cret", result.Message); Assert.DoesNotContain("s3cret", result.Log);
        Assert.Contains("failed with *** and *** and sa", result.Message);
        Assert.Equal("a *** b", InfobaseCreation.Redact("a s3cret-db b", request));
        Assert.Equal("nothing", InfobaseCreation.Redact("nothing", Request()));
    }

    // Codex re-check P2: one password inside another, short passwords, the marker itself and the string as 1C repeats it.
    [Fact] public void RedactionLeavesNoPartOfAPasswordAndKeepsTheRestReadable()
    {
        string Redact(string connection, string text) => InfobaseCreation.Redact(text, OnServer(connection: "Srvr=srv;Ref=b;" + connection));
        // Overlapping: in either order of the keys nothing of the longer one is left.
        Assert.Equal("failed *** ***", Redact("Pwd='abcd';DBPwd='abcdef';", "failed abcd abcdef"));
        Assert.Equal("failed *** ***", Redact("DBPwd='abcdef';Pwd='abcd';", "failed abcd abcdef"));
        // A short password is hidden as a word of its own, not inside other words.
        Assert.Equal("database failed: *** is wrong", Redact("Pwd='a';", "database failed: a is wrong"));
        Assert.Equal("failed *** ***", Redact("Pwd='abc';DBPwd='abcdef';", "failed abc abcdef"));
        // The value of a password key goes whole, whatever its length and quotes; the other keys stay.
        Assert.Equal("(\"Srvr='srv';DBPwd='***';Pwd=\"***\";dbpwd=***;Locale = \"ru_RU\";\")", Redact("Pwd='a';DBPwd='x';", "(\"Srvr='srv';DBPwd='x';Pwd=\"a\";dbpwd=x;Locale = \"ru_RU\";\")"));
        // Codex second re-check P2: a key that starts with punctuation, a password with a quote, as given and as written again for 1C.
        Assert.Equal(".pwd='***';", Redact(".pwd='a''b';", ".pwd='a''b';"));
        Assert.Equal("failed: *** / ***", Redact(".pwd='a''bcd';", "failed: a'bcd / a''bcd"));
        Assert.Equal("Srvr='srv';Ref='b';.pwd='***';", Redact(".pwd='a''b';", InfobaseCreation.ConnectionArgument(OnServer(connection: "Srvr=srv;Ref=b;.pwd='a''b';"))));
        // A password with the marker in it is replaced once; an empty password changes nothing.
        Assert.Equal("one *** two", Redact("Pwd='***x';", "one ***x two"));
        Assert.Equal("a b c", Redact("Pwd='';", "a b c"));
    }

    // Grok full audit 24.10, P3: a string the parser refuses still has the passwords the request holds hidden, and says so.
    [Fact] public void PasswordsOfAStringTheParserRefusesAreHiddenToo()
    {
        var request = OnServer(connection: "Srvr=srv;Ref=b;DBUID=sa;DBPwd='s3cret-db;Pwd=\"s3cret-user\"");
        var text = InfobaseCreation.Redact("failed: DBPwd='s3cret-db, user s3cret-user", request);
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("не разобрана", text);
        Assert.Contains("failed:", text);
    }

    // Codex full audit 24.10, P1-3: 1C writes its log, which may repeat the passwords of the connection string, into a private folder
    // «bn-1c-…» of the user (0700 on Linux), not the shared temporary folder; the folder goes after the run.
    [Fact] public async Task TheLogsOfACreationAreInAPrivateFolderRemovedAfterTheRun()
    {
        Directory.CreateDirectory(Target);
        var plan = Prepared(Request(), log: "created");
        var logs = Path.GetDirectoryName(plan.Steps[0].LogPath)!;
        Assert.StartsWith("bn-1c-", Path.GetFileName(logs));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(logs));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(logs));
        var result = await InfobaseCreation.Run(plan);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(logs));
    }

    // A folder that cannot be removed is told, not taken as removed; a 1C that could not be stopped keeps it while it may write.
    [Fact] public async Task ALogFolderThatStaysIsTold()
    {
        Directory.CreateDirectory(Target);
        var plan = Prepared(Request(), log: "created");
        var logs = Path.GetDirectoryName(plan.Steps[0].LogPath)!;
        FileStream? held = null;
        InfobaseCreation.BeforeCleanup = folder =>
        {
            // Windows: an open file keeps the folder; Linux: a folder without the right to write keeps its files.
            held = new FileStream(Path.Combine(folder, "held.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        };
        try
        {
            var result = await InfobaseCreation.Run(plan);
            Assert.True(result.Success);
            Assert.Contains("Временная папка журнала 1С не удалена: " + logs, result.Message);
        }
        finally
        {
            InfobaseCreation.BeforeCleanup = null; held?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await InfobaseCreation.RemoveFolder(logs);
        }
        File.Delete(Path.Combine(Target, "1Cv8.1CD"));
        var stuck = Prepared(Request(), "sleep");
        var stuckLogs = Path.GetDirectoryName(stuck.Steps[0].LogPath)!;
        var running = 0;
        InfobaseCreation.Stop = process => { running = process.Id; return false; };
        try
        {
            var result = await InfobaseCreation.Run(stuck, timeout: TimeSpan.FromMilliseconds(700));
            Assert.False(result.Success);
            Assert.Contains("не удалось остановить", result.Message);
            Assert.Contains("Временная папка журнала 1С оставлена, пока работает процесс 1С: " + stuckLogs, result.Message);
            Assert.True(Directory.Exists(stuckLogs));
        }
        finally
        {
            InfobaseCreation.Stop = null;
            if (running != 0) { using var process = Process.GetProcessById(running); InfobaseCreation.Terminate(process); }
            // The killed fake's child (ping, sleep) may still hold the folder for a moment: removed with the same second tries.
            await InfobaseCreation.RemoveFolder(stuckLogs);
        }
    }

    // Actions on windows-latest, 28.10: a folder held only for a moment after the run (a dying child of a stopped 1C) is removed by a
    // second try and not told as left behind. The hold goes right after the first attempt failed: no timing (Codex audit P3).
    [Fact] public async Task ALogFolderHeldForAMomentIsStillRemoved()
    {
        Directory.CreateDirectory(Target);
        var plan = Prepared(Request(), log: "created");
        var logs = Path.GetDirectoryName(plan.Steps[0].LogPath)!;
        FileStream? held = null;
        var failed = new List<int>();
        void Release()
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            held?.Dispose(); held = null;
        }
        InfobaseCreation.BeforeCleanup = folder =>
        {
            held = new FileStream(Path.Combine(folder, "held.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        };
        InfobaseCreation.FolderHeld = attempt => { failed.Add(attempt); Release(); };
        try
        {
            var result = await InfobaseCreation.Run(plan);
            Assert.Equal("Информационная база создана.", result.Message);
            Assert.False(Directory.Exists(logs));
            // The first attempt failed while held and a second one removed the folder: a single attempt would have told it as kept.
            Assert.Equal([1], failed);
        }
        finally
        {
            InfobaseCreation.BeforeCleanup = null; InfobaseCreation.FolderHeld = null;
            if (Directory.Exists(logs)) { Release(); await InfobaseCreation.RemoveFolder(logs); }
        }
    }

    private CreationPlan Prepared(NewBaseRequest request, string mode = "", string log = "", bool createFile = true)
    {
        var plan = Planned(request);
        foreach (var (key, value) in new[] { ("BN_FAKE_RECORD", Record), ("BN_FAKE_MODE", mode), ("BN_FAKE_LOG", log), ("BN_FAKE_CREATE", createFile ? Path.Combine(Target, "1Cv8.1CD") : "") })
            plan.Environment[key] = value;
        return plan;
    }

    [Fact] public async Task CreationSucceedsWhenTheDataFileIsThere()
    {
        Directory.CreateDirectory(Target);
        var plan = Prepared(Request(), log: "created");
        var shown = new List<string>();
        var result = await InfobaseCreation.Run(plan, shown.Add);
        Assert.True(result.Success, result.Message); Assert.True(result.BaseCreated); Assert.Contains("created", result.Log);
        Assert.Equal(["Создание информационной базы…"], shown);
        Assert.Contains("CREATEINFOBASE", File.ReadAllText(Record));
        // The log file of the step is removed.
        Assert.False(File.Exists(plan.Steps[0].LogPath));
    }

    [Fact] public async Task LoadRunsTheDesignerAfterTheCreationAndStopsAtTheFirstFailure()
    {
        Directory.CreateDirectory(Target);
        var dump = MakeFile("data.dt"); var request = Request(NewBaseSource.LoadDump, dump);
        var ok = await InfobaseCreation.Run(Prepared(request, log: "done"));
        Assert.True(ok.Success, ok.Message);
        var lines = File.ReadAllLines(Record); Assert.Equal(2, lines.Length);
        Assert.Contains("CREATEINFOBASE", lines[0]); Assert.Contains("DESIGNER", lines[1]); Assert.Contains("/RestoreIB", lines[1]);
        // A failing first step: the designer never starts, and no base is said to exist.
        File.Delete(Record); File.Delete(Path.Combine(Target, "1Cv8.1CD"));
        var failed = await InfobaseCreation.Run(Prepared(request, "fail", "The base already exists."));
        Assert.False(failed.Success); Assert.False(failed.BaseCreated);
        Assert.Contains("Создание информационной базы: The base already exists.", failed.Message); Assert.DoesNotContain("уже создана", failed.Message);
        Assert.Single(File.ReadAllLines(Record));
    }

    // Codex audit P2: the creation succeeded and the load failed — the empty base is there, and the message says so.
    [Fact] public async Task FailedLoadAfterTheCreationSaysTheBaseIsThere()
    {
        Directory.CreateDirectory(Target);
        var dump = MakeFile("data.dt"); var request = Request(NewBaseSource.LoadDump, dump);
        var result = await InfobaseCreation.Run(Prepared(request, "failload", "The file is damaged."));
        Assert.False(result.Success); Assert.True(result.BaseCreated);
        Assert.Contains("Загрузка информационной базы: The file is damaged.", result.Message);
        Assert.Contains("Пустая информационная база уже создана", result.Message); Assert.Contains(Target, result.Message);
        Assert.True(File.Exists(Path.Combine(Target, "1Cv8.1CD")));
    }

    // Found through the window on 8.3.27: a template for a newer platform is refused, but an empty base is left in the folder.
    [Fact] public async Task AFailedCreationThatLeftABaseSaysSo()
    {
        Directory.CreateDirectory(Target);
        var template = MakeFile("new.dt");
        var result = await InfobaseCreation.Run(Prepared(Request(NewBaseSource.Template, template), "failmade", "Platform 8.5.1 or later is required."));
        Assert.False(result.Success); Assert.False(result.BaseCreated);
        Assert.Contains("8.5.1 or later is required.", result.Message);
        // What was found is reported, not judged (Codex: another process may have written it): no advice to delete the folder.
        Assert.Contains("есть файл информационной базы 1Cv8.1CD", result.Message); Assert.Contains(Target, result.Message);
        Assert.Contains("Проверьте каталог", result.Message); Assert.DoesNotContain("удалите", result.Message);
        Assert.True(File.Exists(Path.Combine(Target, "1Cv8.1CD")));   // what is there is not removed
        // Nothing was left: no such sentence.
        File.Delete(Path.Combine(Target, "1Cv8.1CD"));
        var clean = await InfobaseCreation.Run(Prepared(Request(NewBaseSource.Template, template), "fail", "refused"));
        Assert.DoesNotContain("файл информационной базы", clean.Message);
        // Codex P3: a first step stopped by its time limit after writing the data file says so too, once the process has ended.
        var stopped = await InfobaseCreation.Run(Prepared(Request(), "sleepmade"), timeout: TimeSpan.FromMilliseconds(1500));
        Assert.False(stopped.Success); Assert.Contains("остановлена", stopped.Message); Assert.Contains("есть файл информационной базы", stopped.Message);
    }

    [Fact] public async Task ExitCodeWithoutALogAndMissingDataFileAreReported()
    {
        Directory.CreateDirectory(Target);
        var request = Request();
        var noLog = await InfobaseCreation.Run(Prepared(request, "fail"));
        Assert.False(noLog.Success); Assert.Contains("кодом 1", noLog.Message);
        var noFile = await InfobaseCreation.Run(Prepared(request, log: "ok", createFile: false));
        Assert.False(noFile.Success); Assert.Contains("файл информационной базы не найден", noFile.Message);
    }

    [Fact] public async Task ALongRunIsStoppedByTheTimeoutAndByCancel()
    {
        Directory.CreateDirectory(Target);
        var request = Request();
        var timer = Stopwatch.StartNew();
        var slow = await InfobaseCreation.Run(Prepared(request, "sleep"), timeout: TimeSpan.FromMilliseconds(700));
        Assert.False(slow.Success); Assert.Contains("остановлена", slow.Message); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("не удалось остановить", slow.Message);
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var cancelled = await InfobaseCreation.Run(Prepared(request, "sleep"), cancel: source.Token);
        Assert.False(cancelled.Success); Assert.Equal("Создание отменено.", cancelled.Message);
    }

    // Codex audit P2: a cancelled run starts nothing more.
    [Fact] public async Task CancelledBeforeTheStartLaunchesNothing()
    {
        Directory.CreateDirectory(Target);
        using var source = new CancellationTokenSource(); source.Cancel();
        var result = await InfobaseCreation.Run(Prepared(Request()), cancel: source.Token);
        Assert.False(result.Success); Assert.False(result.BaseCreated); Assert.Equal("Создание отменено.", result.Message);
        Assert.False(File.Exists(Record));
    }

    [Fact] public async Task CancelledBetweenTheStepsDoesNotStartTheLoad()
    {
        Directory.CreateDirectory(Target);
        var dump = MakeFile("data.dt"); var request = Request(NewBaseSource.LoadDump, dump);
        using var source = new CancellationTokenSource();
        var result = await InfobaseCreation.Run(Prepared(request, log: "ok"), text => { if (text.StartsWith("Загрузка")) source.Cancel(); }, cancel: source.Token);
        Assert.False(result.Success); Assert.True(result.BaseCreated);
        Assert.StartsWith("Создание отменено.", result.Message); Assert.Contains("Пустая информационная база уже создана", result.Message);
        Assert.Single(File.ReadAllLines(Record));
    }

    // Codex audit P2: what was valid when planned is checked again at the start, and only this class's own logs are removed.
    [Fact] public async Task RunChecksTheRequestAgainBeforeTheStart()
    {
        var template = MakeFile("shop.cf");
        var plan = Prepared(Request(NewBaseSource.Template, template));
        File.Delete(template);
        var gone = await InfobaseCreation.Run(plan);
        Assert.False(gone.Success); Assert.Contains("не найден", gone.Message); Assert.False(File.Exists(Record));
        var second = Prepared(Request());
        Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Target, "later.txt"), "x");
        var filled = await InfobaseCreation.Run(second);
        Assert.False(filled.Success); Assert.Contains("не пуст", filled.Message); Assert.False(File.Exists(Record));
    }

    [Fact] public async Task OnlyLogsMadeByThePlanAreDeleted()
    {
        Directory.CreateDirectory(Target);
        var logs = Path.Combine(folder, "logs"); Directory.CreateDirectory(logs);
        var plan = InfobaseCreation.Plan(Request(), logs);
        // A foreign file and a log outside: a plan naming them is not started, nothing is removed. (A plan is made by Plan only, with a
        // new name for every log; the log of another plan beside it is checked by the run below.)
        var other = Path.Combine(logs, "bn-create-other.log"); File.WriteAllText(other, "precious");
        var foreign = Path.Combine(logs, "keep-me.txt"); File.WriteAllText(foreign, "precious");
        var outside = Path.Combine(folder, "bn-create-outside.log"); File.WriteAllText(outside, "precious");
        foreach (var path in new[] { foreign, outside })
        {
            var step = plan.Steps[0];
            var tricked = new CreationPlan(plan.Request, [new CreationStep(step.Title, [.. step.Arguments.Take(step.Arguments.Count - 1), path], path)], logs);
            tricked.Environment["BN_FAKE_RECORD"] = Record; tricked.Environment["BN_FAKE_LOG"] = "log text";
            var result = await InfobaseCreation.Run(tricked);
            Assert.False(result.Success); Assert.Contains("не соответствуют запросу", result.Message);
            Assert.True(File.Exists(path), path); Assert.False(File.Exists(Record));
        }
        // The plan itself: its own log is removed, the log of another plan beside it is kept.
        plan.Environment["BN_FAKE_RECORD"] = Record; plan.Environment["BN_FAKE_LOG"] = "log text"; plan.Environment["BN_FAKE_CREATE"] = Path.Combine(Target, "1Cv8.1CD");
        Assert.True((await InfobaseCreation.Run(plan)).Success);
        Assert.False(File.Exists(plan.Steps[0].LogPath)); Assert.True(File.Exists(other));
    }

    // Codex re-check P2: a plan cannot be changed after it was made, and steps that are not those of its request never start.
    [Fact] public async Task APlanCannotBeChangedAndForeignStepsDoNotStart()
    {
        var dump = MakeFile("data.dt");
        var plan = Prepared(OnServer("srv\\new", source: NewBaseSource.LoadDump, file: dump));
        Assert.Throws<NotSupportedException>(() => ((IList<CreationStep>)plan.Steps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.Steps[1].Arguments)[2] = "srv\\existing");
        // Made by hand (only inside the program): the load of another base, a missing step, an extra step.
        var load = plan.Steps[1];
        CreationPlan With(params CreationStep[] steps)
        {
            var made = new CreationPlan(plan.Request, steps, Path.GetDirectoryName(plan.Steps[0].LogPath)!);
            foreach (var (key, value) in plan.Environment) made.Environment[key] = value;
            return made;
        }
        var other = new CreationStep(load.Title, load.Arguments.Select(x => x == "srv\\new" ? "srv\\existing" : x), load.LogPath);
        foreach (var tricked in new[] { With(plan.Steps[0], other), With(), With(plan.Steps[0]), With(plan.Steps[0], load, load) })
        {
            var result = await InfobaseCreation.Run(tricked);
            Assert.False(result.Success); Assert.False(result.BaseCreated); Assert.Contains("не соответствуют запросу", result.Message);
        }
        Assert.False(File.Exists(Record));
    }

    [Fact] public void EntryOfTheRequestForTheList()
    {
        var file = InfobaseCreation.ToEntry(Request(), "Новая", "Группа", "8.3");
        Assert.Equal((ConnectionKind.File, Path.GetFullPath(Target), "Новая", "Группа", "8.3"), (file.Kind, file.Location, file.Name, file.Group, file.PlatformVersion));
        var server = OnServer(connection: "Srvr=\"srv\";Ref=\"buh\";DBMS=\"PostgreSQL\";");
        Assert.Equal("srv\\buh", InfobaseCreation.ToEntry(server, "С", "", "").Location);
        Assert.Equal("", InfobaseCreation.ToEntry(server, "С", "", "8").PlatformVersion);
    }

    // The platform itself, only when asked for: BN_REAL_1C_EXE=<path to 1cv8.exe>. A base in a temporary folder is made and removed.
    [RealPlatformFact] public async Task RealPlatformCreatesAnEmptyBaseAndLoadsADump()
    {
        var real = Environment.GetEnvironmentVariable("BN_REAL_1C_EXE")!;
        var request = new NewBaseRequest { Client = real, Location = Path.Combine(folder, "real") };
        var created = await InfobaseCreation.Run(InfobaseCreation.Plan(request), timeout: TimeSpan.FromMinutes(5));
        Assert.True(created.Success, created.Message);
        Assert.True(File.Exists(Path.Combine(folder, "real", "1Cv8.1CD")));
        Assert.Contains("успешно", created.Log);
        // The same folder again: 1C refuses and the check before it says so first.
        Assert.Contains("уже есть", Assert.Throws<ArgumentException>(() => InfobaseCreation.Validate(request)).Message);
        // A dump of this base loaded into another new one.
        var dumpFile = Path.Combine(folder, "real.dt");
        var dump = new ProcessStartInfo(real) { UseShellExecute = false, WorkingDirectory = folder, CreateNoWindow = true };
        foreach (var arg in new[] { "DESIGNER", "/F", Path.Combine(folder, "real"), "/DisableStartupDialogs", "/DumpIB", dumpFile }) dump.ArgumentList.Add(arg);
        using (var process = Process.Start(dump)!) { Assert.True(process.WaitForExit(120000)); Assert.Equal(0, process.ExitCode); }
        var second = new NewBaseRequest { Client = real, Location = Path.Combine(folder, "real2"), Source = NewBaseSource.LoadDump, SourceFile = dumpFile };
        var loaded = await InfobaseCreation.Run(InfobaseCreation.Plan(second), timeout: TimeSpan.FromMinutes(5));
        Assert.True(loaded.Success, loaded.Message);
        Assert.Contains("Загрузка информационной базы успешно завершена", loaded.Log);
    }
}
