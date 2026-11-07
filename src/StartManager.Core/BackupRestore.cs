using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace StartManager.Core;

/// <summary>Loading a backup into an existing base («Загрузка данных», «Загрузить данные из резервной копии»).</summary>
public sealed record RestoreRequest
{
    /// <summary>The thick client 1cv8 for DT and CF; not used for ZIP.</summary>
    public string Client { get; init; } = "";
    public BaseEntry Base { get; init; } = new();
    /// <summary>«Файл:» — a .dt, a .cf, a .zip or any volume «name.zip.001» of a split archive.</summary>
    public string File { get; init; } = "";
    public BackupCredentials Credentials { get; init; } = new();
    /// <summary>The journal of backups: the volumes of a ZIP it recorded are compared with it before they are joined.</summary>
    public BackupJournalStore? Journal { get; init; }
    /// <summary>The user agreed to load although the base could not be checked for sessions (<see cref="RestoreResult.Unchecked"/>).</summary>
    public bool ProceedWithoutBusyCheck { get; init; }
}

/// <summary>The request checked: the format, the files (every volume of a split archive) and the arguments of the configurator.</summary>
public sealed class RestorePlan
{
    internal RestorePlan(RestoreRequest request, BackupFormat format, IEnumerable<string> files, IEnumerable<string> arguments)
    { Request = request; Format = format; Files = Array.AsReadOnly(files.ToArray()); Arguments = Array.AsReadOnly(arguments.ToArray()); }
    public RestoreRequest Request { get; }
    public BackupFormat Format { get; }
    public ReadOnlyCollection<string> Files { get; }
    /// <summary>DT/CF: the arguments of the configurator without the log (made by the run).</summary>
    public ReadOnlyCollection<string> Arguments { get; }
    /// <summary>The arguments as they may be shown: the password and the access code of the lock hidden.</summary>
    public IEnumerable<string> ShownArguments => BackupLock.Shown(Arguments);
    /// <summary>Where the command of the configurator (/RestoreIB, /LoadCfg) begins: /UC goes right before it at the start of 1C.</summary>
    internal int CommandAt { get; init; }
    /// <summary>A failed load can be undone: a file base (its files are copied aside first, or replaced only when all are unpacked).
    /// A server base cannot: the UI warns with <see cref="BackupRestore.ServerNotice"/> before the load.</summary>
    public bool Recoverable => Request.Base.Kind == ConnectionKind.File;
    internal Dictionary<string, string> Environment { get; } = [];
    internal string LogDirectory { get; init; } = Path.GetTempPath();
    /// <summary>The busy check (replaced by the tests).</summary>
    internal Func<BaseEntry, BaseBusy> Busy { get; set; } = x => BaseBusyCheck.Check(x);
    /// <summary>Linux: who holds files under the base folder (replaced by the tests); throws when /proc cannot be read.</summary>
    internal Func<string, IReadOnlyList<FileHolder>> OpenFilesProbe { get; set; } = x => OpenFiles.Under(x);

    /// <summary>The check right before the base changes: a holder refuses; an unreadable /proc refuses too, unless the user agreed
    /// to load without the check (Codex recheck, part 2).</summary>
    internal void CheckOpen(string location)
    {
        IReadOnlyList<FileHolder> holders;
        try { holders = OpenFilesProbe(location); }
        catch (IOException) when (Request.ProceedWithoutBusyCheck) { return; }
        if (holders.Count > 0)
            throw new IOException($"База открыта в другой программе. Завершите все сеансы 1С с этой базой и повторите. {OpenFiles.Describe(holders)}");
    }
}

/// <param name="Kept">A folder that holds the previous files of the base after a failure that could not put them all back.</param>
/// <param name="Unchecked">Nothing was done: the file base could not be checked for sessions. The UI asks and runs again with
/// <see cref="RestoreRequest.ProceedWithoutBusyCheck"/>.</param>
public sealed record RestoreResult(bool Success, string Message, string Log = "", string? Kept = null, bool Unchecked = false);

public static class BackupRestore
{
    public const string NoFile = "Не выбран файл!";
    public const string MissingFile = "Выбранный файл не существует!";
    /// <summary>The warning before a load into a server base: nothing here can undo a failed one.</summary>
    public const string ServerNotice = "Загрузка в серверную базу необратима: при сбое БазоНавигатор не сможет вернуть прежние данные. " +
        "Перед загрузкой сделайте резервную копию средствами сервера СУБД или кластера 1С.";

    /// <summary>The format of a file by its name: .dt, .cf, .zip and «.zip.NNN» (a volume); null for anything else.</summary>
    public static BackupFormat? FormatOf(string path)
    {
        var name = Path.GetFileName(path);
        if (Regex.IsMatch(name, @"\.zip\.\d{3,}$", RegexOptions.IgnoreCase)) return BackupFormat.Zip;
        return Path.GetExtension(name).ToLowerInvariant() switch { ".dt" => BackupFormat.Dt, ".cf" => BackupFormat.Cf, ".zip" => BackupFormat.Zip, _ => null };
    }

    /// <summary>Checks the request; refusals are <see cref="ArgumentException"/> with the texts of the original where it had them.</summary>
    public static RestorePlan Plan(RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.File)) throw new ArgumentException(NoFile);
        var file = request.File.Trim();
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new ArgumentException(MissingFile);
        var entry = request.Base;
        entry.Validate();
        request.Credentials.Validate();
        if (entry.Kind == ConnectionKind.Web) throw new ArgumentException("В базу на веб-сервере загрузить нельзя: конфигуратор к ней не подключается.");
        if (entry.Kind == ConnectionKind.File && !Directory.Exists(entry.Location)) throw new ArgumentException($"Каталог базы не найден: {entry.Location}.");
        var format = FormatOf(file) ?? throw new ArgumentException("Неизвестный формат файла. Выберите файл *.dt, *.cf или *.zip.");
        var files = new List<string> { Path.GetFullPath(file) };
        var arguments = new List<string>();
        var commandAt = 0;
        if (format == BackupFormat.Zip)
        {
            if (entry.Kind != ConnectionKind.File) throw new ArgumentException("Загрузка из ZIP-архива доступна только для файлового варианта информационной базы.");
            if (BackupRun.VolumeSet(Path.GetFileName(file)) is { } set)
                files = Volumes(Path.GetDirectoryName(Path.GetFullPath(file))!, set);
        }
        else
        {
            BackupRun.CheckClient(request.Client);
            arguments.AddRange(BackupRun.Connection(entry));
            arguments.AddRange(request.Credentials.Arguments());
            // /UC of a locked file base is added at the start of 1C, from the lock as it is then (BackupLock.Arguments).
            commandAt = arguments.Count;
            if (format == BackupFormat.Dt) { arguments.Add("/RestoreIB"); arguments.Add(files[0]); }
            else { arguments.Add("/LoadCfg"); arguments.Add(files[0]); arguments.Add("/UpdateDBCfg"); }
        }
        return new RestorePlan(request, format, files, arguments) { CommandAt = commandAt };
    }

    // Every volume from «.001» on, without a gap.
    private static List<string> Volumes(string folder, string name)
    {
        var all = BackupRun.Volumes(folder, name);
        if (VolumeGap(all, folder, name) is { } gap) throw new ArgumentException(gap);
        return all;
    }

    /// <summary>Why the volumes of a set found in a folder (<see cref="BackupRun.Volumes"/>) cannot be joined: no first volume, or a
    /// gap before the last one found; null when they run from «.001» without one. The load and the list's choice of a copy use it.</summary>
    internal static string? VolumeGap(IReadOnlyList<string> all, string folder, string name)
    {
        if (all.Count == 0 || !all[0].EndsWith(".001", StringComparison.Ordinal))
            return $"Не найден первый том архива: {Path.Combine(folder, BackupRun.VolumeName(name, 1))}.";
        for (var i = 0; i < all.Count; i++)
            if (!Path.GetFileName(all[i]).Equals(BackupRun.VolumeName(name, i + 1), BackupRun.PathComparison))
                return $"Не хватает тома архива {BackupRun.VolumeName(name, i + 1)}.";
        return null;
    }

    /// <summary>Loads the file. DT: /RestoreIB; CF: /LoadCfg with /UpdateDBCfg; ZIP: the archive (its volumes joined) is unpacked into
    /// a folder next to the base, then its files replace those of the base; the old files are removed only when every new one is in
    /// place, and on any failure the base stays as it was. A busy file base is refused.</summary>
    public static async Task<RestoreResult> Run(RestorePlan plan, Action<BackupProgress>? progress = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        using var lease = BackupOperation.TryBegin();
        if (lease is null) return new(false, BackupOperation.BusyMessage);
        var request = plan.Request;
        if (plan.Files.Any(x => !File.Exists(x))) return new(false, MissingFile);
        using var baseLock = BackupBaseLock.TryAcquire(request.Base, out var lockFailure);
        if (baseLock is null) return new(false, lockFailure ?? BackupBaseLock.BusyMessage);
        var busy = plan.Recoverable ? plan.Busy(request.Base) : new BaseBusy(BaseBusyState.NotChecked);
        if (busy.State == BaseBusyState.Busy) return new(false, BaseBusyCheck.BusyForLoad + (busy.Detail.Length > 0 ? " " + busy.Detail : ""));
        // A file base that could not be checked is not taken as free: the user decides (Grok audit, part 2). A session of another
        // user is invisible to the check on Linux in any case.
        if (busy.State == BaseBusyState.NotChecked && plan.Recoverable && !request.ProceedWithoutBusyCheck)
            return new(false, BaseBusyCheck.NotCheckedForLoad + (busy.Detail.Length > 0 ? " " + busy.Detail : ""), Unchecked: true);
        if (plan.Format == BackupFormat.Zip)
        {
            try { Unpack(plan, progress, cancel); return new(true, "Данные информационной базы загружены из архива."); }
            catch (OperationCanceledException) { return new(false, "Загрузка отменена. Информационная база не изменена."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return new(false, "Загрузка не выполнена: " + ex.Message); }
        }
        try { BackupRun.CheckClient(request.Client); }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        var title = plan.Format == BackupFormat.Dt ? "Загрузка информационной базы" : "Загрузка конфигурации";
        if (cancel.IsCancellationRequested) return new(false, "Загрузка отменена.");
        // A file base is copied aside first: a load that fails half way (or is stopped) is undone (Codex audit, part 2).
        SafetyCopy? safety = null;
        if (plan.Recoverable)
        {
            try { safety = SafetyCopy.Make(plan, request.Base.Location, progress, cancel); }
            catch (OperationCanceledException) { return new(false, "Загрузка отменена. Информационная база не изменена."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, "Загрузка не выполнена: не удалось сделать страховочную копию базы. " + ex.Message + " Информационная база не изменена."); }
        }
        progress?.Invoke(new(title + "…"));
        // The access code of the lock as it is right before 1C starts (after the questions and the safety copy); the /Out log in a
        // private temporary folder, read with the password and the code hidden, then removed (Codex audit of the lock fix, 19.10).
        string log; string? leftover; BackupRun.PlatformOutcome outcome;
        using (var raw = RawLog.Create())
        {
            var code = BackupLock.UnlockCode(request.Base);
            outcome = await BackupRun.Platform(request.Client, BackupLock.Arguments(plan.Arguments, plan.CommandAt, code).Concat(["/Out", raw.Path]),
                plan.LogDirectory, plan.Environment, timeout, cancel);
            log = request.Credentials.Redact(BackupLock.Mask(raw.Read(), code));
            leftover = raw.Remove();
        }
        string? message = null;
        if (outcome.Failure is { } refused)
            message = (outcome.Cancelled ? "Загрузка отменена." : $"{title}: {refused}") +
                (outcome.Ended ? "" : $" Процесс 1С (PID {outcome.Pid}) не удалось остановить: завершите его вручную.");
        else if (outcome.Exit != 0) message = $"{title}: " + (log.Length > 0 ? log : BackupRun.ExitText(outcome.Exit));
        if (leftover is not null) message = message is null ? null : message + " " + leftover;
        if (message is null)
        {
            safety?.Discard();
            return new(true, title + " завершена." + (leftover is null ? "" : " " + leftover), log);
        }
        if (safety is null) return new(false, message + " Серверная база могла измениться частично: проверьте её или восстановите средствами сервера.", log);
        // While 1C may still write, nothing is put back: the copy stays for the user.
        if (!outcome.Ended) return new(false, message + $" Прежние файлы базы сохранены в каталоге {safety.Folder}: верните их после завершения 1С.", log, safety.Folder);
        var lost = safety.PutBack();
        return lost.Count == 0
            ? new(false, message + " Информационная база возвращена в прежнее состояние.", log)
            : new(false, message + $" Не все прежние файлы базы возвращены ({string.Join(", ", lost)}): они в каталоге {safety.Folder}.", log, safety.Folder);
    }

    /// <summary>The files in the root of a file base copied next to it before a DT/CF load. The data of a file base is 1Cv8.1CD in its
    /// root; the folders inside (the event log, the full-text index, a folder of backups) are not changed by a load and may be large,
    /// so they are not copied. Free space is checked first; any failure removes the copy and nothing has been changed yet.</summary>
    private sealed class SafetyCopy
    {
        private SafetyCopy(string folder, List<(string Original, string Copy)> files) { Folder = folder; Files = files; }
        public string Folder { get; }
        private List<(string Original, string Copy)> Files { get; }

        public static SafetyCopy Make(RestorePlan plan, string location, Action<BackupProgress>? progress, CancellationToken cancel)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var parent = Path.GetDirectoryName(source) ?? throw new IOException("У каталога базы нет родительского каталога.");
            // The lock file 1Cv8.cdn is an operational state, not data: never copied, so a failed load never puts an old lock over one
            // changed meanwhile (Codex audit of the lock fix, 19.10).
            var originals = new DirectoryInfo(source).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
                .Where(x => !BackupRun.IsLock(source, x.FullName)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
            // Size and time of every file before the copy: a file changed while copied (Linux does not keep a session out) makes the
            // copy untrustworthy, and the load is refused before 1C starts (Codex recheck, part 2).
            var before = originals.Select(x => (x.FullName, x.Length, x.LastWriteTimeUtc)).ToList();
            if (originals.FirstOrDefault(x => x.LinkTarget is not null) is { } link) throw new IOException($"Файл базы {link.Name} — ссылка; такую базу загрузите вручную.");
            var total = originals.Sum(x => x.Length);
            if (Free(parent) is { } free && free < total + (64L << 20))
                throw new IOException($"Недостаточно места: нужно {SizeFormat.Short(total)}, свободно {SizeFormat.Short(free)}.");
            var folder = Path.Combine(parent, "." + Path.GetFileName(source) + ".bn-before-restore-" + BackupRun.Random());
            var files = new List<(string, string)>();
            Directory.CreateDirectory(folder);
            try
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                foreach (var original in originals)
                {
                    var copy = Path.Combine(folder, original.Name);
                    try
                    {
                        using var input = new FileStream(original.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                        using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                        for (int read; (read = input.Read(buffer)) > 0;)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            progress?.Invoke(new("Страховочная копия базы…", done += read, total));
                        }
                        output.Flush(true);
                    }
                    catch (IOException ex) when (BackupRun.SharingViolation(ex))
                    { throw new IOException($"Файл {original.Name} занят другой программой. Завершите все сеансы 1С с этой базой и повторите.", ex); }
                    File.SetLastWriteTimeUtc(copy, original.LastWriteTimeUtc);
                    files.Add((original.FullName, copy));
                }
                plan.CheckOpen(source);
                foreach (var (path, length, written) in before)
                {
                    var now = new FileInfo(path);
                    if (!now.Exists || now.Length != length || now.LastWriteTimeUtc != written)
                        throw new IOException($"Файл {Path.GetFileName(path)} изменился во время страховочного копирования: копия не цельная. Завершите все сеансы 1С с этой базой и повторите.");
                }
            }
            catch { RemoveFolder(folder); throw; }
            return new SafetyCopy(folder, files);
        }

        // The free bytes of the volume of a folder; null where it cannot be told (a network share).
        private static long? Free(string folder)
        {
            try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Every copied file back in its place; the names that could not be put back (then the folder stays).</summary>
        public List<string> PutBack()
        {
            var lost = new List<string>();
            foreach (var (original, copy) in Files)
            {
                try
                {
                    if (File.Exists(original)) File.SetAttributes(original, FileAttributes.Normal);
                    File.Move(copy, original, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(Path.GetFileName(original)); }
            }
            if (lost.Count == 0) RemoveFolder(Folder);
            return lost;
        }

        public void Discard() => RemoveFolder(Folder);
    }

    // ---- ZIP ----

    private static void Unpack(RestorePlan plan, Action<BackupProgress>? progress, CancellationToken cancel)
    {
        var location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Request.Base.Location));
        var parent = Path.GetDirectoryName(location) ?? throw new IOException("У каталога базы нет родительского каталога.");
        var name = Path.GetFileName(location);
        var staging = Path.Combine(parent, "." + name + ".restoring-" + BackupRun.Random());
        CheckSet(plan);
        Stream source = plan.Files.Count > 1 ? new VolumeReadStream(plan.Files) : new FileStream(plan.Files[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using (source)
            using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8))
            {
                var entries = archive.Entries.Select(x => (Entry: x, Path: EntryPath(x.FullName))).ToList();
                // The one data file of the root, whatever its case (an archive made on Windows may write «1cv8.1cd»); it is unpacked
                // as 1Cv8.1CD, or on Linux it would lie next to the real data file, which would stay old (Codex full audit 24.10, P2-5).
                var data = entries.Where(x => x.Path.Length == 1 && x.Path[0].Equals(BackupRun.BaseFile, StringComparison.OrdinalIgnoreCase) && !x.Entry.FullName.EndsWith('/')).ToList();
                if (data.Count == 0) throw new InvalidDataException($"В архиве нет файла информационной базы {BackupRun.BaseFile} в корне.");
                if (data.Count > 1)
                    throw new InvalidDataException($"В архиве несколько файлов информационной базы с разным регистром имени ({string.Join(", ", data.Select(x => x.Entry.FullName))}): неясно, какой загружать. База не изменена.");
                entries = [.. entries.Select(x => ReferenceEquals(x.Entry, data[0].Entry) ? (x.Entry, new[] { BackupRun.BaseFile }) : x)];
                var names = entries.Where(x => !x.Entry.FullName.EndsWith('/')).Select(x => string.Join('/', x.Path)).ToList();
                if (names.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != names.Count)
                    throw new InvalidDataException("В архиве повторяются имена файлов.");
                var total = entries.Sum(x => x.Entry.Length);
                long done = 0;
                Directory.CreateDirectory(staging);
                var buffer = new byte[1 << 20];
                progress?.Invoke(new("Распаковка архива…", 0, total));
                foreach (var (entry, path) in entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    // A lock in an older archive is not restored, and the lock the base has now stays (Codex audit, part 2).
                    // Nor a lock file kept aside under a private name (it holds an access code; Codex re-check, 19.10).
                    if (path.Length == 1 && BackupLock.IsLockName(path[0])) continue;
                    var target = Path.Combine([staging, .. path]);
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var crc = 0xFFFFFFFFu;
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        for (int read; (read = input.Read(buffer)) > 0;)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
                            progress?.Invoke(new("Распаковка архива…", done += read, total));
                        }
                        output.Flush(true);
                    }
                    // A volume of another backup joined in gives other bytes than the archive's directory recorded (Grok audit).
                    if (~crc != entry.Crc32) throw new InvalidDataException($"Архив повреждён или тома от разных копий: контрольная сумма {entry.FullName} не совпадает.");
                    File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                }
            }
            cancel.ThrowIfCancellationRequested();
            Replace(plan, staging, location, parent, name, progress);
        }
        finally { RemoveFolder(staging); }
    }

    /// <summary>The volumes the journal recorded must be on the disk as they were written: same size and time, and the set as long as
    /// the record's. A set the journal does not know is checked by the archive itself (its directory and checksums) while unpacked.</summary>
    private static void CheckSet(RestorePlan plan)
    {
        if (plan.Request.Journal is not { } journal) return;
        IReadOnlyList<(string File, BackupFileStamp? Stamp)>? stamps;
        try { stamps = journal.StampsOf(plan.Files); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
        if (stamps is null || stamps.Count == 0) return;
        var mixed = stamps.Where(x => x.Stamp is null || !x.Stamp.Matches(x.File)).Select(x => Path.GetFileName(x.File)).ToList();
        if (mixed.Count > 0)
            throw new InvalidDataException($"Тома архива не совпадают с записью журнала выгрузок ({string.Join(", ", mixed)}): набор смешан с другой копией или изменён. Загрузка остановлена.");
    }

    /// <summary>The parts of an entry name inside the archive; a name that would leave the folder (absolute, a drive, «..») is refused.</summary>
    internal static string[] EntryPath(string fullName)
    {
        var name = fullName.Replace('\\', '/').TrimEnd('/');
        var parts = name.Split('/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || parts.Any(x => x.Length == 0 || x.Trim('.', ' ').Length == 0) ||
            parts.Any(x => x.IndexOfAny([.. Path.GetInvalidFileNameChars()]) >= 0))
            throw new InvalidDataException($"Недопустимое имя в архиве: {fullName}.");
        return parts;
    }

    // Every unpacked file replaces the file of the base with its name; a file of the base is set aside first. Files of the base
    // the archive does not have stay (logs, the folder of backups). Any failure puts everything back.
    private static void Replace(RestorePlan plan, string staging, string location, string parent, string name, Action<BackupProgress>? progress)
    {
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            // The data file goes last: until then the old base stays whole for anyone who looks (it is held, see below).
            .OrderBy(x => Path.GetRelativePath(staging, x).Equals(BackupRun.BaseFile, BackupRun.PathComparison) ? 1 : 0).ToList();
        // A link (symlink, junction) under the base would send a file of the archive somewhere else and remove what is there:
        // refused before anything moves (Codex audit, part 2).
        foreach (var file in files) CheckNoLink(location, Path.GetRelativePath(staging, file));
        // The last moment before the files change: 1C may have opened the base meanwhile. Linux asks /proc (it has no mandatory
        // locks: a process that opens the base after this check is not stopped); Windows holds the data file for the whole
        // replacement, so no session starts until it is done.
        plan.CheckOpen(location);
        var held = BackupRun.HoldBase(location, allowMove: true);
        var aside = Path.Combine(parent, "." + name + ".replaced-" + BackupRun.Random());
        var placed = new List<string>();
        var moved = new List<(string Original, string Aside)>();
        try
        {
            progress?.Invoke(new("Замена файлов базы…"));
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(staging, file);
                var target = Path.Combine(location, relative);
                if (Directory.Exists(target)) throw new IOException($"В каталоге базы на месте файла {relative} находится каталог.");
                if (File.Exists(target))
                {
                    var set = Path.Combine(aside, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(set)!);
                    try { File.Move(target, set); }
                    catch (IOException ex) when (BackupRun.SharingViolation(ex))
                    { throw new IOException($"Файл {relative} занят другой программой. Завершите все сеансы 1С с этой базой и повторите.", ex); }
                    moved.Add((target, set));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                placed.Add(target);
            }
        }
        catch (Exception reason)
        {
            held?.Dispose();
            var lost = new List<string>();
            foreach (var file in placed) BackupRun.TryDelete(file);
            foreach (var (original, set) in moved)
            {
                try { File.Move(set, original, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(set); }
            }
            if (lost.Count == 0) RemoveFolder(aside);
            throw lost.Count == 0 ? reason is IOException io ? io : new IOException(reason.Message, reason)
                : new IOException($"{reason.Message} Не все прежние файлы базы возвращены на место: они в каталоге {aside}.", reason);
        }
        held?.Dispose();
        RemoveFolder(aside);
    }

    /// <summary>Refuses a path under the base whose existing part — a folder on the way or the file itself — is a link or another
    /// reparse point: what lies behind it is outside the base.</summary>
    internal static void CheckNoLink(string location, string relative)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) return;
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"В каталоге базы {Path.GetRelativePath(location, current)} — ссылка на другое место. Загрузка остановлена, база не изменена.");
        }
    }

    private static void RemoveFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public enum BaseBusyState { Free, Busy, NotChecked }

/// <summary>CRC-32 (IEEE 802.3) as ZIP records it.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    })];

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

/// <param name="Detail">Who holds the files (Linux) or why the check was not made.</param>
public sealed record BaseBusy(BaseBusyState State, string Detail = "");

/// <summary>Whether a file base is in use: Linux asks /proc (<see cref="OpenFiles.Under"/>), Windows tries to open 1Cv8.1CD for itself
/// alone (the original removed 1Cv8.1CL to find out; nothing is removed here). A server base is not checked.</summary>
public static class BaseBusyCheck
{
    public const string FreeForDump = "Информационная база свободна и можно начать выполнение выгрузки";
    public const string BusyForDump = "В данный момент информационная база заблокирована и выгрузка данных невозможна.";
    public const string BusyForLoad = "Информационная база занята! Загрузка данных невозможна!";
    public const string NotCheckedForLoad = "Не удалось проверить, открыта ли информационная база в 1С. Загружать данные без проверки?";

    public static BaseBusy Check(BaseEntry entry, string procRoot = "/proc")
    {
        if (entry.Kind != ConnectionKind.File) return new(BaseBusyState.NotChecked);
        var location = entry.Location.Trim();
        if (!Directory.Exists(location)) return new(BaseBusyState.NotChecked, $"Каталог базы не найден: {location}.");
        if (OperatingSystem.IsLinux())
        {
            try { return OpenFiles.Under(location, procRoot) is { Count: > 0 } holders ? new(BaseBusyState.Busy, OpenFiles.Describe(holders)) : new(BaseBusyState.Free); }
            catch (IOException ex) { return new(BaseBusyState.NotChecked, ex.Message); }
        }
        if (!OperatingSystem.IsWindows()) return new(BaseBusyState.NotChecked);
        var data = Path.Combine(location, BackupRun.BaseFile);
        if (!File.Exists(data)) return new(BaseBusyState.Free);
        try
        {
            using var probe = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.None, 1);
            return new(BaseBusyState.Free);
        }
        catch (IOException ex) when (BackupRun.SharingViolation(ex)) { return new(BaseBusyState.Busy, $"Файл {BackupRun.BaseFile} открыт другой программой."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(BaseBusyState.NotChecked, ex.Message); }
    }
}

/// <summary>The last user of a base as 1C remembers it: def.usr in the folder of the base's settings («{"Имя"}» in UTF-8 with a BOM;
/// the original reads the first line, drops the braces, then the quotes — _Unit186.pas, sub_008477D4).</summary>
public static class LastUser
{
    /// <summary>Where 1C keeps the settings of bases: Windows %APPDATA%\1C\1cv8 (and 1Cv82 of the old platform, as the original
    /// looks there too), Linux ~/.1cv8/1C/1cv8.</summary>
    public static IReadOnlyList<string> Roots()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } data
                ? [Path.Combine(data, "1C", "1cv8"), Path.Combine(data, "1C", "1Cv82")] : [];
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? [Path.Combine(home, ".1cv8", "1C", "1cv8")] : [];
    }

    /// <summary>The name from the first def.usr found for the base; null when there is none or it holds no name.</summary>
    public static string? Read(Guid baseId, IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? Roots())
        {
            var path = Path.Combine(root, baseId.ToString("D"), "def.usr");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 65536) continue;
                using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                if (Parse(reader.ReadToEnd()) is { } user) return user;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The name in the text of def.usr: the first non-empty line without its braces; a quoted value (with "" for a quote)
    /// or, without quotes, the text up to a comma.</summary>
    public static string? Parse(string text)
    {
        var line = text.TrimStart('﻿').Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        if (line is null) return null;
        if (line.StartsWith('{')) line = line[1..].TrimEnd().TrimEnd('}').Trim();
        string name;
        if (line.StartsWith('"'))
        {
            var value = new StringBuilder();
            for (var i = 1; i < line.Length; i++)
            {
                if (line[i] != '"') { value.Append(line[i]); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; continue; }
                break;
            }
            name = value.ToString();
        }
        else name = line.Split(',')[0];
        name = name.Trim();
        return name.Length == 0 || name.Any(char.IsControl) ? null : name;
    }
}
