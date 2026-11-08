using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StartManager.Core;

/// <summary>The list carries the marker of a write that never finished (a crash, a power or network failure in the middle of
/// it): the file may hold part of the new text over the old one. Nothing is read from it until the user restores the backup
/// (<see cref="V8iStore.RecoverInterruptedWrite"/>) or keeps the file as it is (<see cref="V8iStore.DismissInterruptedWrite"/>).</summary>
public sealed class InterruptedWriteException(string path, byte[] current, bool backupMatches) : IOException(
    "Найдена отметка незавершённой записи списка (сбой программы, питания или сети): файл может быть записан не полностью. " +
    (backupMatches ? "Резервная копия текста до записи цела." : "Резервная копия текста до записи не найдена или изменена."))
{
    public string PathName { get; } = path;
    /// <summary>The file as it was seen: recovery replaces it only while it is still the same.</summary>
    public byte[] Current { get; } = current;
    /// <summary>The .bak is the text from before the interrupted write.</summary>
    public bool BackupMatches { get; } = backupMatches;
}

/// <summary>The list on disk is no longer the one this store loaded: another program or window changed it.</summary>
public sealed class ListChangedException() : IOException("Список изменён другой программой. Нажмите «Перечитать список баз» и повторите изменение.");

/// <summary>The same bases or groups were changed both here and by another program: the change is not applied over theirs.</summary>
public sealed class ListConflictException(IReadOnlyList<string> names) : IOException(
    "Эти записи изменены и здесь, и другой программой: " + string.Join(", ", names.Take(5).Select(x => "«" + x + "»")) +
    (names.Count > 5 ? $" и ещё {names.Count - 5}" : "") + ". Проверьте записи и повторите изменение.")
{
    public IReadOnlyList<string> Names { get; } = names;
}

/// <summary>The list was opened read-only: this change would rewrite the file. Local settings of the bases are still saved.</summary>
public sealed class ListReadOnlyException(string reason, string? effect = null) : IOException(
    $"Список открыт только для чтения: {reason}. " + (effect ?? "Добавление, удаление, названия, подключения и группы баз не изменяются.") +
    " Снимите ограничение и нажмите «Перечитать список баз».")
{
    public const string RecoveryEffect = "Восстановить список из .bak или снять отметку прерванной записи нельзя.";
    public string Reason { get; } = reason;
}

/// <summary>Whether a list can be written: the file itself and its folder, where the .bak, the write marker and their
/// temporary files are made. Only a lasting refusal counts; a busy file or a full disk is reported by the save itself.</summary>
public static class ListAccess
{
    /// <summary>The same file behind its names: links and junctions on the way are resolved; an unreadable path is compared as written.</summary>
    public static bool SameFile(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Physical(string path)
        {
            var full = Path.GetFullPath(path);
            try { return FilePersistence.Target(full); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return full; }
        }
        return string.Equals(Physical(first), Physical(second), comparison);
    }

    public static string? ReadOnlyReason(string path)
    {
        var full = Path.GetFullPath(path);
        // .bak goes next to the name the list was opened by, the write marker next to the file behind its links.
        var physical = full;
        try { physical = FilePersistence.Target(full); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Every check runs on its own: a busy file says nothing about the folders, and the other way round.
        if (FileReason(full) is { } file) return file;
        if (OperatingSystem.IsWindows())
        {
            if (ReplaceReason(full + ".bak", "копии .bak") is { } backup) return backup;
            if (ReplaceReason(physical + ".writing", "отметки записи .writing") is { } marker) return marker;
        }
        if (FolderReason(full, "нет прав на запись в папку списка (там создаётся копия .bak)") is { } folder) return folder;
        if (!string.Equals(Path.GetDirectoryName(physical), Path.GetDirectoryName(full),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return FolderReason(physical, "нет прав на запись в папку файла, на который ссылается список (там создаётся отметка записи)");
        return null;
    }

    private static string? FileReason(string path)
    {
        try
        {
            // On Linux the attribute reflects the permissions; the open below names that case.
            if (OperatingSystem.IsWindows() && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                return "у файла атрибут «Только для чтения»";
            // Opened for writing and closed at once: nothing is written, other readers and writers are not blocked.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        }
        catch (UnauthorizedAccessException) { return "нет прав на запись в файл"; }
        catch (IOException ex) when (ReadOnlyMedia(ex)) { return MediaReason; }
        catch (IOException) { }
        return null;
    }

    // Windows replaces an existing .bak or marker by name (MoveFileEx): a protected one refuses it. On Linux the folder decides.
    private static string? ReplaceReason(string existing, string name)
    {
        try
        {
            if (!File.Exists(existing)) return null;
            if (File.GetAttributes(existing).HasFlag(FileAttributes.ReadOnly)) return $"у {name} атрибут «Только для чтения»";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        // The right that the replacement needs, not a write into the old copy; a busy file is no refusal.
        return FilePersistence.ReplaceAccess(existing) switch { 5 => $"нет прав на замену {name}", 19 => MediaReason, _ => null };
    }

    private static string? FolderReason(string besides, string denied)
    {
        var probe = besides + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { } }
        catch (UnauthorizedAccessException) { return denied; }
        catch (IOException ex) when (ReadOnlyMedia(ex)) { return MediaReason; }
        catch (IOException) { }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return null;
    }

    private const string MediaReason = "диск или сетевая папка без записи";
    // ERROR_WRITE_PROTECT on Windows, EROFS on Linux.
    private static bool ReadOnlyMedia(IOException ex) => OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) == 19 : ex.HResult == 30;
}

// Retains unrecognised records and properties, including connection-string options.
public sealed class V8iStore(string path)
{
    private sealed class Section(string name, List<string> lines)
    {
        public string Name = name;
        public string Header = "[" + name + "]";
        private readonly string originalName = name;
        public string RenderHeader => Name == originalName ? Header : "[" + Name + "]";
        public List<string> Lines = lines;
        public string? Get(string key) => Lines.LastOrDefault(x => IsKey(x, key))?.Split('=', 2)[1].Trim();
        private static bool IsKey(string line, string key) => line.IndexOf('=') is var i && i > 0 && line[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
        public void Set(string key, string value)
        {
            var index = Lines.FindLastIndex(x => IsKey(x, key));
            if (index < 0) Lines.Insert(0, key + "=" + value);
            else Lines[index] = Lines[index][..(Lines[index].IndexOf('=') + 1)] + value;
        }
        public void Remove(string key) => Lines.RemoveAll(x => IsKey(x, key));
        public Section Copy() => new(Name, [.. Lines]) { Header = RenderHeader };
    }
    /// <summary>User list of the 1C:Enterprise launcher (1CEStart) for this OS.</summary>
    public static string StandardPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart", "ibases.v8i")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".1C", "1cestart", "ibases.v8i");
    private byte[]? original;
    private Encoding encoding = new UTF8Encoding(false, true);
    private byte[] preamble = [];
    private string newline = "\n";
    private string normalizedOriginal = "";
    private List<string> prefix = [];
    private List<Section> sections = [];
    private Dictionary<Guid, Section> entries = [];
    private Dictionary<string, Section> folders = new(StringComparer.OrdinalIgnoreCase);
    private Library loaded = new();
    /// <summary>The folders of the groups as the user had them before this save, when the save goes over another program's change
    /// through a freshly read store (<see cref="SaveOverExternalChange"/>); otherwise those of <see cref="loaded"/>.</summary>
    private IReadOnlyList<GroupFolder>? folderBaseline;
    public string PathName => Path.GetFullPath(path);
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    /// <summary>Why the loaded list cannot be written, or null. Checked on every load; a changed right needs a new load.</summary>
    public string? ReadOnly { get; private set; }
    /// <summary>Both the write and its rollback failed: the file is not known any more, nothing is saved until a new load.</summary>
    public bool Lost { get; private set; }
    public bool HasPersistentId(Guid id) => entries.TryGetValue(id, out var section) && Guid.TryParse(section.Get("ID"), out _);

    public Library Load(Library settings)
    {
        original = null; Lost = false;
        var bytes = File.ReadAllBytes(path);
        MarkerNotice = CheckInterrupted(path, bytes);
        var result = Parse(bytes, settings);
        original = bytes;
        ReadOnly = ListAccess.ReadOnlyReason(path);
        return result;
    }

    /// <summary>The bases and groups of a list text held in memory (a snapshot), read as the list file would be. No file is
    /// written: a list may carry ProxyPassword= or other secrets of the connection (Codex full audit 24.10, P1-2).</summary>
    public static Library Read(byte[] content) => Read(content, out _);

    public static Library Read(byte[] content, out IReadOnlyList<string> warnings)
    {
        var reader = new V8iStore(Path.Combine(Path.GetTempPath(), "snapshot.v8i"));
        var result = reader.Parse(content, new());
        warnings = reader.Warnings;
        return result;
    }

    private Library Parse(byte[] bytes, Library settings)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        preamble = [];
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); preamble = [0xEF, 0xBB, 0xBF]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); preamble = [0xFF, 0xFE]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); preamble = [0xFE, 0xFF]; }
        else encoding = new UTF8Encoding(false, true);
        string text;
        try { text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length); }
        catch (DecoderFallbackException) when (preamble.Length == 0)
        {
            encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
        }
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        normalizedOriginal = string.Join(newline, System.Text.RegularExpressions.Regex.Split(text, "\r\n|\n|\r"));
        prefix = []; sections = []; entries = []; folders = new(StringComparer.OrdinalIgnoreCase);
        Section? current = null;
        foreach (var line in System.Text.RegularExpressions.Regex.Split(text, "\r\n|\n|\r"))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            { current = new(trimmed[1..^1], []) { Header = line }; sections.Add(current); }
            else (current?.Lines ?? prefix).Add(line);
        }
        var bases = new List<BaseEntry>();
        var warnings = new List<string>();
        var preservedGroups = new List<string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var imported = V8iImporter.Import(new Library(), "[" + section.Name + "]\n" + string.Join("\n", section.Lines));
            if (imported.Warnings.Count > 0)
            {
                warnings.AddRange(imported.Warnings);
                var group = Groups.Normalize(section.Get("Folder") ?? "");
                if (group.Length > 0) preservedGroups.Add(group);
                continue;
            }
            if (imported.Library.Bases.FirstOrDefault() is { } entry)
            {
                var identity = section.Name + "\0" + section.Get("Folder") + "\0" + section.Get("Connect");
                occurrences.TryGetValue(identity, out var occurrence); occurrences[identity] = occurrence + 1;
                var id = Guid.TryParse(section.Get("ID"), out var parsed) ? parsed : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\0" + occurrence)).AsSpan(0, 16));
                if (entries.TryGetValue(id, out var conflict)) throw new InvalidDataException($"Повторяющийся ID у записей «{conflict.Name}» и «{section.Name}». Исправьте конфликт в копии списка.");
                var preferences = settings.Bases.FirstOrDefault(x => x.Id == id);
                bases.Add(entry with { Id = id, Executable = preferences?.Executable ?? "", PlatformRule = preferences?.PlatformRule,
                    Comment = preferences?.Comment ?? "", Configuration = preferences?.Configuration ?? "",
                    ConfigurationRelease = preferences?.ConfigurationRelease ?? "", ConfigurationId = preferences?.ConfigurationId ?? "", AdditionalParameters = preferences?.AdditionalParameters,
                    ListParameters = section.Get(ParametersKey) ?? "",
                    ExternalProcessing = preferences?.ExternalProcessing ?? "", Authentication = preferences?.Authentication,
                    DisallowDelete = preferences?.DisallowDelete ?? false, DoubleClick = preferences?.DoubleClick, RunAsAdmin = preferences?.RunAsAdmin ?? false, CheckServerAgent = preferences?.CheckServerAgent ?? false, ManagedApplication = preferences?.ManagedApplication ?? false,
                    BackupScriptId = preferences?.BackupScriptId, BackupFolder = preferences?.BackupFolder ?? "", BackupPrefix = preferences?.BackupPrefix ?? "",
                    Extra = preferences?.Extra }); entries.Add(id, section);
            }
            else if (imported.Library.Groups.FirstOrDefault() is { } group)
            {
                if (!folders.TryAdd(group, section)) throw new InvalidDataException($"Повторяющаяся группа «{group}» в списке .v8i.");
            }
        }
        loaded = settings with { ListPath = PathName, Bases = bases, Groups = folders.Keys.Concat(preservedGroups).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        // The folders of the groups (local settings) follow the group records as they are in the file now.
        loaded = loaded with { GroupFolders = Groups.Reconcile(settings.GroupFolders ?? [], Records(folders), Groups.All(loaded)) };
        LibraryStore.Validate(loaded);
        Warnings = warnings;
        return loaded;
    }

    /// <param name="copies">New bases that copy the record of another one: new ID → source ID. The copy keeps every line
    /// of the source record, gets its own name, ID and connection and stands right after the source.</param>
    /// <param name="groupMap">Several groups renamed at once (the formatting of names): the old full path of each group to its
    /// new one. Used instead of <paramref name="renamedFrom"/>/<paramref name="renamedTo"/>; the records keep their ID and fields.</param>
    public Library Save(Library next, string? renamedFrom = null, string? renamedTo = null, Guid? ensureId = null,
        IReadOnlyDictionary<Guid, Guid>? copies = null, IReadOnlyDictionary<string, string>? groupMap = null)
    {
        if (original is null) throw new InvalidOperationException("Сначала откройте список .v8i.");
        LibraryStore.Validate(next);
        foreach (var entry in next.Bases) CheckName(entry.Name);
        foreach (var group in Groups.All(next)) foreach (var part in group.Split('/')) CheckName(part);
        string Map(string group) => groupMap is not null ? groupMap.GetValueOrDefault(group, group)
            : renamedFrom is not null && renamedTo is not null && Groups.Contains(renamedFrom, group) ? renamedTo + group[renamedFrom.Length..] : group;
        var result = new List<Section>();
        var resultEntries = new Dictionary<Guid, Section>();
        var resultFolders = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
        var pending = next.Bases.ToDictionary(x => x.Id);
        var remainingGroups = new HashSet<string>(Groups.All(next), StringComparer.OrdinalIgnoreCase);
        var clones = (copies ?? new Dictionary<Guid, Guid>()).Where(x => pending.ContainsKey(x.Key) && entries.ContainsKey(x.Value) && !loaded.Bases.Any(b => b.Id == x.Key))
            .ToLookup(x => x.Value, x => x.Key);
        foreach (var section in sections)
        {
            var known = entries.FirstOrDefault(x => ReferenceEquals(x.Value, section));
            if (known.Value is not null)
            {
                if (pending.Remove(known.Key, out var entry))
                {
                    var copy = Update(section.Copy(), entry, loaded.Bases.Single(x => x.Id == entry.Id));
                    result.Add(copy); resultEntries.Add(entry.Id, copy);
                }
                foreach (var cloneId in clones[known.Key])
                {
                    pending.Remove(cloneId, out var clone);
                    var cloned = Update(new Section(clone!.Name, [.. section.Lines]), clone, loaded.Bases.Single(x => x.Id == known.Key));
                    cloned.Set("ID", clone.Id.ToString());
                    result.Add(cloned); resultEntries.Add(clone.Id, cloned);
                }
                continue;
            }
            var folder = folders.FirstOrDefault(x => ReferenceEquals(x.Value, section));
            if (folder.Value is not null)
            {
                var mapped = Map(folder.Key);
                if (remainingGroups.Remove(mapped))
                {
                    var copy = section.Copy();
                    if (mapped != folder.Key) { copy.Name = mapped.Split('/')[^1]; copy.Set("Folder", "/" + Groups.Parent(mapped)); }
                    result.Add(copy); resultFolders.Add(mapped, copy);
                }
                continue;
            }
            // Unsupported connections stay byte-for-byte in their original group.
            var unknownGroup = Groups.Normalize(section.Get("Folder") ?? "");
            if (unknownGroup.Length > 0 && (!Groups.All(next).Contains(unknownGroup, StringComparer.OrdinalIgnoreCase) || Map(unknownGroup) != unknownGroup))
                throw new InvalidOperationException($"Группа содержит неподдерживаемое подключение «{section.Name}». Измените её в стандартном списке 1С.");
            result.Add(section.Copy());
        }
        // A group known only from the Folder= of its bases gets its own record when it has a folder (Grok audit 13.10): the record's
        // ID keeps the folder with the group when another program renames it. A read-only list is not changed for that.
        // Only a folder set or changed by this save: an unchanged one does not make an ordinary save rewrite the list (Codex re-check).
        var baseline = folderBaseline ?? loaded.GroupFolders; folderBaseline = null;
        var unkeyed = new HashSet<string>(ReadOnly is null ? next.GroupFolders.Where(x => x.Id is null && !baseline.Any(was =>
            Groups.Normalize(was.Group).Equals(Groups.Normalize(x.Group), StringComparison.OrdinalIgnoreCase) && was.Folder == x.Folder))
            .Select(x => Groups.Normalize(x.Group)) : [], StringComparer.OrdinalIgnoreCase);
        foreach (var group in remainingGroups)
        {
            if (Groups.All(loaded).Contains(group, StringComparer.OrdinalIgnoreCase) && !folders.ContainsKey(group) && !unkeyed.Contains(group)) continue;
            var section = new Section(group.Split('/')[^1], []);
            section.Set("ID", Guid.NewGuid().ToString()); section.Set("Folder", "/" + Groups.Parent(group)); result.Add(section); resultFolders.Add(group, section);
        }
        foreach (var entry in pending.Values)
        {
            var section = Update(new(entry.Name, []), entry, null); result.Add(section); resultEntries.Add(entry.Id, section);
        }
        if (ensureId is { } target)
        {
            if (!resultEntries.TryGetValue(target, out var section)) throw new InvalidOperationException("База для закрепления ID не найдена.");
            // A read-only list keeps the record as it is: the base is known by its computed ID, local settings follow it.
            if (!Guid.TryParse(section.Get("ID"), out _) && ReadOnly is null) section.Set("ID", target.ToString());
        }
        string Render() => string.Join(newline, prefix.Concat(result.SelectMany(section => new[] { section.RenderHeader }.Concat(section.Lines))));
        var rendered = Render();
        var changed = rendered != normalizedOriginal;
        if (changed)
        {
            // Materialise all missing IDs together, before any deletion/reordering can
            // change identity. Local client settings alone never modify the shared file.
            foreach (var pair in resultEntries)
                if (!Guid.TryParse(pair.Value.Get("ID"), out _)) pair.Value.Set("ID", pair.Key.ToString());
            rendered = Render();
        }
        byte[] output;
        try { output = changed ? preamble.Concat(encoding.GetBytes(rendered)).ToArray() : original; }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Название содержит символы, недоступные в кодировке этого списка. Используйте другое название или сохраните список в UTF-8.", ex); }
        if (changed && ReadOnly is { } reason) throw new ListReadOnlyException(reason);
        WriteInPlace(changed ? output : null);
        // Adopt the already validated result; no fallible read after a successful write.
        sections = result; entries = resultEntries; folders = resultFolders;
        original = output; normalizedOriginal = rendered;
        loaded = next with { ListPath = PathName, Groups = Groups.All(next) };
        // A group record made by this save gets its ID now: its folder takes it, and follows a group renamed in the file.
        loaded = loaded with { GroupFolders = Groups.Reconcile(next.GroupFolders, Records(resultFolders), loaded.Groups) };
        return loaded;
    }

    private static Dictionary<string, Guid?> Records(Dictionary<string, Section> groups) => groups.ToDictionary(x => x.Key,
        x => Guid.TryParse(x.Value.Get("ID"), out var id) ? id : (Guid?)null, StringComparer.OrdinalIgnoreCase);

    /// <summary>The save after <see cref="ListChangedException"/>: the list is read again and the change made here is put over
    /// it when it touches other records than the other program did. A base or group counts as touched by the other program
    /// when the text of its record changed in any way, unknown lines included; touched here when it was added, removed or
    /// got another name, group, connection, version or launch parameters (or is the source of a copy, or gets its ID). Anything touched on both sides
    /// stops the save with <see cref="ListConflictException"/>, and nothing is written. Returns the store of the list as
    /// it is now and the library saved into it; the caller replaces its store with that one. <paramref name="required"/> are
    /// the bases the change is about although it may be only local (a version rule): removed there, they stop the save too.</summary>
    public (V8iStore Store, Library Saved) SaveOverExternalChange(Library next, string? renamedFrom = null, string? renamedTo = null,
        Guid? ensureId = null, IReadOnlyDictionary<Guid, Guid>? copies = null, IReadOnlyDictionary<string, string>? groupMap = null,
        IReadOnlyCollection<Guid>? required = null)
    {
        if (original is null) throw new InvalidOperationException("Сначала откройте список .v8i.");
        var fresh = new V8iStore(path);
        // The local settings of every base come from the change itself, as on any load.
        var current = fresh.Load(next);
        // The last record of a file ending in a line break carries one empty line more; it moves when they add a record at
        // the end, which is no change of the record itself. Empty lines at the end of a record are not compared.
        static string Text(Section section)
        {
            var count = section.Lines.Count;
            while (count > 0 && section.Lines[count - 1].Length == 0) count--;
            return section.RenderHeader + "\n" + string.Join("\n", section.Lines.Take(count));
        }
        static bool Same(BaseEntry a, BaseEntry b) => a.Name == b.Name && a.Group == b.Group && a.Kind == b.Kind && a.Location == b.Location &&
            a.AskCredentials == b.AskCredentials && a.WebServerAskCredentials == b.WebServerAskCredentials && a.PlatformVersion == b.PlatformVersion &&
            a.ClientType == b.ClientType && a.ConnectionSpeed == b.ConnectionSpeed && a.UseProxy == b.UseProxy && a.ProxyServer == b.ProxyServer &&
            a.ProxyPort == b.ProxyPort && a.ProxyUser == b.ProxyUser && a.ListParameters.Trim() == b.ListParameters.Trim();

        var before = loaded.Bases.ToDictionary(x => x.Id);
        var mine = next.Bases.ToDictionary(x => x.Id);
        var touched = mine.Values.Where(x => !before.TryGetValue(x.Id, out var was) || !Same(was, x)).Select(x => x.Id)
            .Concat(before.Keys.Where(id => !mine.ContainsKey(id)))
            // Fixing the ID changes the record only where it has none yet.
            .Concat(copies?.Values ?? []).Concat(ensureId is { } target && !HasPersistentId(target) ? [target] : []).ToHashSet();
        var conflicts = new List<string>();
        foreach (var id in touched.Where(before.ContainsKey))
            if (!fresh.entries.TryGetValue(id, out var now) || !entries.TryGetValue(id, out var was) || Text(now) != Text(was))
                conflicts.Add(before[id].Name);
        // «Режим» is one choice (Codex audit 12.10): «(УП)» changed here is local and touches no record, but with App= of the base
        // changed there meanwhile the two would mix (thin client marked «(УП)»). Any other change of their record stays theirs.
        foreach (var x in mine.Values)
            if (before.TryGetValue(x.Id, out var was) && x.ManagedApplication != was.ManagedApplication && x.ClientType == was.ClientType &&
                current.Bases.FirstOrDefault(c => c.Id == x.Id) is { } now && !now.ClientType.Equals(was.ClientType, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(was.Name);

        var groupsBefore = new HashSet<string>(Groups.All(loaded), StringComparer.OrdinalIgnoreCase);
        var groupsMine = new HashSet<string>(Groups.All(next), StringComparer.OrdinalIgnoreCase);
        var touchedGroups = new HashSet<string>(groupsBefore.Where(x => !groupsMine.Contains(x)), StringComparer.OrdinalIgnoreCase);
        if (renamedFrom is not null) touchedGroups.UnionWith(groupsBefore.Where(x => Groups.Contains(renamedFrom, x)));
        if (groupMap is not null) touchedGroups.UnionWith(groupMap.Where(x => x.Key != x.Value).Select(x => x.Key));
        foreach (var group in touchedGroups.Where(folders.ContainsKey))
            if (!fresh.folders.TryGetValue(group, out var now) || Text(now) != Text(folders[group])) conflicts.Add(group);
        var groupsNow = new HashSet<string>(Groups.All(current), StringComparer.OrdinalIgnoreCase);
        // Only operations on groups count here: a group removed, renamed or formatted, a group made. A group that merely
        // appears or goes with the bases moved into or out of it is no operation of its own (no record of its own changes).
        var explicitLoaded = new HashSet<string>(loaded.Groups, StringComparer.OrdinalIgnoreCase);
        var explicitNext = new HashSet<string>(next.Groups, StringComparer.OrdinalIgnoreCase);
        var removedGroups = new HashSet<string>(explicitLoaded.Where(x => !explicitNext.Contains(x)), StringComparer.OrdinalIgnoreCase);
        // Groups.Rename and RemoveEmpty list the groups of the bases too: a group that was there before in any form is not made here.
        var madeGroups = new HashSet<string>(explicitNext.Where(x => !explicitLoaded.Contains(x) && !groupsBefore.Contains(x)), StringComparer.OrdinalIgnoreCase);
        if (renamedFrom is not null && renamedTo is not null)
        {
            removedGroups.UnionWith(groupsBefore.Where(x => Groups.Contains(renamedFrom, x)));
            madeGroups.UnionWith(groupsMine.Where(x => Groups.Contains(renamedTo, x) && !groupsBefore.Contains(x)));
        }
        if (groupMap is not null)
            foreach (var (from, to) in groupMap.Where(x => !x.Key.Equals(x.Value, StringComparison.Ordinal)))
            { removedGroups.Add(from); if (!groupsBefore.Contains(to)) madeGroups.Add(to); }
        // A group removed or renamed here that got new members there: the removal would be undone or the members left behind.
        foreach (var group in removedGroups)
            if (current.Bases.Any(x => Groups.Contains(group, x.Group) && !(before.TryGetValue(x.Id, out var was) && Groups.Contains(group, was.Group)))
                || groupsNow.Any(x => Groups.Contains(group, x) && !groupsBefore.Contains(x)))
                conflicts.Add(group);
        // A group made here that appeared there meanwhile: one of the two records would be dropped.
        foreach (var group in madeGroups.Where(groupsNow.Contains)) conflicts.Add(group);
        // The base whose properties are saved (its ID fixed) was removed there: nothing to save it into.
        if (ensureId is { } edited && before.ContainsKey(edited) && mine.ContainsKey(edited) && !fresh.entries.ContainsKey(edited)) conflicts.Add(before[edited].Name);
        foreach (var id in required ?? [])
            if (before.ContainsKey(id) && mine.ContainsKey(id) && !fresh.entries.ContainsKey(id)) conflicts.Add(before[id].Name);
        if (conflicts.Count > 0) throw new ListConflictException([.. conflicts.Distinct()]);

        // Their list with this change on top: the records touched here as they are now, theirs as they are on disk.
        var merged = new List<BaseEntry>();
        foreach (var entry in current.Bases)
        {
            if (!touched.Contains(entry.Id)) merged.Add(entry);
            else if (mine.TryGetValue(entry.Id, out var own)) merged.Add(own); // removed here when absent
        }
        merged.AddRange(next.Bases.Where(x => !before.ContainsKey(x.Id) && !merged.Any(m => m.Id == x.Id)));
        var explicitBefore = new HashSet<string>(loaded.Groups, StringComparer.OrdinalIgnoreCase);
        var explicitMine = new HashSet<string>(next.Groups, StringComparer.OrdinalIgnoreCase);
        var groups = current.Groups.Where(x => !explicitBefore.Contains(x) || explicitMine.Contains(x))
            // Only groups made here: a group of the bases carried in next.Groups by Groups.Rename/RemoveEmpty is not one, and
            // when their bases left it there it must not come back as an empty group.
            .Concat(next.Groups.Where(x => !explicitBefore.Contains(x) && !groupsBefore.Contains(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Library saved;
        fresh.folderBaseline = loaded.GroupFolders;
        try { saved = fresh.Save(next with { Bases = merged, Groups = groups }, renamedFrom, renamedTo, ensureId, copies, groupMap); }
        // The merged write and its rollback both failed: this store does not know the file either, the caller sees it as lost.
        catch when (fresh.Lost) { original = null; Lost = true; throw; }
        return (fresh, saved);
    }

    // Write through the existing file handle: symlinks, hardlinks, ACL, owner,
    // mode and alternate streams belong to the same file and survive the save.
    // A flushed backup is mandatory: an in-place write is not crash-atomic.
    // Null output only checks that nobody else changed the file.
    private void WriteInPlace(byte[]? output)
    {
        using var source = new FileStream(path, FileMode.Open, output is not null ? FileAccess.ReadWrite : FileAccess.Read, FileShare.None);
        using var buffer = new MemoryStream(); source.CopyTo(buffer);
        if (!buffer.ToArray().AsSpan().SequenceEqual(original)) throw new ListChangedException();
        if (output is null) return;
        WriteBackup(original!);
        // The marker outlives a crash in the middle of the write: the next load sees it and does not take a torn file for a list.
        // Both names are made durable before the first byte of the list changes: a power loss must not keep the new bytes
        // but lose the marker.
        FilePersistence.Replace(MarkerPath(path), Marker(original!, output, path + ".bak"), path, durable: true);
        try { source.Position = 0; source.Write(output); source.SetLength(output.Length); source.Flush(true); }
        catch (Exception writeError)
        {
            try { source.Position = 0; source.Write(original); source.SetLength(original!.Length); source.Flush(true); }
            catch (Exception rollbackError)
            { original = null; Lost = true; throw new IOException($"Запись и восстановление не выполнены. Резервная копия: {path}.bak", new AggregateException(writeError, rollbackError)); }
            DeleteMarker(path);
            throw new IOException("Запись не выполнена; исходное содержимое восстановлено.", writeError);
        }
        // Removed while the file is still held: another reader never sees the marker of a write that already finished.
        DeleteMarker(path);
    }

    /// <summary>The marker lives next to the physical file: every symbolic name of one list finds the same marker. Hard links
    /// have no common name: a crash is seen only through the name it was written by and the symbolic names leading to it;
    /// any other hard name of the file, even in the same folder, reads the torn text.</summary>
    private static string MarkerPath(string list) => FilePersistence.Target(list) + ".writing";

    /// <summary>The marker of this list, one of the program's own files: nothing else may be written over it.</summary>
    public string MarkerPathName => MarkerPath(path);
    private const string MarkerHeader = "BaseNavigator write in progress";

    /// <summary>The .bak sits next to the name the list was opened by, so the marker names it as the writer saw it.</summary>
    private static byte[] Marker(byte[] before, byte[] after, string backup) => Encoding.UTF8.GetBytes(
        MarkerHeader + "\nbefore=" + Convert.ToHexString(SHA256.HashData(before)) + "\nafter=" + Convert.ToHexString(SHA256.HashData(after)) +
        "\nbackup=" + Path.GetFullPath(backup) + "\n");

    /// <summary>The hashes of the text before and after the write the marker announces and its .bak; null for a marker this
    /// program did not write or cannot read. Such a marker is treated as an interrupted write too: the file is not trusted
    /// without the user.</summary>
    private static (string Before, string After, string Backup)? ReadMarker(string marker)
    {
        try
        {
            var lines = File.ReadAllLines(marker);
            string? Value(string key) => lines.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
            return lines.FirstOrDefault() == MarkerHeader && Value("before") is { Length: 64 } before && Value("after") is { Length: 64 } after
                && Value("backup") is { Length: > 0 } backup ? (before, after, backup) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // A marker that outlived its write is only a leftover: removing it may fail on a read-only share, the next load decides again.
    private static void DeleteMarker(string list)
    {
        try { File.Delete(MarkerPath(list)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A leftover marker of a finished write that could not be removed: the text for the status line, else null.</summary>
    public string? MarkerNotice { get; private set; }

    // A leftover of a rolled-back write (the file is the text before it) is read past and goes with the next save: a writer that
    // has made its marker but not yet changed a byte looks the same. A leftover of a finished write (the file is the text after it)
    // is removed here (Grok full audit 24.10, P2-1): on a shared list the next change is made by 1C or another computer, and the
    // marker would make that good list look torn and offer the older .bak.
    private static string? CheckInterrupted(string list, byte[] bytes)
    {
        var marker = MarkerPath(list);
        if (!File.Exists(marker)) return null;
        var hashes = ReadMarker(marker);
        var current = Convert.ToHexString(SHA256.HashData(bytes));
        // The text after the write: finished. Also when it equals the text before (a write of the same bytes by an earlier build):
        // such a marker would never be read past otherwise (Codex re-check 25.10, R3).
        if (hashes is { } finished && current == finished.After) return RemoveFinished(list, finished);
        if (hashes is { } known && (current == known.After || current == known.Before)) return null;
        throw new InterruptedWriteException(Path.GetFullPath(list), bytes, hashes is { } expected && MatchingBackup(list, expected) is not null);
    }

    // Only while the list is held with FileShare.None (a writer holds it so for the whole write) and still the text after the write
    // the marker names, read again together with the marker. A list busy with a writer keeps the marker: that write decides.
    private static string? RemoveFinished(string list, (string Before, string After, string Backup) seen)
    {
        var marker = MarkerPath(list);
        FileStream source;
        // Busy: a writer may hold the list, so the marker stays; but it may as well be a leftover, which is said (Grok audit of the
        // fixes 25.10, P3), as for a delete that failed.
        try { source = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Отметка записи списка не проверена: список занят другой программой ({ex.Message}). Если запись завершена, удалите " +
                $"отметку вручную: {marker}, иначе следующее изменение списка другой программой будет принято за оборванную запись.";
        }
        using (source)
        {
            using var buffer = new MemoryStream(); source.CopyTo(buffer);
            if (Convert.ToHexString(SHA256.HashData(buffer.ToArray())) != seen.After || ReadMarker(marker) != seen) return null;
            try { BeforeMarkerRemoval?.Invoke(marker); File.Delete(marker); return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(marker)) return null;
                return $"Отметка завершённой записи списка не удалена ({ex.Message}): {marker}. Удалите её вручную, иначе следующее изменение " +
                    "списка другой программой будет принято за оборванную запись.";
            }
        }
    }

    /// <summary>Tests: runs right before a leftover marker of a finished write is removed.</summary>
    internal static Action<string>? BeforeMarkerRemoval;

    /// <summary>The text from before the write, from the first .bak whose SHA-256 is the one in the marker. The path the writer
    /// wrote is tried first; another computer or OS sees the same share under another name, so the .bak next to the name
    /// opened here and the one next to the physical file are tried too. A copy is never taken without the hash.</summary>
    private static byte[]? MatchingBackup(string list, (string Before, string After, string Backup) marker)
    {
        var candidates = new[] { Path.IsPathFullyQualified(marker.Backup) ? marker.Backup : null, list + ".bak", FilePersistence.Target(list) + ".bak" };
        foreach (var candidate in candidates.OfType<string>().Distinct())
        {
            try { if (File.ReadAllBytes(candidate) is var bytes && Convert.ToHexString(SHA256.HashData(bytes)) == marker.Before) return bytes; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>Puts back the text from before the interrupted write. The file must still be the one the user saw
    /// (<paramref name="seen"/>) and the .bak must still be the text the marker names; the .bak itself is kept as it is,
    /// a torn file never replaces it. Afterwards the list is to be loaded again.</summary>
    public static void RecoverInterruptedWrite(string list, byte[] seen)
    {
        if (ListAccess.ReadOnlyReason(list) is { } reason) throw new ListReadOnlyException(reason, ListReadOnlyException.RecoveryEffect);
        using var source = new FileStream(list, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var buffer = new MemoryStream(); source.CopyTo(buffer);
        if (!buffer.ToArray().AsSpan().SequenceEqual(seen)) throw new IOException("Список изменён другой программой. Нажмите «Перечитать список баз».");
        if (ReadMarker(MarkerPath(list)) is not { } hashes) throw new IOException("Отметка прерванной записи не прочитана; восстановить список из .bak нельзя.");
        if (MatchingBackup(list, hashes) is not { } backup)
            throw new IOException("Резервная копия не совпадает с текстом до прерванной записи; восстановить список из .bak нельзя.");
        source.Position = 0; source.Write(backup); source.SetLength(backup.Length); source.Flush(true);
        // The list is the text from before now; a marker left behind is read past as the trace of a rolled-back write.
        DeleteMarker(list);
    }

    /// <summary>Keeps the file as it is now: the marker is removed only while the file is still the one the user saw.</summary>
    public static void DismissInterruptedWrite(string list, byte[] seen)
    {
        if (ListAccess.ReadOnlyReason(list) is { } reason) throw new ListReadOnlyException(reason, ListReadOnlyException.RecoveryEffect);
        using var source = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.None);
        using var buffer = new MemoryStream(); source.CopyTo(buffer);
        if (!buffer.ToArray().AsSpan().SequenceEqual(seen)) throw new IOException("Список изменён другой программой. Нажмите «Перечитать список баз».");
        File.Delete(MarkerPath(list));
    }

    private const char Bom = (char)0xFEFF;

    /// <summary>Puts the text of a snapshot back into the list, byte for byte, with the same guarantees as a save: the file
    /// must be unchanged since it was loaded, the previous text goes to .bak, a failed write is rolled back. The snapshot
    /// must be a list this program reads; afterwards the list is to be loaded again.</summary>
    public void Restore(byte[] content)
    {
        if (original is null) throw new InvalidOperationException("Сначала откройте список .v8i.");
        if (ReadOnly is { } reason) throw new ListReadOnlyException(reason);
        if (content.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Список в снимке слишком велик.");
        // Read in memory, not through a temporary file (Codex full audit 24.10, P1-2).
        var probe = new V8iStore(path);
        var loaded = probe.Parse(content, new());
        // Text without a single «[…]» section is not a list, even though it reads as an empty one.
        if (loaded.Bases.Count == 0 && loaded.Groups.Count == 0 && probe.sections.Count == 0 &&
            probe.prefix.Any(x => !string.IsNullOrWhiteSpace(x.Replace(Bom, ' '))) && !probe.prefix.Any(x => x.Replace(Bom, ' ').TrimStart().StartsWith('[')))
            throw new InvalidDataException("В снимке нет списка информационных баз.");
        // The same bytes as the list: nothing to write, no write transaction (.bak, marker); the list is only checked unchanged.
        WriteInPlace(content.AsSpan().SequenceEqual(original) ? null : content);
        original = null;
    }

    private void WriteBackup(byte[] bytes)
    {
        FilePersistence.Replace(path + ".bak", bytes, path, durable: true);
    }

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new ArgumentException("Название не должно быть пустым или содержать переносы строк.");
    }
    private static Section Update(Section section, BaseEntry entry, BaseEntry? before)
    {
        if (before is null || entry.Name != before.Name) section.Name = entry.Name;
        if (before is null || entry.Group != before.Group) section.Set("Folder", "/" + entry.Group);
        // A new record gets the version chosen for it (the search on the computer offers 8.1–8.3); 8.3 when none is known.
        if (before is null) { section.Set("ID", entry.Id.ToString()); section.Set("Version", PlatformCatalog.IsVersion(entry.PlatformVersion) ? entry.PlatformVersion : "8.3"); }
        // «Назначить версию» (Shift+Ctrl+F8) changes the version of a base, as the original does in the list itself.
        else if (entry.PlatformVersion != before.PlatformVersion && PlatformCatalog.IsVersion(entry.PlatformVersion)) section.Set("Version", entry.PlatformVersion);
        // «Режим» of the properties, as the original writes it to the list: App=Auto/ThinClient/ThickClient/WebClient.
        if (before is null ? entry.ClientType.Length > 0 : entry.ClientType != before.ClientType)
            section.Set("App", entry.ClientType.Length > 0 ? entry.ClientType : "Auto");
        // As 1CEStart: WA=0 asks for the name and password, WA=1 chooses automatically. A new record gets the line only to ask.
        if (before is null ? entry.AskCredentials : entry.AskCredentials != before.AskCredentials) section.Set("WA", entry.AskCredentials ? "0" : "1");
        // WSA=1 asks for the name and password of the web server, WSA=0 chooses automatically; a record without the line stays so.
        if (entry.WebServerAskCredentials is { } askWeb && askWeb != before?.WebServerAskCredentials) section.Set("WSA", askWeb ? "1" : "0");
        // «Скорость соединения» as the original writes it: Normal or Low; «Выбирать при запуске» removes the line.
        // A value it does not know (null) stays until a speed is chosen.
        if (entry.ConnectionSpeed is { } speed && (before is null ? speed.Length > 0 : speed != before.ConnectionSpeed))
        {
            if (speed.Length > 0) section.Set("ClientConnectionSpeed", speed);
            else section.Remove("ClientConnectionSpeed");
        }
        // «Использование Proxy»: UseProxy=0/1/2; a record without the line stays so until a way is chosen. The address goes to
        // ProxyServer= (the original wrote it over ClientConnectionSpeed=, _Unit205.pas:26583). An emptied field removes its line.
        if (entry.UseProxy is { } proxy && proxy != before?.UseProxy) section.Set("UseProxy", ((int)proxy).ToString(CultureInfo.InvariantCulture));
        foreach (var (key, value, was) in new[] { ("ProxyServer", entry.ProxyServer, before?.ProxyServer), ("ProxyPort", entry.ProxyPort, before?.ProxyPort),
            ("ProxyUser", entry.ProxyUser, before?.ProxyUser) })
        {
            if (value == (was ?? "")) continue;
            if (value.Length > 0) section.Set(key, value); else section.Remove(key);
        }
        if (before is null || entry.Kind != before.Kind || entry.Location != before.Location)
        {
            section.Set("Connect", RebuildConnect(section.Get("Connect"), entry, before?.Kind));
        }
        // «Дополнительные параметры» of the properties, as the original writes them (_Unit205.pas:26354–26479): the trimmed text
        // replaces the value, an emptied one removes the line, a new line goes to the end of the record. 1CEStart reads it too.
        var parameters = entry.ListParameters.Trim();
        if (parameters != (before?.ListParameters.Trim() ?? ""))
        {
            if (parameters.Length == 0) section.Remove(ParametersKey);
            else if (section.Get(ParametersKey) is not null) section.Set(ParametersKey, parameters);
            else section.Lines.Insert(section.Lines.FindLastIndex(x => x.Trim().Length > 0) + 1, ParametersKey + "=" + parameters);
        }
        return section;
    }

    private const string ParametersKey = "AdditionalParameters";

    /// <summary>Connect in the 1C format: the new location first, in double quotes with doubled inner quotes; every other
    /// parameter stays as written (key spelling, quoting). DbConnectionStringBuilder would switch a value with a quote to
    /// single quotes and lower-case the keys, which is the ADO.NET format, not the one of 1C.</summary>
    internal static string RebuildConnect(string? connect, BaseEntry entry, ConnectionKind? previous = null)
    {
        var location = BaseClipboard.ConnectionString(entry).TrimEnd(';');
        // The web options (wsn, wsp, the proxy wsp…) belong to a base on a web server: they are dropped only when a web base
        // becomes a file or server one. Unknown keys of a file or server base stay, even when they start with «ws».
        bool Replaced(string key) => new[] { "File", "Srvr", "Ref", "ws" }.Contains(key, StringComparer.OrdinalIgnoreCase) ||
            previous == ConnectionKind.Web && entry.Kind != ConnectionKind.Web && key.StartsWith("ws", StringComparison.OrdinalIgnoreCase);
        var kept = Segments(connect ?? "").Where(segment =>
            segment.IndexOf('=') is var index && (index <= 0 || !Replaced(segment[..index].Trim())));
        return string.Join(";", kept.Prepend(location)) + ";";
    }

    // Parameters split by «;» outside double quotes; a doubled quote inside a value toggles twice and changes nothing.
    private static IEnumerable<string> Segments(string text)
    {
        var start = 0; var quoted = false;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] == '"') quoted = !quoted;
            else if (index == text.Length || text[index] == ';' && !quoted)
            {
                var segment = text[start..index].Trim();
                if (segment.Length > 0) yield return segment;
                start = index + 1;
            }
        }
    }
}
