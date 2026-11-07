using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace StartManager.Core;

/// <summary>Who the batch configurator logs in as: «Использовать аутентификацию ОС» (/WA+), a 1C user (/N, /P, /WA-) or nobody.
/// Nobody passes nothing (1C may log in by the OS account), unless <see cref="AskCredentials"/> — «Запрашивать имя и пароль» of the
/// list — forbids that: then /WA- alone (Codex audit, part 2). OS authentication is a separate choice and wins.</summary>
public sealed record BackupCredentials(string User = "", string Password = "", bool OsAuthentication = false, bool AskCredentials = false)
{
    /// <summary>The configurator login of the base's page «Аутентификация»: OS authentication, else its saved designer user with the
    /// password of <see cref="PasswordVault"/>, else nobody.</summary>
    public static BackupCredentials FromBase(BaseEntry entry, Func<PasswordRole, string?> password)
    {
        var auth = entry.Authentication ?? new AuthenticationSettings();
        if (auth.OsAuthentication) return new(OsAuthentication: true);
        return auth.DesignerLogin && auth.DesignerUser.Length > 0 ? new(auth.DesignerUser, password(PasswordRole.Designer) ?? "", AskCredentials: entry.AskCredentials)
            : new(AskCredentials: entry.AskCredentials);
    }

    internal void Validate()
    {
        if (User is null || Password is null) throw new ArgumentException("Повреждены имя пользователя или пароль.");
        if (User.Any(char.IsControl) || Password.Contains('\0'))
            throw new ArgumentException("Имя пользователя и пароль не должны содержать переносы строк и управляющие символы.");
    }

    internal IEnumerable<string> Arguments()
    {
        if (OsAuthentication) { yield return "/WA+"; yield break; }
        if (User.Trim().Length == 0) { if (AskCredentials) yield return "/WA-"; yield break; }
        yield return "/N"; yield return User.Trim();
        if (Password.Length > 0) { yield return "/P"; yield return Password; }
        yield return "/WA-";
    }

    /// <summary>The text 1C wrote without the password: the rules of <see cref="InfobaseCreation.Redact"/> for a password key.</summary>
    internal string Redact(string text)
    {
        if (Password.Length == 0 || text.Length == 0) return text;
        var connection = new DbConnectionStringBuilder { ["Pwd"] = Password }.ConnectionString;
        return InfobaseCreation.Redact(text, new NewBaseRequest { ConnectionString = connection });
    }

    public override string ToString() => OsAuthentication ? "/WA+" : User;
}

/// <summary>A stage of a backup or a restore for the window of progress: <paramref name="Total"/> 0 is a stage without a measure
/// (1C works); otherwise <paramref name="Done"/> of <paramref name="Total"/> bytes.</summary>
public sealed record BackupProgress(string Stage, long Done = 0, long Total = 0);

/// <summary>A backup of one base by one scenario («Выполнение сценария»). <see cref="Folder"/> and the folders 2–3 of the scenario
/// may hold macros; they are replaced with <see cref="Values"/>. An empty <see cref="FileName"/> is made by the scenario's template.</summary>
public sealed record BackupRequest
{
    /// <summary>The thick client 1cv8 for DT and CF; not used for ZIP.</summary>
    public string Client { get; init; } = "";
    public BaseEntry Base { get; init; } = new();
    public BackupScenario Scenario { get; init; } = new();
    /// <summary>«Выгрузить в каталог:».</summary>
    public string Folder { get; init; } = "";
    /// <summary>«Имя файла:» when the user typed it (the extension of the format is added when missing).</summary>
    public string FileName { get; init; } = "";
    public BackupCredentials Credentials { get; init; } = new();
    /// <summary>The name's base, prefix, computer and time; also the time of the journal record.</summary>
    public BackupMacroValues Values { get; init; } = new("", "", "", DateTime.Now);
    /// <summary>The user agreed to replace the files of <see cref="BackupPlan.Existing"/>.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>The request resolved: the folders, the name, the files it will make and those already there. Made by
/// <see cref="BackupRun.Plan"/> only.</summary>
public sealed class BackupPlan
{
    internal BackupPlan(BackupRequest request, string folder, string fileName, IEnumerable<string> copies, IEnumerable<string> existing, IEnumerable<string> arguments)
    {
        Request = request; Folder = folder; FileName = fileName;
        Copies = Array.AsReadOnly(copies.ToArray()); Existing = Array.AsReadOnly(existing.ToArray()); Arguments = Array.AsReadOnly(arguments.ToArray());
    }
    public BackupRequest Request { get; }
    /// <summary>The folder of the backup, macros replaced.</summary>
    public string Folder { get; }
    /// <summary>The name of the backup; a split ZIP gets «.001», «.002»… after it.</summary>
    public string FileName { get; }
    public string Target => Path.Combine(Folder, FileName);
    /// <summary>«2. Каталог» and «3. Каталог» of the scenario that are on, macros replaced; the ready file is copied there.</summary>
    public ReadOnlyCollection<string> Copies { get; }
    /// <summary>Files of this backup already there (in any of its folders): the UI asks «replace?» and plans again with
    /// <see cref="BackupRequest.Overwrite"/>.</summary>
    public ReadOnlyCollection<string> Existing { get; }
    /// <summary>DT/CF: the arguments of the configurator without the temporary file and the log (those are made by the run).</summary>
    public ReadOnlyCollection<string> Arguments { get; }
    /// <summary>The arguments as they may be shown: the password and the access code of the lock hidden.</summary>
    public IEnumerable<string> ShownArguments => BackupLock.Shown(Arguments);
    /// <summary>Where the command of the configurator (/DumpIB, /DumpCfg) begins: /UC goes right before it at the start of 1C.</summary>
    internal int CommandAt { get; init; }
    public long? VolumeBytes => Request.Scenario.Format == BackupFormat.Zip ? Request.Scenario.VolumeBytes : null;
    /// <summary>Variables added to the environment of 1C (the fake client of the tests).</summary>
    internal Dictionary<string, string> Environment { get; } = [];
}

/// <param name="Files">The backup and its copies (every volume) as recorded in the journal.</param>
/// <param name="Warnings">What went wrong after the backup itself was made: a copy, the journal, the rotation.</param>
/// <param name="Exists">The run refused because files of the backup are there and replacing them was not agreed.</param>
/// <param name="LogPath">The log of 1C kept in the folder after a failure; null otherwise.</param>
public sealed record BackupResult(bool Success, string Message, string Log = "", IReadOnlyList<string>? Files = null, IReadOnlyList<string>? Warnings = null,
    bool Exists = false, string? LogPath = null, BackupRecord? Record = null)
{
    public IReadOnlyList<string> Files { get; init; } = Files ?? [];
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];
}

/// <summary>One backup or restore at a time in this program (the original: «Дождитесь завершения внешнего процесса!»).</summary>
public static class BackupOperation
{
    public const string BusyMessage = "Дождитесь завершения текущей операции резервирования или загрузки.";
    private static int running;
    public static bool Running => Volatile.Read(ref running) != 0;

    /// <summary>The right to run; null while another operation runs. Disposing gives it back.</summary>
    public static IDisposable? TryBegin() => Interlocked.CompareExchange(ref running, 1, 0) == 0 ? new Lease() : null;

    private sealed class Lease : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Volatile.Write(ref running, 0); }
    }
}

/// <summary>Making a backup: DT (/DumpIB) and CF (/DumpCfg) by the batch configurator, ZIP of the files of a file base; then the
/// copies into the folders 2–3 of the scenario, the journal record and the rotation. Every file is written under a temporary name
/// and renamed when complete: a final name never holds a partial file.</summary>
public static class BackupRun
{
    internal const string BaseFile = "1Cv8.1CD";
    internal const string Partial = ".bn-partial-";
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Checks the request and resolves its folders and name. Refusals are <see cref="ArgumentException"/> with the text for the user.</summary>
    public static BackupPlan Plan(BackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = request.Base;
        var scenario = request.Scenario;
        entry.Validate();
        scenario.Validate();
        request.Credentials.Validate();
        if (entry.Kind == ConnectionKind.Web) throw new ArgumentException("Базу на веб-сервере выгрузить нельзя: конфигуратор к ней не подключается.");
        if (scenario.Format == BackupFormat.Zip && entry.Kind != ConnectionKind.File)
            throw new ArgumentException("Архивирование файлов (ZIP) доступно только для файлового варианта информационной базы.");
        if (entry.Kind == ConnectionKind.File)
        {
            if (!Directory.Exists(entry.Location)) throw new ArgumentException($"Каталог базы не найден: {entry.Location}.");
            if (!File.Exists(Path.Combine(entry.Location, BaseFile))) throw new ArgumentException($"В каталоге {entry.Location} нет файла информационной базы {BaseFile}.");
        }
        if (scenario.Format != BackupFormat.Zip) CheckClient(request.Client);
        var folder = Resolve(request.Folder, request.Values, "Выгрузить в каталог");
        var name = Name(request);
        var copies = new List<string>();
        foreach (var (use, path, title) in new[] { (scenario.CopiesToFolder2, scenario.Folder2, "2. Каталог"), (scenario.CopiesToFolder3, scenario.Folder3, "3. Каталог") })
        {
            if (!use || path.Trim().Length == 0) continue;
            var copy = Resolve(path, request.Values, title);
            if (!Same(copy, folder) && !copies.Any(x => Same(x, copy))) copies.Add(copy);
        }
        if (entry.Kind == ConnectionKind.File)
        {
            // The backup inside the files it is made of would be read while written (ZIP) or replaced by a restore.
            foreach (var target in copies.Prepend(folder))
                if (Same(target, entry.Location)) throw new ArgumentException("Каталог резервной копии не должен совпадать с каталогом базы.");
        }
        var volumes = scenario.Format == BackupFormat.Zip && scenario.VolumeBytes is not null;
        var existing = copies.Prepend(folder).SelectMany(x => Existing(x, name, volumes)).ToList();
        var arguments = new List<string>();
        var commandAt = 0;
        if (scenario.Format != BackupFormat.Zip)
        {
            arguments.AddRange(Connection(entry));
            arguments.AddRange(request.Credentials.Arguments());
            // /UC of a locked file base is added at the start of 1C, from the lock as it is then (BackupLock.Arguments).
            commandAt = arguments.Count;
            arguments.Add(scenario.Format == BackupFormat.Dt ? "/DumpIB" : "/DumpCfg");
        }
        return new BackupPlan(request, folder, name, copies, existing, arguments) { CommandAt = commandAt };
    }

    internal static void CheckClient(string client)
    {
        if (string.IsNullOrWhiteSpace(client) || !Path.IsPathFullyQualified(client) || !File.Exists(client))
            throw new ArgumentException("Клиент 1С не найден. Выберите установленную платформу.");
        if (PlatformCatalog.IsThinClient(client))
            throw new ArgumentException("Для выгрузки и загрузки нужен толстый клиент 1cv8; найден только тонкий клиент 1cv8c.");
    }

    /// <summary>The configurator on the base: the folder of a file base, the server\name of a server base.</summary>
    internal static IEnumerable<string> Connection(BaseEntry entry) => entry.Kind == ConnectionKind.File
        ? ["DESIGNER", "/F", Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Location)), "/DisableStartupDialogs"]
        : ["DESIGNER", "/S", entry.Location.Trim(), "/DisableStartupDialogs"];

    private static string Resolve(string path, BackupMacroValues values, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Укажите «{title}».");
        var expanded = BackupName.Folder(path.Trim(), values);
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"«{title}»: укажите полный путь ({expanded}).");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static string Name(BackupRequest request)
    {
        var format = request.Scenario.Format;
        if (request.FileName.Trim() is not { Length: > 0 } typed) return BackupName.FileName(request.Scenario.Template, request.Values, format);
        if (BackupName.Clean(typed) != typed || typed.Trim('.', ' ').Length == 0)
            throw new ArgumentException("Имя файла содержит недопустимые символы.");
        var extension = BackupName.Extension(format);
        return typed.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? typed : typed + extension;
    }

    internal static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    internal static bool Inside(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(root, PathComparison) || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>The volumes «name.001», «name.002»… in a folder, in order of their numbers.</summary>
    internal static List<string> Volumes(string folder, string name)
    {
        if (!Directory.Exists(folder)) return [];
        var pattern = new Regex("^" + Regex.Escape(name) + @"\.(\d{3,})$", OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        // A number too long for any real set is not a volume (Grok audit, part 2).
        return [.. Directory.EnumerateFiles(folder).Select(x => (Path: x, Match: pattern.Match(Path.GetFileName(x))))
            .Where(x => x.Match.Success && VolumeNumber(x.Path) is not null).OrderBy(x => VolumeNumber(x.Path)).Select(x => x.Path)];
    }

    /// <summary>The file name of the volume of a split archive: numbered after the name, as 7-Zip names them («name.001»).</summary>
    internal static string VolumeName(string name, long number) => $"{name}.{number:000}";

    /// <summary>The name of the set a file «name.NNN» is a volume of; null when the file name is not a volume's.</summary>
    internal static string? VolumeSet(string fileName) =>
        Regex.Match(fileName, @"^(?<name>.+)\.(?<number>\d{3,})$") is { Success: true } volume ? volume.Groups["name"].Value : null;

    /// <summary>The number of a volume «name.NNN»; null when the extension is not a number that fits.</summary>
    internal static long? VolumeNumber(string path) =>
        long.TryParse(Path.GetExtension(path).TrimStart('.'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static IEnumerable<string> Existing(string folder, string name, bool volumes) =>
        volumes ? Volumes(folder, name) : File.Exists(Path.Combine(folder, name)) ? [Path.Combine(folder, name)] : [];

    internal static string Random() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Makes the backup. The time limit is that of 1C (DT/CF); a cancelled or timed out 1C is killed with its children.
    /// The journal is required: every publication is serialized with the removals of the journal under its lock (Codex fourth
    /// recheck). The record is added and the scenario's «Хранить копий» applied; their failures are warnings.</summary>
    public static async Task<BackupResult> Run(BackupPlan plan, BackupJournalStore journal, Action<BackupProgress>? progress = null,
        TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        using var lease = BackupOperation.TryBegin();
        if (lease is null) return new(false, BackupOperation.BusyMessage);
        var request = plan.Request;
        using var baseLock = BackupBaseLock.TryAcquire(request.Base, out var lockFailure);
        if (baseLock is null) return new(false, lockFailure ?? BackupBaseLock.BusyMessage);
        var scenario = request.Scenario;
        var warnings = new List<string>();
        try
        {
            // The disk may have changed since the plan: checked again right before the start.
            if (scenario.Format != BackupFormat.Zip) CheckClient(request.Client);
            if (request.Base.Kind == ConnectionKind.File && !File.Exists(Path.Combine(request.Base.Location, BaseFile)))
                return new(false, $"Файл информационной базы не найден: {Path.Combine(request.Base.Location, BaseFile)}.");
        }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        var volumes = plan.VolumeBytes is not null;
        var existing = plan.Copies.Prepend(plan.Folder).SelectMany(x => Existing(x, plan.FileName, volumes)).ToList();
        if (existing.Count > 0 && !request.Overwrite)
            return new(false, "Файл резервной копии уже существует: " + existing[0] + (existing.Count > 1 ? $" (и ещё {existing.Count - 1})" : "") + ".", Exists: true);
        try { Directory.CreateDirectory(plan.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, $"Не удалось создать каталог {plan.Folder}: {ex.Message}"); }

        List<string> temporary;
        string log = "";
        if (scenario.Format == BackupFormat.Zip)
        {
            try { temporary = Archive(plan, progress, cancel); }
            catch (OperationCanceledException) { return new(false, "Выгрузка отменена."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return new(false, "Архивирование не выполнено: " + ex.Message); }
        }
        else
        {
            var (made, failure) = await Dump(plan, progress, timeout, cancel);
            if (failure is not null) return failure;
            temporary = [made.Path]; log = made.Log;
            if (made.Warning is { } leftover) warnings.Add(leftover);
        }

        // The final names: the volumes are numbered after the name, as 7-Zip names them.
        var finals = volumes ? temporary.Select((_, i) => Path.Combine(plan.Folder, VolumeName(plan.FileName, i + 1))).ToList() : [plan.Target];
        // The copies for the folders 2–3 are written under temporary names first, from the temporary files: the slow part stays
        // outside the lock of the journal.
        var staged = new List<(string Folder, List<(string Temporary, string Final)> Pairs)>();
        foreach (var copy in plan.Copies)
        {
            if (cancel.IsCancellationRequested) { warnings.Add($"Копирование в {copy} отменено."); continue; }
            try { staged.Add((copy, Stage(temporary, finals, copy, progress, cancel))); }
            catch (OperationCanceledException) { warnings.Add($"Копирование в {copy} отменено."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"Не скопировано в {copy}: {ex.Message}"); }
        }
        void Discard() { foreach (var file in temporary.Concat(staged.SelectMany(x => x.Pairs.Select(p => p.Temporary)))) TryDelete(file); }

        // Publication — the renames to the final names, their rollback, the journal record and the rotation — runs under the lock of
        // the journal, so it never interleaves with the check and removal of files by another window (Codex third recheck). The base
        // lock is always taken first (at the start of the run), the journal lock second.
        BeforePublish?.Invoke();
        BackupRecord? record = null;
        BackupResult? failed = null;
        var files = new List<string>(finals);
        long size = 0;
        void Publish()
        {
            try { Commit(temporary.Zip(finals).ToList(), request.Overwrite, warnings, volumes ? Surplus(plan.Folder, plan.FileName, finals.Count) : null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Discard();
                // What the rollback could not put back is told with the place of the kept files (Codex audit, part 2).
                var message = (ex is FileExistsException ? ex.Message : "Готовый файл не переименован: " + ex.Message) +
                    (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
                failed = new(false, message, log, Warnings: warnings, Exists: ex is FileExistsException);
                return;
            }
            size = finals.Sum(x => new FileInfo(x).Length);
            foreach (var (folder, pairs) in staged)
            {
                try
                {
                    Commit(pairs, request.Overwrite, warnings, volumes ? Surplus(folder, plan.FileName, pairs.Count) : null);
                    files.AddRange(pairs.Select(x => x.Final));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    foreach (var (temp, _) in pairs) TryDelete(temp);
                    warnings.Add($"Не скопировано в {folder}: {ex.Message}");
                }
            }
            record = new BackupRecord
            {
                BaseId = request.Base.Id, ScenarioId = scenario.Id, Format = scenario.Format, Time = new DateTimeOffset(request.Values.Time),
                Files = files, Size = size, Stamps = [.. files.Select(BackupFileStamp.Of)]
            };
            try
            {
                journal.Add(record);
                if (scenario.KeepCopies > 0) warnings.AddRange(journal.Rotate(request.Base.Id, scenario.Id, scenario.KeepCopies));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { warnings.Add("Журнал выгрузок не обновлён: " + ex.Message); record = null; }
        }
        ArgumentNullException.ThrowIfNull(journal);
        // A journal held by another window too long: nothing is published, the temporary files go, the old files stay.
        try { journal.Locked(Publish); }
        catch (Exception ex) when (failed is null && record is null && ex is IOException or UnauthorizedAccessException)
        {
            Discard();
            // What the run already found (a raw log of 1C that stays, with its place) is told here too (Codex re-check, 19.10).
            return new(false, "Резервная копия не опубликована: журнал выгрузок занят или недоступен. " + ex.Message + " Прежние файлы не изменены.", log,
                Warnings: warnings);
        }
        if (failed is not null) return failed;
        return new(true, "Резервная копия создана: " + finals[0] + (finals.Count > 1 ? $" (томов: {finals.Count})" : "") + ".", log, files, warnings, Record: record);
    }

    /// <summary>Called right before the publication takes the journal lock (the tests use it).</summary>
    internal static Action? BeforePublish { get; set; }

    // ---- DT and CF: the batch configurator ----

    private static async Task<((string Path, string Log, string? Warning) Made, BackupResult? Failure)> Dump(BackupPlan plan, Action<BackupProgress>? progress, TimeSpan? timeout, CancellationToken cancel)
    {
        var request = plan.Request;
        var extension = BackupName.Extension(request.Scenario.Format);
        // The temporary name keeps the extension of the format: 1C is given a name it would make itself.
        var temporary = Path.Combine(plan.Folder, Path.GetFileNameWithoutExtension(plan.FileName) + Partial + Random() + extension);
        var title = request.Scenario.Format == BackupFormat.Dt ? "Выгрузка информационной базы" : "Выгрузка конфигурации";
        if (cancel.IsCancellationRequested) return (default, new(false, "Выгрузка отменена."));
        progress?.Invoke(new(title + "…"));
        // 1C writes its /Out log into a private temporary folder; only the text without the password and the access code is kept
        // next to the backups (the original's DumpLog_….txt). The access code is that of the lock right now (Codex audit, 19.10).
        using var raw = RawLog.Create();
        var code = BackupLock.UnlockCode(request.Base);
        var arguments = BackupLock.Arguments(plan.Arguments, plan.CommandAt, code).Concat([temporary, "/Out", raw.Path]).ToList();
        var outcome = await Platform(request.Client, arguments, plan.Folder, plan.Environment, timeout, cancel);
        var log = request.Credentials.Redact(BackupLock.Mask(raw.Read(), code));
        var leftover = raw.Remove();
        string With(string message) => leftover is null ? message : message + " " + leftover;
        if (outcome.Failure is { } refused)
        {
            if (outcome.Ended) TryDelete(temporary);
            var left = outcome.Ended ? "" : $" Процесс 1С (PID {outcome.Pid}) не удалось остановить: завершите его вручную." +
                (File.Exists(temporary) ? $" Незавершённый файл: {temporary}." : "");
            return (default, new(false, With((outcome.Cancelled ? "Выгрузка отменена." : $"{title}: {refused}") + left), log));
        }
        if (outcome.Exit != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
        {
            TryDelete(temporary);
            var kept = KeepLog(plan.Folder, log);
            var reason = log.Length > 0 ? log : outcome.Exit != 0 ? ExitText(outcome.Exit) : "1С не создала файл выгрузки.";
            return (default, new(false, With($"{title}: {reason}"), log, LogPath: kept));
        }
        return ((temporary, log, leftover), null);
    }

    internal sealed record PlatformOutcome(int Exit, string? Failure, bool Cancelled, bool Ended, int Pid);

    /// <summary>One start of 1C with a time limit (30 minutes by default) and cancellation; a stopped 1C is killed with its children.</summary>
    internal static async Task<PlatformOutcome> Platform(string client, IEnumerable<string> arguments, string folder, IReadOnlyDictionary<string, string> environment,
        TimeSpan? timeout, CancellationToken cancel)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        var start = new ProcessStartInfo(client) { UseShellExecute = false, WorkingDirectory = folder, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить 1С.");
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(limit);
            try { await process.WaitForExitAsync(timer.Token); }
            catch (OperationCanceledException)
            {
                var ended = InfobaseCreation.Terminate(process);
                var cancelled = cancel.IsCancellationRequested;
                return new(-1, cancelled ? "отменено." : $"1С не завершила работу за {limit.TotalMinutes:0.##} мин" + (ended ? " и остановлена." : "."),
                    cancelled, ended, InfobaseCreation.SafeId(process));
            }
            return new(process.ExitCode, null, false, true, 0);
        }
        catch (System.ComponentModel.Win32Exception ex) { return new(-1, Launcher.ErrorMessage(ex), false, true, 0); }
        catch (InvalidOperationException ex) { return new(-1, ex.Message, false, true, 0); }
    }

    /// <summary>The text for a batch 1C that ended with a code and wrote no log. On Linux without an X or Wayland display
    /// 1C fails to initialize GTK+ and ends with 255 before writing /Out: that is told plainly.</summary>
    internal static string ExitText(int exit) => ExitText(exit, OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable);

    internal static string ExitText(int exit, bool linux, Func<string, string?> variable) =>
        linux && exit == 255 && string.IsNullOrWhiteSpace(variable("DISPLAY")) && string.IsNullOrWhiteSpace(variable("WAYLAND_DISPLAY"))
            ? "1С завершилась с кодом 255: не удалось открыть окно — нет графического сеанса (переменная DISPLAY не задана). Запустите программу в графическом сеансе."
            : $"1С завершилась с кодом {exit}.";

    /// <summary>The text of a /Out log: by its BOM, else UTF-8, else — bytes that are not UTF-8 — the ANSI code page of the system
    /// (1251 on a Russian Windows; 1251 where the system has none), as a platform may write it (Grok audit, part 2).</summary>
    internal static string ReadLog(string path, Encoding? fallback = null)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var bytes = File.ReadAllBytes(path);
            return Decode(bytes, fallback).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    internal static string Decode(byte[] bytes, Encoding? fallback = null)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return (fallback ?? Ansi()).GetString(bytes); }
    }

    private static Encoding Ansi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var page = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try { return OperatingSystem.IsWindows() && page is not (0 or 65001) ? Encoding.GetEncoding(page) : Encoding.GetEncoding(1251); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.GetEncoding(1251); }
    }

    // The log of a failure next to the backups (the original's DumpLog_….txt): the text without the password and the access code.
    private static string? KeepLog(string folder, string redacted)
    {
        if (redacted.Length == 0) return null;
        var path = Path.Combine(folder, "DumpLog_" + Random() + ".txt");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(redacted)]);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(path); return null; }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- ZIP of the files of a file base ----

    /// <summary>The files of a file base that go into its archive: every file of its folder except the folders of this backup
    /// that lie inside it. A link is refused, as the copy of a base does: it could lead anywhere.</summary>
    internal static List<string> BaseFiles(string location, IEnumerable<string> excluded)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        var skip = excluded.Where(x => Inside(x, source)).ToList();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        var files = new List<string>();
        var pending = new Stack<string>([source]);
        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options))
            {
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"В каталоге базы есть ссылка {Path.GetRelativePath(source, entry.FullName)}; такую базу архивируйте вручную.");
                if (skip.Any(x => Inside(entry.FullName, x))) continue;
                // The lock of the base (the button «Установить блокировку ИБ») is an operational state, not data: a restored
                // base must not come back locked (Codex audit, part 2).
                if (entry is FileInfo && IsLock(source, entry.FullName)) continue;
                if (entry is DirectoryInfo) pending.Push(entry.FullName); else files.Add(entry.FullName);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>A lock file in the root of the base: 1Cv8.cdn, or one kept aside under a private name by <see cref="BackupLock.Release"/>
    /// (<see cref="BackupLock.IsLockName"/>). Neither is data: not archived, not copied before a load, not unpacked.</summary>
    internal static bool IsLock(string folder, string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), PathComparison) &&
        BackupLock.IsLockName(Path.GetFileName(path));

    /// <summary>Windows: the data file of the base opened for this program alone — others may neither read nor write it, only rename
    /// it, so a restore can still move it: 1C cannot open the base while it is held. Null elsewhere (Linux has no mandatory locks)
    /// and without the file. A base already open elsewhere is refused.</summary>
    /// <param name="allowMove">A restore moves the held file itself: rename and delete are shared. A backup shares nothing, so the
    /// live file cannot be removed while it is read (Grok audit, part 2).</param>
    internal static FileStream? HoldBase(string location, bool allowMove = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var data = Path.Combine(location, BaseFile);
        if (!File.Exists(data)) return null;
        try { return new FileStream(data, FileMode.Open, FileAccess.Read, allowMove ? FileShare.Delete : FileShare.None, 1, FileOptions.SequentialScan); }
        catch (IOException ex) when (SharingViolation(ex))
        { throw new IOException($"Файл {BaseFile} занят другой программой. Завершите все сеансы 1С с этой базой и повторите.", ex); }
    }

    /// <summary>Linux: the processes holding files of the base, as a refusal (checked before and after the critical part).</summary>
    internal static void CheckOpen(string location)
    {
        if (OpenFiles.Under(location) is { Count: > 0 } holders)
            throw new IOException($"База открыта в другой программе. Завершите все сеансы 1С с этой базой и повторите. {OpenFiles.Describe(holders)}");
    }

    /// <summary>ZIP compression by «Сжатие:»: .NET has no level above SmallestSize, so ULTRA is MAXIMUM.</summary>
    public static CompressionLevel Level(BackupCompression compression) => compression switch
    {
        BackupCompression.Save => CompressionLevel.NoCompression, BackupCompression.Fast => CompressionLevel.Fastest,
        BackupCompression.Normal => CompressionLevel.Optimal, _ => CompressionLevel.SmallestSize
    };

    // The archive under temporary names; the files written so far are removed on any failure.
    private static List<string> Archive(BackupPlan plan, Action<BackupProgress>? progress, CancellationToken cancel)
    {
        var location = plan.Request.Base.Location;
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        // Linux gives no sharing violation for a base open in 1C: its processes are found in /proc instead, before and after.
        CheckOpen(source);
        // Windows: the data file is held for the whole archive, so no session starts while the files are read (Codex audit, part 2).
        using var held = HoldBase(source);
        var data = Path.Combine(source, BaseFile);
        var files = BaseFiles(source, plan.Copies.Prepend(plan.Folder));
        // The size and time of every file before it is read: a file changed meanwhile (Linux has no lock against a session that
        // starts and ends during the archive) fails the backup instead of giving a torn copy (Grok audit, part 2).
        var before = files.Select(x => (Path: x, Stamp: Stamp(x))).ToList();
        var total = files.Sum(x => new FileInfo(x).Length);
        var temporary = Path.Combine(plan.Folder, plan.FileName + Partial + Random());
        VolumeWriteStream? split = null;
        Stream output = plan.VolumeBytes is { } volume
            ? split = new VolumeWriteStream(temporary, volume)
            : new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            long done = 0;
            var buffer = new byte[1 << 20];
            progress?.Invoke(new("Архивирование…", 0, total));
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var level = Level(plan.Request.Scenario.Compression);
                foreach (var file in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    var name = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = archive.CreateEntry(name, level);
                    entry.LastWriteTime = ZipTime(File.GetLastWriteTime(file));
                    try
                    {
                        var own = held is not null && string.Equals(file, data, PathComparison);
                        if (own) held!.Position = 0;
                        using var opened = own ? null : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                        var input = opened ?? held!;
                        using var target = entry.Open();
                        for (int read; (read = input.Read(buffer)) > 0;)
                        {
                            cancel.ThrowIfCancellationRequested();
                            target.Write(buffer, 0, read);
                            progress?.Invoke(new("Архивирование…", done += read, total));
                        }
                    }
                    catch (IOException ex) when (SharingViolation(ex))
                    {
                        throw new IOException($"Файл {Path.GetRelativePath(source, file)} занят другой программой. Завершите все сеансы 1С с этой базой и повторите.", ex);
                    }
                }
            }
            output.Flush();
            if (output is FileStream stream) stream.Flush(true);
            var parts = split?.Parts.ToList() ?? [temporary];
            output.Dispose();
            // After the archive is closed: its own file may lie inside the base folder.
            CheckOpen(source);
            if (before.FirstOrDefault(x => Stamp(x.Path) != x.Stamp) is { Path: { } changed })
                throw new IOException($"Файл {Path.GetRelativePath(source, changed)} изменился во время архивирования: копия не цельная. Завершите все сеансы 1С с этой базой и повторите.");
            return parts;
        }
        catch
        {
            output.Dispose();
            foreach (var part in split?.Parts.ToList() ?? [temporary]) TryDelete(part);
            throw;
        }
    }

    private static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
    }

    // A ZIP keeps times from 1980 to 2107.
    private static DateTimeOffset ZipTime(DateTime time) =>
        time.Year < 1980 ? new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local) : time.Year > 2107 ? new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local) : time;

    internal static bool SharingViolation(IOException ex) => OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is 32 or 33;

    // ---- Final names and copies ----

    /// <summary>Gives every temporary file its final name. A file already there is set aside first and removed only when every name
    /// is in place; on a failure the new files are removed and the old ones come back. <paramref name="surplus"/> — volumes of the
    /// earlier backup beyond the new count — are set aside in the same transaction: the folder holds either the whole old set or
    /// the whole new one, never a mix (Codex audit, part 2).</summary>
    internal static void Commit(List<(string Temporary, string Final)> pairs, bool overwrite, List<string> warnings, IReadOnlyList<string>? surplus = null)
    {
        var placed = new List<string>();
        var aside = new List<(string Old, string Final)>();
        try
        {
            foreach (var extra in surplus ?? [])
            {
                if (!overwrite) throw new FileExistsException("Файл резервной копии уже существует: " + extra + ".");
                var old = extra + ".bn-old-" + Random();
                File.Move(extra, old);
                aside.Add((old, extra));
            }
            foreach (var (temporary, final) in pairs)
            {
                if (File.Exists(final) || Directory.Exists(final))
                {
                    if (!overwrite || Directory.Exists(final)) throw new FileExistsException("Файл резервной копии уже существует: " + final + ".");
                    var old = final + ".bn-old-" + Random();
                    File.Move(final, old);
                    aside.Add((old, final));
                }
                File.Move(temporary, final);
                placed.Add(final);
            }
        }
        catch
        {
            foreach (var final in placed)
            {
                TryDelete(final);
                if (File.Exists(final)) warnings.Add($"Новый файл не удалён при откате: {final}.");
            }
            // In reverse: a name set aside twice gets its oldest file last. Overwrite: a new file that could not be removed is
            // replaced when the system allows it (Grok audit, part 2); a restore refuses a set that is still mixed.
            foreach (var (old, final) in Enumerable.Reverse(aside))
            {
                try { File.Move(old, final, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"Прежний файл {final} не возвращён: он сохранён как {old}."); }
            }
            throw;
        }
        foreach (var (old, _) in aside)
        {
            TryDelete(old);
            if (File.Exists(old)) warnings.Add($"Прежний файл не удалён: {old}.");
        }
    }

    // Volumes of an earlier backup of the same name beyond the new count would be joined with the new ones.
    private static List<string> Surplus(string folder, string name, int count) =>
        [.. Volumes(folder, name).Where(x => VolumeNumber(x) > count)];

    // The files of the backup copied into a folder under temporary names; the pairs to publish. Removed again on any failure.
    private static List<(string Temporary, string Final)> Stage(List<string> sources, List<string> finals, string folder, Action<BackupProgress>? progress,
        CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var total = sources.Sum(x => new FileInfo(x).Length);
        long done = 0;
        var pairs = new List<(string, string)>();
        try
        {
            var buffer = new byte[1 << 20];
            foreach (var (source, final) in sources.Zip(finals))
            {
                var temporary = Path.Combine(folder, Path.GetFileName(final) + Partial + Random());
                pairs.Add((temporary, Path.Combine(folder, Path.GetFileName(final))));
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    for (int read; (read = input.Read(buffer)) > 0;)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        progress?.Invoke(new($"Копирование в {folder}…", done += read, total));
                    }
                    output.Flush(true);
                }
            }
        }
        catch
        {
            foreach (var (temporary, _) in pairs) TryDelete(temporary);
            throw;
        }
        return pairs;
    }
}

/// <summary>One backup or restore of a base at a time across the processes of this program: a lock file per base in the temporary
/// folder of the user, held without sharing for the whole operation (Grok audit, part 2). Another user's program is not seen.</summary>
public static class BackupBaseLock
{
    public const string BusyMessage = "С этой базой уже выполняется выгрузка или загрузка в другом окне БазоНавигатора. Дождитесь её завершения.";

    /// <summary>The folder of the locks (replaced by the tests).</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>The folder of the locks of this user: Windows %TEMP% (per user), Linux $XDG_RUNTIME_DIR (per user, made by the
    /// system) or a folder of the temporary directory named with the uid — never a name shared by all users (Codex recheck).</summary>
    internal static string Folder()
    {
        if (FolderOverride is { } own) return own;
        if (OperatingSystem.IsWindows()) return Path.Combine(Path.GetTempPath(), "basenavigator-backup-locks");
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime))
            return Path.Combine(runtime, "basenavigator-backup-locks");
        return Path.Combine(Path.GetTempPath(), "basenavigator-backup-locks-" + getuid());
    }

    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint getuid();

    /// <summary>The lock file of a base: a file base by its physical folder, a server base by server\name.</summary>
    internal static string PathOf(BaseEntry entry)
    {
        var identity = entry.Kind == ConnectionKind.File ? BackupJournalStore.Identity(entry.Location.Trim()) : "srvr:" + entry.Location.Trim();
        if (OperatingSystem.IsWindows() || entry.Kind != ConnectionKind.File) identity = identity.ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return Path.Combine(Folder(), hash + ".lock");
    }

    /// <summary>The lock of the base; null while another process (or call) holds it — then <paramref name="failure"/> is null — or
    /// when the lock cannot be made at all (the folder or the file is denied): then <paramref name="failure"/> says why.</summary>
    public static IDisposable? TryAcquire(BaseEntry entry, out string? failure)
    {
        failure = null;
        try
        {
            var path = PathOf(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException ex) when (Held(ex)) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failure = "Не удалось создать блокировку операции с базой: " + ex.Message;
            return null;
        }
    }

    // Windows: a sharing or lock violation. Elsewhere .NET reports a held lock as EWOULDBLOCK (11).
    private static bool Held(IOException ex) => OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 32 or 33 : ex.HResult == 11;
}

/// <summary>A file of the backup is there and replacing it was not agreed.</summary>
public sealed class FileExistsException(string message) : IOException(message);

/// <summary>A write-only stream that cuts what is written into files «prefix.001», «prefix.002»… of a fixed size: the volumes of
/// 7-Zip (a byte split, joined by concatenation). It may seek back to update what was written, as the ZIP writer does.</summary>
internal sealed class VolumeWriteStream(string prefix, long size) : Stream
{
    private readonly List<string> parts = [];
    private FileStream? current;
    private int index = -1;
    private long position, length;

    public IReadOnlyList<string> Parts => parts;
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var part = (int)(position / size);
            var at = position % size;
            var stream = Open(part);
            stream.Position = at;
            var count = (int)Math.Min(buffer.Length, size - at);
            stream.Write(buffer[..count]);
            buffer = buffer[count..];
            position += count;
            length = Math.Max(length, position);
        }
    }

    private FileStream Open(int part)
    {
        if (part == index && current is not null) return current;
        if (part > parts.Count) throw new IOException("Тома архива пишутся по порядку.");
        current?.Flush(true);
        current?.Dispose();
        current = null;
        var path = $"{prefix}.{part + 1:000}";
        current = new FileStream(path, part < parts.Count ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (part == parts.Count) parts.Add(path);
        index = part;
        return current;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
        if (target < 0 || target > length) throw new IOException("Переход за пределы записанного архива.");
        return position = target;
    }

    public override void Flush() => current?.Flush(true);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { current?.Dispose(); current = null; }
        base.Dispose(disposing);
    }
}

/// <summary>A read-only seekable stream over the volumes of a split archive, joined in order.</summary>
internal sealed class VolumeReadStream : Stream
{
    private readonly string[] parts;
    private readonly long[] starts;
    private readonly long length;
    private FileStream? current;
    private int index = -1;
    private long position;

    public VolumeReadStream(IReadOnlyList<string> parts)
    {
        this.parts = [.. parts];
        starts = new long[parts.Count];
        for (var i = 0; i < parts.Count; i++) { starts[i] = length; length += new FileInfo(parts[i]).Length; }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && position < length)
        {
            var part = Array.FindLastIndex(starts, x => x <= position);
            // An empty volume shares its start with the next one: the next with data is read.
            while (part < parts.Length - 1 && starts[part + 1] <= position) part++;
            if (part != index)
            {
                current?.Dispose();
                current = new FileStream(parts[part], FileMode.Open, FileAccess.Read, FileShare.Read);
                index = part;
            }
            current!.Position = position - starts[part];
            var read = current.Read(buffer);
            if (read == 0) throw new IOException($"Том архива {parts[part]} короче ожидаемого.");
            buffer = buffer[read..];
            position += read;
            total += read;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
        if (target < 0) throw new IOException("Переход за начало архива.");
        return position = target;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { current?.Dispose(); current = null; }
        base.Dispose(disposing);
    }
}
