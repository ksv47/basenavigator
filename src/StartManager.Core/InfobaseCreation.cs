using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StartManager.Core;

/// <summary>What a new information base is made from: nothing («Создать пустую»), an installed template, or the empty base
/// followed by a loaded data dump (.dt) or configuration (.cf) — the original's «Создать и загрузить».</summary>
public enum NewBaseSource { Empty, Template, LoadDump, LoadConfiguration }

/// <summary>A request to create a base with the platform itself: <c>1cv8 CREATEINFOBASE</c>, then for a load <c>1cv8 DESIGNER</c>.
/// Only a file base and a server base are made; a base on a web server is only connected.</summary>
public sealed record NewBaseRequest
{
    /// <summary>The thick client 1cv8 of the chosen platform (the thin client has no creation mode).</summary>
    public string Client { get; init; } = "";
    public ConnectionKind Kind { get; init; } = ConnectionKind.File;
    /// <summary>A file base: the folder to create. A server base: <c>server\name</c> (the connection string is built from it)
    /// unless <see cref="ConnectionString"/> is given.</summary>
    public string Location { get; init; } = "";
    /// <summary>A server base: the complete connection string (DBMS and the database are written in it); empty — built from Location.
    /// When both are given they must name the same base: the load step uses the same target as the creation.</summary>
    public string ConnectionString { get; init; } = "";
    public NewBaseSource Source { get; init; }
    /// <summary>The template file (.cf or .dt), the data dump (.dt) or the configuration (.cf) of <see cref="Source"/>.</summary>
    public string SourceFile { get; init; } = "";
}

/// <summary>One start of the platform: the arguments of the client of the request. Its log file is made and removed by this class only.</summary>
public sealed class CreationStep
{
    internal CreationStep(string title, IEnumerable<string> arguments, string logPath)
    { Title = title; Arguments = Array.AsReadOnly(arguments.ToArray()); LogPath = logPath; }
    public string Title { get; }
    public ReadOnlyCollection<string> Arguments { get; }
    public string LogPath { get; }
}

/// <summary>The request together with its steps: what was validated is what runs. Made by <see cref="InfobaseCreation.Plan"/> only; nothing
/// in it can be changed, and <see cref="InfobaseCreation.Run"/> refuses steps that are not those of the request.</summary>
public sealed class CreationPlan
{
    internal CreationPlan(NewBaseRequest request, IEnumerable<CreationStep> steps, string logDirectory, bool privateLogs = false)
    { Request = request; Steps = Array.AsReadOnly(steps.ToArray()); LogDirectory = logDirectory; PrivateLogs = privateLogs; }
    public NewBaseRequest Request { get; }
    public ReadOnlyCollection<CreationStep> Steps { get; }
    internal string LogDirectory { get; }
    /// <summary>The logs are in a private folder «bn-1c-…» of this plan (0700 on Unix), removed by <see cref="InfobaseCreation.Run"/>.</summary>
    internal bool PrivateLogs { get; }
    /// <summary>Variables added to the environment of every start (the fake client of the tests).</summary>
    internal Dictionary<string, string> Environment { get; } = [];
}

/// <param name="BaseCreated">The first step succeeded: a base may be on the disk or the server even when the whole run failed.</param>
public sealed record CreationResult(bool Success, string Message, string Log, bool BaseCreated = false);

public static class InfobaseCreation
{
    private const string BaseFile = "1Cv8.1CD";

    /// <summary>The refusals before anything starts. 1C itself accepts a missing template and then makes an empty base, and a
    /// relative path would be made in the current folder: both are checked here. Run checks again right before the start.</summary>
    public static void Validate(NewBaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Client) || !Path.IsPathFullyQualified(request.Client) || !File.Exists(request.Client))
            throw new ArgumentException("Клиент 1С не найден. Выберите установленную платформу.");
        if (PlatformCatalog.IsThinClient(request.Client))
            throw new ArgumentException("Для создания базы нужен толстый клиент 1cv8; найден только тонкий клиент 1cv8c.");
        switch (request.Kind)
        {
            case ConnectionKind.File: ValidateFolder(request.Location); break;
            case ConnectionKind.Server: Server(request); break;
            default: throw new ArgumentException("База на веб-сервере только подключается: создать её нельзя.");
        }
        // The base must be representable in the list, or it would be made and then refused there.
        ToEntry(request, "Новая информационная база", "", "").Validate();
        switch (request.Source)
        {
            case NewBaseSource.Empty:
                break;
            case NewBaseSource.Template:
                CheckFile(request.SourceFile, "шаблона", ".cf", ".dt"); break;
            case NewBaseSource.LoadDump:
                CheckFile(request.SourceFile, "выгрузки информационной базы", ".dt"); break;
            case NewBaseSource.LoadConfiguration:
                CheckFile(request.SourceFile, "конфигурации", ".cf"); break;
            default: throw new ArgumentException("Неизвестный способ создания базы.");
        }
    }

    private static void ValidateFolder(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location) || location.Any(char.IsControl))
            throw new ArgumentException("Укажите полный путь к каталогу новой базы.");
        var full = Path.GetFullPath(location);
        if (File.Exists(full)) throw new ArgumentException($"По этому пути лежит файл, а не каталог: {full}.");
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) is not { Length: > 0 } parent || !Directory.Exists(parent))
            throw new ArgumentException($"Родительский каталог не найден: {Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full))}. Создайте его или выберите другое место.");
        // A link would send the data files to wherever it points; the folder itself must be a folder.
        if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is not null)
            throw new ArgumentException($"{full} — ссылка на другой каталог. Укажите сам каталог новой базы.");
        if (File.Exists(Path.Combine(full, BaseFile)))
            throw new ArgumentException($"В каталоге {full} уже есть информационная база. Выберите другой каталог.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new ArgumentException($"Каталог {full} не пуст. Выберите пустой или несуществующий каталог.");
    }

    private static void CheckFile(string path, string what, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException($"Укажите полный путь к файлу {what}.");
        if (!File.Exists(path)) throw new ArgumentException($"Файл {what} не найден: {path}.");
        if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Файл {what} должен иметь расширение {string.Join(" или ", extensions)}: {path}.");
    }

    /// <summary>The connection string passed to 1C. Single quotes inside the value are doubled; the value of a file base is written
    /// in single quotes, as the original does (<c>"File='…'"</c>), so that no double quote has to cross the command line.</summary>
    public static string ConnectionArgument(NewBaseRequest request) => request.Kind == ConnectionKind.File
        ? $"File='{Path.GetFullPath(request.Location).Replace("'", "''")}';"
        : Server(request).Connection;

    /// <summary>The one target of a server base: the string for the creation and the <c>server\name</c> of the load step, the list and
    /// the name. A string and a location that name different bases are refused.</summary>
    private static (string Connection, string Target) Server(NewBaseRequest request)
    {
        var text = request.ConnectionString.Trim();
        var location = request.Location.Trim();
        if (location.Any(char.IsControl)) throw new ArgumentException("Название сервера и базы не должно содержать переносы строк.");
        if (text.Length == 0)
        {
            var split = location.IndexOf('\\');
            if (split <= 0 || split == location.Length - 1)
                throw new ArgumentException("Укажите сервер и имя информационной базы в кластере: сервер\\имя.");
            return ($"Srvr='{location[..split].Replace("'", "''")}';Ref='{location[(split + 1)..].Replace("'", "''")}';", location);
        }
        if (text.Any(char.IsControl)) throw new ArgumentException("Строка подключения не должна содержать переносы строк.");
        DbConnectionStringBuilder parts;
        try { parts = new DbConnectionStringBuilder { ConnectionString = text }; }
        catch (ArgumentException) { throw new ArgumentException("Строка подключения записана неверно."); }
        if (!parts.TryGetValue("Srvr", out var server) || !parts.TryGetValue("Ref", out var name) || parts.ContainsKey("File") || parts.ContainsKey("ws") ||
            string.IsNullOrWhiteSpace(server?.ToString()) || string.IsNullOrWhiteSpace(name?.ToString()))
            throw new ArgumentException("Для базы на сервере в строке подключения нужны Srvr и Ref (без File и ws).");
        var target = server + "\\" + name;
        if (location.Length > 0 && !string.Equals(location, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Размещение «{location}» и строка подключения («{target}») указывают на разные базы.");
        // Written again with the values in single quotes: on Windows a double quote inside an argument reaches 1C as \" and it reads the
        // backslash as a part of the value (Srvr="srv" gave tcp://\srv\:1541 on 8.3.24). A double quote inside a value cannot be passed.
        var written = new StringBuilder();
        foreach (string key in parts.Keys)
        {
            var value = parts[key]?.ToString() ?? "";
            if (value.Contains('"')) throw new ArgumentException($"Значение «{Known(key)}» строки подключения не должно содержать двойную кавычку.");
            written.Append(Known(key)).Append("='").Append(value.Replace("'", "''")).Append("';");
        }
        return (written.ToString(), target);
    }

    // The keys of a connection string as 1C writes them (the parser gives them in lower case; 1C accepts either).
    private static readonly string[] Keys = ["Srvr", "Ref", "DBMS", "DBSrvr", "DB", "DBUID", "DBPwd", "SQLYOffs", "CrSQLDB", "SchJobDn", "SUsr", "SPwd", "Locale", "Usr", "Pwd", "LicDstr", "Zn"];
    private static string Known(string key) => Keys.FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;

    /// <summary>The 1C base of the request as a list entry: the connection string of the original, to be added to the list.</summary>
    public static BaseEntry ToEntry(NewBaseRequest request, string name, string group, string platformVersion) => new()
    {
        Name = name, Group = group, Kind = request.Kind,
        Location = request.Kind == ConnectionKind.File ? Path.GetFullPath(request.Location) : Server(request).Target,
        PlatformVersion = PlatformCatalog.IsVersion(platformVersion) ? platformVersion : ""
    };

    /// <summary>The steps of the request: always the creation; for a load a second start of the configurator on the new base (it has no
    /// users, so it needs no name or password: only the one target of the server base is passed).</summary>
    /// <param name="logDirectory">Tests: the folder of the logs. Otherwise a private folder of the user as for backups: the log may
    /// repeat the passwords of the connection string before they are hidden (Codex full audit 24.10, P1-3).</param>
    public static CreationPlan Plan(NewBaseRequest request, string? logDirectory = null)
    {
        Validate(request);
        var directory = Path.GetFullPath(logDirectory ?? RawLog.PrivateFolder());
        try { return new CreationPlan(request, Steps(request, () => Path.Combine(directory, "bn-create-" + Guid.NewGuid().ToString("N") + ".log")), directory, logDirectory is null); }
        catch when (logDirectory is null) { try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } throw; }
    }

    // The command lines of the request; log gives the /Out file of each step in turn.
    private static List<CreationStep> Steps(NewBaseRequest request, Func<string> log)
    {
        var steps = new List<CreationStep>();
        var create = new List<string> { "CREATEINFOBASE", ConnectionArgument(request) };
        // A template is used by the creation itself; a dump or a configuration is loaded afterwards by the configurator.
        if (request.Source == NewBaseSource.Template) { create.Add("/UseTemplate"); create.Add(request.SourceFile); }
        var createLog = log(); create.Add("/Out"); create.Add(createLog);
        steps.Add(new("Создание информационной базы", create, createLog));
        if (request.Source is NewBaseSource.LoadDump or NewBaseSource.LoadConfiguration)
        {
            var file = request.Kind == ConnectionKind.File;
            var load = new List<string> { "DESIGNER", file ? "/F" : "/S", file ? Path.GetFullPath(request.Location) : Server(request).Target, "/DisableStartupDialogs" };
            if (request.Source == NewBaseSource.LoadDump) { load.Add("/RestoreIB"); load.Add(request.SourceFile); }
            else { load.Add("/LoadCfg"); load.Add(request.SourceFile); load.Add("/UpdateDBCfg"); }
            var loadLog = log(); load.Add("/Out"); load.Add(loadLog);
            steps.Add(new(request.Source == NewBaseSource.LoadDump ? "Загрузка информационной базы" : "Загрузка конфигурации", load, loadLog));
        }
        return steps;
    }

    // The steps of the plan are those of its request, and every log is one of its own folder: anything else is not started.
    private static bool Matches(CreationPlan plan)
    {
        var index = 0;
        var expected = Steps(plan.Request, () => index < plan.Steps.Count ? plan.Steps[index++].LogPath : "");
        return expected.Count == plan.Steps.Count && expected.Zip(plan.Steps).All(x => x.First.Title == x.Second.Title &&
            x.First.Arguments.SequenceEqual(x.Second.Arguments, StringComparer.Ordinal) && Owned(plan, x.Second.LogPath));
    }

    private static ProcessStartInfo Start(CreationPlan plan, CreationStep step)
    {
        var start = new ProcessStartInfo(plan.Request.Client) { UseShellExecute = false, WorkingDirectory = plan.LogDirectory, CreateNoWindow = true };
        foreach (var argument in step.Arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in plan.Environment) start.Environment[key] = value;
        return start;
    }

    /// <summary>Starts the steps one after another and waits for each; stops at the first failure. The request is checked again right before
    /// the first start. A created base is left as it is (the user decides); the message says so when a later step failed. The time limit is
    /// that of each step, so a creation with a load may take twice as long. A cancelled or timed out step is killed with its children and
    /// the run reports whether the process really ended. The text of the log is returned without the passwords of the connection string.</summary>
    public static async Task<CreationResult> Run(CreationPlan plan, Action<string>? progress = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var cleanup = new Cleanup();
        var result = await RunSteps(plan, progress, timeout, cancel, cleanup);
        return await Finish(plan, result, cleanup);
    }

    /// <summary>Tests: runs after each attempt of <see cref="RemoveFolder"/> that left the folder in place, with its number.</summary>
    internal static Action<int>? FolderHeld;

    /// <summary>Removes the folder, trying again for a short while (about 3 s) while it is held; whether it is gone. A stopped 1C has
    /// ended, but <see cref="Process.Kill(bool)"/> waits for none of its children, and on Windows a folder that is still the working
    /// directory of a dying child, or holds a file it has open, cannot be removed yet: without the second tries a cancelled creation told
    /// of a folder left behind that was free a moment later (seen in Actions on windows-latest, 28.10).</summary>

    internal static async Task<bool> RemoveFolder(string folder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!Directory.Exists(folder)) return true;
            FolderHeld?.Invoke(attempt);
            if (attempt >= 30) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>The logs this run could not remove, and whether a 1C process that could not be stopped may still write there.</summary>
    private sealed class Cleanup
    {
        public bool Alive;
        public readonly List<string> Kept = [];
    }

    /// <summary>Tests: runs right before the private folder of the logs is removed.</summary>
    internal static Action<string>? BeforeCleanup;
    /// <summary>Tests: stands for <see cref="Terminate"/> (a process that does not end).</summary>
    internal static Func<Process, bool>? Stop;

    // The private folder goes once 1C has ended; what stays is told, as it may hold a password of the connection string.
    private static async Task<CreationResult> Finish(CreationPlan plan, CreationResult result, Cleanup cleanup)
    {
        string? note = null;
        if (plan.PrivateLogs)
        {
            if (cleanup.Alive)
                note = $"Временная папка журнала 1С оставлена, пока работает процесс 1С: {plan.LogDirectory}. Удалите её вручную после его завершения — в ней может быть пароль.";
            else
            {
                BeforeCleanup?.Invoke(plan.LogDirectory);
                if (!await RemoveFolder(plan.LogDirectory))
                    note = $"Временная папка журнала 1С не удалена: {plan.LogDirectory}. Удалите её вручную — в ней может быть пароль.";
            }
        }
        else if (cleanup.Kept.Count > 0)
            note = $"Временный журнал 1С не удалён: {string.Join(", ", cleanup.Kept)}. Удалите его вручную — в нём может быть пароль.";
        return note is null ? result : result with { Message = result.Message + " " + note };
    }

    private static async Task<CreationResult> RunSteps(CreationPlan plan, Action<string>? progress, TimeSpan? timeout, CancellationToken cancel, Cleanup cleanup)
    {
        var request = plan.Request;
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        var all = new StringBuilder();
        try { Validate(request); }
        catch (ArgumentException ex) { return new(false, ex.Message, ""); }
        if (!Matches(plan)) return new(false, "Шаги создания не соответствуют запросу: создание не запущено.", "");
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var created = index > 0;
            if (cancel.IsCancellationRequested) return new(false, "Создание отменено." + Left(request, created), Redact(all.ToString(), request), created);
            // The second step needs the base the first made.
            if (created && request.Kind == ConnectionKind.File && !File.Exists(DataFile(request)))
                return new(false, $"{step.Title}: файл информационной базы не найден: {DataFile(request)}.", Redact(all.ToString(), request), true);
            progress?.Invoke(step.Title + "…");
            if (cancel.IsCancellationRequested) return new(false, "Создание отменено." + Left(request, created), Redact(all.ToString(), request), created);
            int exit;
            try
            {
                using var process = Process.Start(Start(plan, step)) ?? throw new InvalidOperationException("Не удалось запустить 1С.");
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timer.CancelAfter(limit);
                try { await process.WaitForExitAsync(timer.Token); }
                catch (OperationCanceledException)
                {
                    var ended = (Stop ?? Terminate)(process);
                    // A process that may still write keeps its log: removing it now could leave a new one behind unnoticed.
                    if (ended) Forget(plan, step.LogPath, cleanup); else { cleanup.Alive = true; cleanup.Kept.Add(step.LogPath); }
                    var why = cancel.IsCancellationRequested ? "Создание отменено." :
                        $"{step.Title}: 1С не завершила работу за {limit.TotalMinutes:0.##} мин" + (ended ? " и остановлена." : ".");
                    // What a stopped first step left is reported once the process is gone; while it may still write, only the process is.
                    return new(false, why + (ended ? "" : $" Процесс 1С (PID {SafeId(process)}) не удалось остановить: завершите его вручную.") + Left(request, created) +
                        (ended ? Unfinished(request, created) : ""),
                        Redact(all.ToString(), request), created);
                }
                exit = process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) { Forget(plan, step.LogPath, cleanup); return new(false, $"{step.Title}: " + Launcher.ErrorMessage(ex) + Left(request, created), Redact(all.ToString(), request), created); }
            var log = Redact(ReadLog(step.LogPath), request); Forget(plan, step.LogPath, cleanup);
            if (log.Length > 0) all.AppendLine(log);
            if (exit != 0)
                return new(false, $"{step.Title}: " + (log.Length > 0 ? log : BackupRun.ExitText(exit)) + Left(request, created) + Unfinished(request, created),
                    all.ToString(), created);
        }
        // A file base exists when its data file does; 1C reports success of the creation in the log, but the file is the proof.
        if (request.Kind == ConnectionKind.File && !File.Exists(DataFile(request)))
            return new(false, "1С сообщила об успехе, но файл информационной базы не найден: " + DataFile(request), all.ToString(), true);
        return new(true, "Информационная база создана.", all.ToString(), true);
    }

    private static string DataFile(NewBaseRequest request) => Path.Combine(Path.GetFullPath(request.Location), BaseFile);

    // What the user needs to know when a later step failed or the run was stopped after the creation: the empty base is still there.
    private static string Left(NewBaseRequest request, bool created) => !created ? "" :
        $" Пустая информационная база уже создана ({(request.Kind == ConnectionKind.File ? Path.GetFullPath(request.Location) : Server(request).Target)}): " +
        "подключите её командой «Подключить существующую информационную базу» или удалите вручную.";

    // A failed creation may still leave a base behind: 8.3.27 refusing a template made for 8.5 left an empty 1Cv8.1CD in the folder. The
    // folder was empty before the start, but nothing kept others out of it since: what is found is reported, not judged, and nothing is removed.
    private static string Unfinished(NewBaseRequest request, bool created) => created || request.Kind != ConnectionKind.File || !File.Exists(DataFile(request)) ? "" :
        $" После неудачной попытки в каталоге {Path.GetFullPath(request.Location)} есть файл информационной базы {BaseFile} — скорее всего, незавершённая " +
        "база 1С. Проверьте каталог перед повтором или выберите другой пустой каталог.";

    internal static int SafeId(Process process) { try { return process.Id; } catch (InvalidOperationException) { return 0; } }

    /// <summary>Kills the process with its children and waits (a few seconds) for it to end; whether it really did.</summary>
    internal static bool Terminate(Process process)
    {
        try { process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        try { return process.WaitForExit(10000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>The values of password keys of the connection string are hidden in the text 1C wrote: a log may repeat what it was given.
    /// A <c>key=value</c> of a password key loses its whole value; elsewhere a password is replaced wherever it stands, a short one (under
    /// four characters) only as a word of its own, so that a password «a» leaves the rest of the text readable. One pass over the original
    /// text, the longest password first: one password inside another leaves nothing of the longer, and «***» is never matched again.</summary>
    internal static string Redact(string text, NewBaseRequest request)
    {
        if (text.Length == 0 || request.ConnectionString.Trim().Length == 0) return text;
        var (pairs, parsed) = SecretPairs(request.ConnectionString.Trim());
        var keys = pairs.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A password as given and as written again for 1C (a single quote doubled inside single quotes).
        var secrets = pairs.Select(x => x.Value).Where(x => x.Length > 0).SelectMany(x => new[] { x, x.Replace("'", "''") })
            .Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ToArray();
        if (secrets.Length == 0) return parsed ? text : text + "\n" + Unparsed;
        var redacted = Hide(text, keys, secrets);
        return parsed ? redacted : redacted + "\n" + Unparsed;
    }

    private const string Unparsed = "Строка подключения не разобрана: пароли скрыты по её тексту, проверьте журнал перед тем, как передать его.";

    // The password keys of the string with their values. A string the parser refuses (Grok full audit 24.10, P3) is split by «;» and «=»
    // with the quotes of a value taken off: the passwords the request holds are hidden anyway, and the text says the string was not parsed.
    private static (List<(string Key, string Value)> Pairs, bool Parsed) SecretPairs(string connection)
    {
        try
        {
            var parts = new DbConnectionStringBuilder { ConnectionString = connection };
            return ([.. parts.Keys.Cast<string>().Where(Secret).Select(x => (x, parts[x]?.ToString() ?? ""))], true);
        }
        catch (ArgumentException)
        {
            var found = Regex.Matches(connection, @"(?<key>[^;=]+?)\s*=\s*(?<value>'(?:[^']|'')*'|""(?:[^""]|"""")*""|[^;]*)")
                .Where(x => Secret(x.Groups["key"].Value.Trim()))
                .Select(x => (x.Groups["key"].Value.Trim(), Unquote(x.Groups["value"].Value.Trim())));
            return ([.. found], false);
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0] ? value[1..^1].Replace(new string(value[0], 2), value[0].ToString())
        : value.Trim('\'', '"');

    private static string Hide(string text, string[] keys, string[] secrets)
    {
        var alone = string.Join("|", secrets.Select(x => x.Length >= 4 ? Regex.Escape(x) : $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(x)}(?![\p{{L}}\p{{N}}])"));
        // A key starts the text or follows a separator of the string (a key may itself start with punctuation, so not \b).
        var pairs = $@"(?<key>(?<=^|[\s;'""(])(?:{string.Join("|", keys.Select(Regex.Escape))})\s*=\s*)(?<value>'(?:[^']|'')*'|""(?:[^""]|"""")*""|[^;\s]*)";
        return Regex.Replace(text, pairs + "|" + alone, match =>
        {
            if (!match.Groups["key"].Success) return "***";
            var value = match.Groups["value"].Value;
            var quote = value.Length > 0 && value[0] is '\'' or '"' ? value[0].ToString() : "";
            return match.Groups["key"].Value + quote + "***" + quote;
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool Secret(string key) => key.Contains("pwd", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // Only a log this class made: in the folder of the plan (case matters where the file system makes it matter), with its own name.
    private static bool Owned(CreationPlan plan, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(plan.LogDirectory), comparison) &&
                name.StartsWith("bn-create-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // A log that cannot be removed is told by the run (Codex full audit 24.10, P1-3), not taken as removed.
    private static void Forget(CreationPlan plan, string path, Cleanup cleanup)
    {
        if (!Owned(plan, path)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanup.Kept.Add(path); }
    }
}
