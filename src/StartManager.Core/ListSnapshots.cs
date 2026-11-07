using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace StartManager.Core;

/// <summary>A copy of a list of bases taken at a moment: the .v8i text in a .v8sx archive, as the original's snapshots.</summary>
/// Auto: taken by the program itself before the first change of the day, as the original's automatic snapshots.
/// Version: the original's column «Версия», the branch of 1C whose standard list was taken (8.1, 8.2, 8.3); null when the
/// snapshot is of another list (older snapshots have none).
public sealed record ListSnapshot(string File, string Source, DateTime Created, int Bases, string Description, bool Auto = false, string? Version = null)
{
    /// <summary>Fields of the catalog this program does not know (a newer version wrote them): kept when the catalog is rewritten.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The original's «Создать снимок списка ИБ для 8.3 / 8.2 / 8.1» (TSettings.V8GetListFile): the standard list of each
/// branch. 8.2 and 8.3 share the list of 1CEStart; 8.1 has its own in 1Cv81. On Linux only the list of 1CEStart exists.</summary>
public static class SnapshotBranches
{
    public static readonly string[] All = ["8.3", "8.2", "8.1"];

    public static string? ListPath(string branch) => branch switch
    {
        "8.3" or "8.2" when OperatingSystem.IsWindows() || branch == "8.3" => V8iStore.StandardPath,
        "8.1" when OperatingSystem.IsWindows() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1Cv81", "ibases.v8i"),
        _ => null
    };

    /// <summary>The branches whose list exists, in the order of the original's menu.</summary>
    public static IReadOnlyList<(string Branch, string Path)> Available() =>
        [.. All.Select(x => (Branch: x, Path: ListPath(x))).Where(x => x.Path is not null && File.Exists(x.Path)).Select(x => (x.Branch, x.Path!))];
}

/// <summary>The bases of a local list as the profile keeps them: the client, the version rule and unknown fields that the .v8i
/// text cannot carry. Stored next to ibases.v8i in a snapshot of a local list.
/// The folders of the groups are there too; a snapshot made before 13.10.2026 has none (null).</summary>
public sealed record LocalListState(List<BaseEntry> Bases, List<string> Groups, List<GroupFolder>? GroupFolders = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static byte[] Write(Library library) => JsonSerializer.SerializeToUtf8Bytes(new LocalListState([.. library.Bases], [.. library.Groups], [.. library.GroupFolders]), Options);
    public static LocalListState Read(byte[] data)
    {
        try
        {
            var state = JsonSerializer.Deserialize<LocalListState>(data, Options) ?? throw new InvalidDataException("Пустое состояние списка.");
            LibraryStore.Validate(new Library { Bases = state.Bases ?? [], Groups = state.Groups ?? [], GroupFolders = state.GroupFolders ?? [] });
            return state;
        }
        catch (JsonException ex) { throw new InvalidDataException("Состояние локального списка в снимке повреждено.", ex); }
    }
}

/// <summary>
/// The original's «Снимки списка информационных баз»: .v8sx files (a ZIP with one ibases.v8i, the format the original
/// writes and the import reads) and a catalog next to them. The catalog is changed under a lock, so two windows add
/// their snapshots without losing one. A damaged catalog is not overwritten.
/// </summary>
public sealed class ListSnapshots(string folder)
{
    // Cyrillic descriptions stay readable in the catalog.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Folder { get; } = Path.GetFullPath(folder);
    private string Catalog => Path.Combine(Folder, "snapshots.json");

    /// <summary>The snapshots whose file still exists, newest first.</summary>
    public IReadOnlyList<ListSnapshot> List() =>
        [.. Read().Where(x => File.Exists(Path.Combine(Folder, Path.GetFileName(x.File)))).OrderByDescending(x => x.Created)];

    /// <param name="state">The local list's own state (<see cref="LocalListState"/>), stored as basenavigator.json.</param>
    public ListSnapshot Create(string source, byte[] list, string description, byte[]? state = null, bool auto = false, string? version = null)
    {
        // The same limits as reading: a snapshot that could not be read back is refused before anything is written.
        if (list.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Список слишком велик для снимка.");
        if (state is not null && state.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Состояние списка слишком велико для снимка (больше 16 МБ).");
        Directory.CreateDirectory(Folder);
        var created = DateTime.Now;
        var name = created.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".v8sx";
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                using (var entry = archive.CreateEntry("ibases.v8i", CompressionLevel.Optimal).Open()) entry.Write(list);
                if (state is not null) using (var entry = archive.CreateEntry(StateEntry, CompressionLevel.Optimal).Open()) entry.Write(state);
            }
            FilePersistence.Replace(Path.Combine(Folder, name), buffer.ToArray(), null);
        }
        var snapshot = new ListSnapshot(name, source, created, CountBases(list), description.Trim(), auto, version);
        try { Change(items => [.. items, snapshot]); }
        catch { File.Delete(Path.Combine(Folder, name)); throw; }
        return snapshot;
    }

    /// <summary>The list text of a snapshot, with the limits of the import: one root ibases.v8i of at most 16 MB.</summary>
    private const string StateEntry = "basenavigator.json";

    /// <summary>Whether the list has no snapshot taken today: then the first change of the day is preceded by an automatic one.</summary>
    public bool NeedsAuto(string source, DateTime now) =>
        !List().Any(x => x.Source.Equals(source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && x.Created.Date == now.Date);

    /// <summary>The automatic snapshot of the day, if the list has none yet: the check, the snapshot and the pruning happen under
    /// one lock shared by every window, so two windows never take two. The content is read only when it is needed.</summary>
    public ListSnapshot? CreateAutoIfNeeded(string source, Func<(byte[] List, byte[]? State)> content, string description, int keep, string? version = null)
    {
        Directory.CreateDirectory(Folder);
        using var gate = FilePersistence.Lock(Path.Combine(Folder, "auto"));
        if (!NeedsAuto(source, DateTime.Now)) return null;
        var (list, state) = content();
        var created = Create(source, list, description, state, auto: true, version: version);
        PruneAuto(source, keep);
        return created;
    }

    /// <summary>Keeps the newest automatic snapshots of a list; manual ones are never removed here.</summary>
    public void PruneAuto(string source, int keep)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var old = List().Where(x => x.Auto && x.Source.Equals(source, comparison)).Skip(keep).ToList();
        if (old.Count > 0) Delete(old);
    }

    /// <summary>The local list's own state of a snapshot, or null for a snapshot of a shared list or of the original.</summary>
    public byte[]? ReadState(ListSnapshot snapshot)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(Folder, Path.GetFileName(snapshot.File)));
        var entry = archive.Entries.SingleOrDefault(x => x.FullName == StateEntry);
        if (entry is null) return null;
        if (entry.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Состояние списка в снимке слишком велико.");
        using var stream = entry.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }

    public byte[] Read(ListSnapshot snapshot)
    {
        var path = Path.Combine(Folder, Path.GetFileName(snapshot.File));
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Where(x => x.FullName.Equals("ibases.v8i", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1 || entries[0].Length > LegacyImport.MaximumBytes)
                throw new InvalidDataException("Снимок должен содержать один ibases.v8i не больше 16 МБ.");
            using var stream = entries[0].Open(); using var copy = new MemoryStream();
            var chunk = new byte[81920]; int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                copy.Write(chunk, 0, read);
                if (copy.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Список в снимке слишком велик.");
            }
            return copy.ToArray();
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("Снимок", StringComparison.Ordinal) || ex.Message.StartsWith("Список", StringComparison.Ordinal)) { throw; }
        catch (InvalidDataException ex) { throw new InvalidDataException("Снимок повреждён: " + snapshot.File, ex); }
    }

    /// <summary>Removes the snapshots; a file that cannot be removed (open in another window) keeps its record and is returned.</summary>
    public IReadOnlyList<ListSnapshot> Delete(IEnumerable<ListSnapshot> snapshots)
    {
        var chosen = snapshots.ToList();
        var failed = new List<ListSnapshot>();
        Change(items =>
        {
            foreach (var snapshot in chosen)
            {
                var path = Path.Combine(Folder, Path.GetFileName(snapshot.File));
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(snapshot); }
            }
            var gone = chosen.Except(failed).Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. items.Where(x => !gone.Contains(x.File))];
        });
        return failed;
    }

    /// <summary>A list file read for a snapshot: refused before it is read when it is larger than the limit, and while reading
    /// when it grows past it.</summary>
    public static byte[] ReadList(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Список слишком велик для снимка (больше 16 МБ).");
        using var copy = new MemoryStream();
        var chunk = new byte[81920]; int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            copy.Write(chunk, 0, read);
            if (copy.Length > LegacyImport.MaximumBytes) throw new InvalidDataException("Список слишком велик для снимка (больше 16 МБ).");
        }
        return copy.ToArray();
    }

    /// <summary>The records of a .v8i text that have a connection: the bases of the snapshot.</summary>
    public static int CountBases(byte[] list)
    {
        var text = DecodeList(list);
        return text.Split('\n').Count(x => x.TrimStart().StartsWith("Connect=", StringComparison.OrdinalIgnoreCase));
    }

    private static string DecodeList(byte[] list)
    {
        if (list.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(list, 2, list.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(list); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1251).GetString(list); }
    }

    private List<ListSnapshot> Read()
    {
        if (!File.Exists(Catalog)) return [];
        try { return JsonSerializer.Deserialize<List<ListSnapshot>>(File.ReadAllBytes(Catalog), Options)?.Where(x => x is not null).ToList() ?? []; }
        catch (JsonException ex) { throw new InvalidDataException("Каталог снимков повреждён: " + Catalog, ex); }
    }

    private void Change(Func<List<ListSnapshot>, List<ListSnapshot>> update)
    {
        Directory.CreateDirectory(Folder);
        using var gate = FilePersistence.Lock(Catalog);
        var next = update(Read());
        var current = File.Exists(Catalog) ? File.ReadAllBytes(Catalog) : null;
        FilePersistence.Replace(Catalog, JsonSerializer.SerializeToUtf8Bytes(next, Options), current is null ? null : Catalog);
    }
}
