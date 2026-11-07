using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

/// <summary>A file of a backup as it was written: its size and time. A restore compares the volumes on the disk with them, so a set
/// that mixes two backups of the same name is refused (Grok audit, part 2).</summary>
public sealed record BackupFileStamp
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime Written { get; init; }

    public static BackupFileStamp Of(string path)
    {
        var info = new FileInfo(path);
        return new() { Path = path, Size = info.Length, Written = info.LastWriteTimeUtc };
    }

    /// <summary>Whether the file on the disk is still the one written.</summary>
    public bool Matches(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == Size && info.LastWriteTimeUtc == Written.ToUniversalTime();
    }
}

/// <summary>One backup made by this program: the base, the scenario, the format, when, every file (the copies in the folders 2–3
/// and every volume) and the size of one copy. The original's backup.lst is not imported.</summary>
public sealed record BackupRecord
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BaseId { get; init; }
    public Guid ScenarioId { get; init; }
    public BackupFormat Format { get; init; }
    public DateTimeOffset Time { get; init; }
    public List<string> Files { get; init; } = [];
    /// <summary>The bytes of one copy (all its volumes).</summary>
    public long Size { get; init; }
    /// <summary>The size and time of every file as written; empty in a record of an older build.</summary>
    public List<BackupFileStamp> Stamps { get; init; } = [];

    internal void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidDataException("У записи журнала выгрузок нет идентификатора.");
        if (Files is null || Files.Any(x => string.IsNullOrWhiteSpace(x) || x.Any(char.IsControl)))
            throw new InvalidDataException("Повреждён список файлов в журнале выгрузок.");
        if (!Enum.IsDefined(Format) || Size < 0 || Stamps is null || Stamps.Any(x => x is null || x.Path is null))
            throw new InvalidDataException("Повреждена запись журнала выгрузок.");
    }
}

/// <summary>The journal of backups («Список выгрузок»): the records in the order they were made.</summary>
public sealed record BackupJournal
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public int Version { get; init; } = 1;
    public List<BackupRecord> Records { get; init; } = [];

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия журнала выгрузок.");
        if (Records is null || Records.Any(x => x is null)) throw new InvalidDataException("Повреждён журнал выгрузок.");
        foreach (var record in Records) record.Validate();
        if (Records.Select(x => x.Id).Distinct().Count() != Records.Count) throw new InvalidDataException("Повторяющиеся записи в журнале выгрузок.");
    }

    /// <summary>The records of a base, the newest first (the list of the window).</summary>
    public IEnumerable<BackupRecord> Of(Guid baseId) => Records.Where(x => x.BaseId == baseId).Select((x, i) => (x, i))
        .OrderByDescending(x => x.x.Time).ThenByDescending(x => x.i).Select(x => x.x);
}

/// <summary>The journal of backups of a profile: «backup-journal.json» next to the settings. Every change reads the file under its
/// lock, applies itself and writes it back, so two windows that add records at once both keep theirs. Unknown fields stay; a damaged
/// file is never overwritten (the previous text stays in «.bak»).</summary>
public sealed class BackupJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public BackupJournalStore(string settingsPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? throw new ArgumentException("Не задан каталог настроек.");
        PathName = Path.Combine(folder, "backup-journal.json");
    }

    public string PathName { get; }

    public BackupJournal Load() => File.Exists(PathName) ? Parse(File.ReadAllBytes(PathName)) : new BackupJournal();

    private static BackupJournal Parse(byte[] bytes)
    {
        BackupJournal result;
        try { result = JsonSerializer.Deserialize<BackupJournal>(bytes, Options) ?? throw new InvalidDataException("Журнал выгрузок пуст."); }
        catch (JsonException ex) { throw new InvalidDataException("Журнал выгрузок содержит некорректный JSON или неизвестные значения.", ex); }
        result.Validate();
        return result;
    }

    // Read, change and write under the lock of the file; the change returns the new journal (null: nothing to write).
    // afterWrite runs still under the lock, after the journal is on the disk: the removal of files is serialized with every other
    // change of the journal, so no other window can register a path between the write and the removal (Codex second recheck).
    private T Update<T>(Func<BackupJournal, (BackupJournal? Next, T Result)> change, Func<T, T>? afterWrite = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var guard = holding.Value ? null : FilePersistence.Lock(PathName);
        var current = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
        var journal = current is null ? new BackupJournal() : Parse(current);
        var (next, result) = change(journal);
        if (next is null) return result;
        next.Validate();
        var written = JsonSerializer.SerializeToUtf8Bytes(next, Options);
        // Durable: files are removed after this write, so the new journal reaches the disk first (Grok audit, Codex recheck).
        FilePersistence.Replace(FilePersistence.Target(PathName), written, current is null ? null : PathName,
            () => { if (current is not null) FilePersistence.Replace(PathName + ".bak", current, PathName); }, durable: true);
        return afterWrite is null ? result : afterWrite(result);
    }

    /// <summary>Called under the lock right before files are removed (the tests use it to act as another window).</summary>
    internal Action? BeforeErase { get; set; }
    /// <summary>Called under the lock for each file after its stamp is checked, right before it is removed (the tests).</summary>
    internal Action<string>? BeforeDelete { get; set; }

    // This thread holds the lock of the journal (inside Locked): the changes made there do not take it again.
    private readonly ThreadLocal<bool> holding = new();

    /// <summary>Runs <paramref name="action"/> holding the lock of the journal — another window can neither change the journal nor
    /// remove its files meanwhile; <see cref="Add"/> and <see cref="Rotate"/> called inside use the same lock. About 1.5 s of
    /// waiting for another window, then <see cref="IOException"/>.</summary>
    internal void Locked(Action action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var guard = FilePersistence.Lock(PathName);
        holding.Value = true;
        try { action(); }
        finally { holding.Value = false; }
    }

    /// <summary>One file however it is written: the full path («.», «..» and separators resolved) with every existing link in it
    /// followed to its target, compared with the case rule of the system (Codex audit, part 2).</summary>
    internal static string Identity(string path)
    {
        try { return LongName(BaseDuplicate.Physical(Path.GetFullPath(path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception inner) when (inner is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }

    /// <summary>The stamps of the record that names every file of <paramref name="files"/> (by identity), each with its file; null
    /// when no record names the first file. A record of an older build without stamps gives an empty list.</summary>
    public IReadOnlyList<(string File, BackupFileStamp? Stamp)>? StampsOf(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return null;
        var first = Identity(files[0]);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var record = Load().Records.LastOrDefault(x => x.Files.Any(f => comparer.Equals(Identity(f), first)));
        if (record is null) return null;
        if (record.Stamps.Count == 0) return [];
        return [.. files.Select(f => (f, record.Stamps.FirstOrDefault(x => comparer.Equals(Identity(x.Path), Identity(f)))))];
    }

    /// <summary>Windows: the 8.3 short names of a path («LONGFO~1») expanded to the long ones, for the part of the path that exists
    /// (Codex recheck, part 2). The path as it is elsewhere and when the system cannot tell.</summary>
    internal static string LongName(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~')) return path;
        var existing = path;
        var rest = "";
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) return path;
            rest = Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetLongPathName(existing, buffer, buffer.Capacity);
        if (length > buffer.Capacity) { buffer.Capacity = (int)length; length = GetLongPathName(existing, buffer, buffer.Capacity); }
        if (length == 0 || length > buffer.Capacity) return path;
        return Path.TrimEndingDirectorySeparator(rest.Length == 0 ? buffer.ToString() : Path.Combine(buffer.ToString(), rest.TrimEnd(Path.DirectorySeparatorChar)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, int size);

    private static HashSet<string> Identities(IEnumerable<string> paths) =>
        paths.Select(Identity).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Adds a record. A file it names leaves any older record (a backup replaced under the same name): the rotation of the
    /// older one must never remove the new file. An older record left without files goes.</summary>
    public void Add(BackupRecord record)
    {
        record.Validate();
        var added = Identities(record.Files);
        Update(journal =>
        {
            var records = journal.Records.Select(x => x.Files.Any(f => added.Contains(Identity(f)))
                ? x with { Files = [.. x.Files.Where(f => !added.Contains(Identity(f)))] } : x)
                .Where(x => x.Files.Count > 0).ToList();
            records.Add(record);
            return (journal with { Records = records }, 0);
        });
    }

    /// <summary>«Хранить копий»: the records of this base and scenario beyond the newest <paramref name="keep"/> lose their files and
    /// go. Only files of the journal are removed, in every folder; a file that is gone just leaves its record, a file whose folder is
    /// not reachable (a network share that is off) and a file that cannot be removed stay with their record and are reported.</summary>
    public IReadOnlyList<string> Rotate(Guid baseId, Guid scenarioId, int keep)
    {
        if (keep <= 0) return [];
        var (_, warnings) = Update(journal =>
        {
            var ordered = journal.Records.Select((x, i) => (Record: x, Index: i)).Where(x => x.Record.BaseId == baseId && x.Record.ScenarioId == scenarioId)
                .OrderByDescending(x => x.Record.Time).ThenByDescending(x => x.Index).Select(x => x.Record).ToList();
            var old = ordered.Skip(keep).Select(x => x.Id).ToHashSet();
            if (old.Count == 0) return ((BackupJournal?)null, (Doomed: new List<(string, BackupFileStamp?)>(), Warnings: new List<string>()));
            var kept = Identities(journal.Records.Where(x => !old.Contains(x.Id)).SelectMany(x => x.Files));
            var warnings = new List<string>();
            var doomed = new List<(string, BackupFileStamp?)>();
            var records = new List<BackupRecord>();
            foreach (var record in journal.Records)
            {
                if (!old.Contains(record.Id)) { records.Add(record); continue; }
                var left = Select(record, kept, keepUnreachable: true, doomed, warnings);
                if (left.Count > 0) records.Add(record with { Files = left });
            }
            return (journal with { Records = records }, (Doomed: doomed, Warnings: warnings));
        }, x => { BeforeErase?.Invoke(); x.Warnings.AddRange(Erase(x.Doomed, BeforeDelete)); return x; });
        return warnings;
    }

    /// <summary>«Удалить файл архива?»: the files of one record and the record. The journal is written first, then the files are
    /// removed: a file that cannot be removed is reported by name and is no longer in the journal.</summary>
    public IReadOnlyList<string> Delete(Guid recordId)
    {
        var (_, warnings) = Update(journal =>
        {
            var record = journal.Records.FirstOrDefault(x => x.Id == recordId);
            if (record is null) return ((BackupJournal?)null, (Doomed: new List<(string, BackupFileStamp?)>(), Warnings: new List<string>()));
            var warnings = new List<string>();
            var doomed = new List<(string, BackupFileStamp?)>();
            var kept = Identities(journal.Records.Where(x => x.Id != recordId).SelectMany(x => x.Files));
            var left = Select(record, kept, keepUnreachable: false, doomed, warnings);
            var records = journal.Records.Select(x => x.Id == recordId ? x with { Files = left } : x).Where(x => x.Files.Count > 0).ToList();
            return (journal with { Records = records }, (Doomed: doomed, Warnings: warnings));
        }, x => { BeforeErase?.Invoke(); x.Warnings.AddRange(Erase(x.Doomed, BeforeDelete)); return x; });
        return warnings;
    }

    // Chooses what goes: the files to remove leave the record (they are removed only after the journal is on the disk, so a crash
    // never leaves the journal naming a removed file — Codex recheck); what cannot be removed stays and is reported. A file another
    // record also names is only taken off this one.
    private static List<string> Select(BackupRecord record, HashSet<string> kept, bool keepUnreachable, List<(string, BackupFileStamp?)> doomed, List<string> warnings)
    {
        var left = new List<string>();
        foreach (var file in record.Files)
        {
            if (kept.Contains(Identity(file))) continue;
            if (!Path.IsPathFullyQualified(file)) { warnings.Add($"Файл не удалён (путь не полный): {file}."); left.Add(file); continue; }
            if (Directory.Exists(file)) { warnings.Add($"Не удалён: {file} — это каталог, а не файл."); left.Add(file); continue; }
            if (!File.Exists(file) && keepUnreachable && Path.GetDirectoryName(file) is { } folder && !Directory.Exists(folder))
            { warnings.Add($"Каталог недоступен, файл не удалён: {file}."); left.Add(file); continue; }
            var identity = Identity(file);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            doomed.Add((file, record.Stamps.FirstOrDefault(x => comparer.Equals(Identity(x.Path), identity))));
        }
        return left;
    }

    // Removes the files already taken out of the journal; a file that stays is named (it is no longer listed anywhere).
    private static List<string> Erase(List<(string File, BackupFileStamp? Stamp)> files, Action<string>? beforeDelete)
    {
        var warnings = new List<string>();
        foreach (var (file, stamp) in files)
        {
            try
            {
                if (!File.Exists(file)) continue;
                // A file that is no longer the one this record wrote (a backup of the same name made since) is never removed.
                if (stamp is not null && !stamp.Matches(file))
                { warnings.Add($"Файл не удалён: {file} заменён более новой копией; он больше не числится в этой записи журнала."); continue; }
                beforeDelete?.Invoke(file);
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"Файл не удалён и больше не числится в журнале: {file} ({ex.Message}) Удалите его вручную."); }
        }
        return warnings;
    }
}
