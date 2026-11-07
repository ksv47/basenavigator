namespace StartManager.Core;

/// <summary>The original's «Дублировать информационную базу»: the files of a file base are copied to a new folder
/// and the list gets a copy of its record with a new ID.</summary>
public static class BaseDuplicate
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string SuggestName(string name, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.CurrentCultureIgnoreCase);
        var candidate = name + " (копия)";
        for (var number = 2; taken.Contains(candidate); number++) candidate = $"{name} (копия {number})";
        return candidate;
    }

    public static string SuggestFolder(string location)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        var candidate = source + "_copy";
        for (var number = 2; Directory.Exists(candidate) || File.Exists(candidate); number++) candidate = $"{source}_copy{number}";
        return candidate;
    }

    /// <summary>The new folder must be absent or empty and lie outside the source; the source must hold 1Cv8.1CD.</summary>
    public static void CheckTarget(string sourceLocation, string targetLocation)
    {
        if (string.IsNullOrWhiteSpace(targetLocation)) throw new ArgumentException("Укажите каталог новой базы.");
        if (targetLocation.Any(char.IsControl) || targetLocation.Contains('"')) throw new ArgumentException("Каталог содержит недопустимые символы.");
        if (!Path.IsPathFullyQualified(targetLocation)) throw new ArgumentException("Укажите полный путь к каталогу новой базы.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceLocation));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetLocation));
        if (!File.Exists(Path.Combine(source, "1Cv8.1CD"))) throw new ArgumentException("В каталоге базы нет файла 1Cv8.1CD.");
        // Links anywhere in either path are resolved: a junction or symlink must not hide that the folders coincide or nest.
        foreach (var (from, to) in new[] { (source, target), (Physical(source), Physical(target)) })
        {
            if (to.Equals(from, PathComparison)) throw new ArgumentException("Каталог новой базы совпадает с каталогом исходной.");
            if (to.StartsWith(from + Path.DirectorySeparatorChar, PathComparison) || from.StartsWith(to + Path.DirectorySeparatorChar, PathComparison))
                throw new ArgumentException("Каталоги исходной и новой базы не должны быть вложены друг в друга.");
        }
        if (File.Exists(target)) throw new ArgumentException("По указанному пути уже есть файл.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new ArgumentException("Каталог новой базы не пуст. Укажите новый или пустой каталог.");
        if (Path.GetDirectoryName(target) is not { } parent || !Directory.Exists(parent))
            throw new ArgumentException("Родительский каталог новой базы не существует.");
    }

    /// <summary>Total size of the files to copy, for the progress.</summary>
    public static long Measure(string sourceLocation) =>
        Tree(Path.GetFullPath(sourceLocation)).Files.Sum(x => new FileInfo(x).Length);

    /// <summary>Copies into a staging folder next to the target and renames it at the end: an interrupted copy never
    /// looks like a base. Each source file is opened without write sharing, so a base open in 1C (Windows) fails
    /// instead of giving an inconsistent copy. On failure or cancellation the staging folder is removed; when that fails too,
    /// <see cref="IncompleteCopyException"/> names the folder left behind.</summary>
    public static void CopyFiles(string sourceLocation, string targetLocation, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        CheckTarget(sourceLocation, targetLocation);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceLocation));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetLocation));
        // The tree is listed before anything is written, so the copy never walks into its own output.
        var (files, directories) = Tree(source);
        CheckClosed(source);
        // The size and time of every file before it is read: a session that writes meanwhile (Linux has no lock against a native
        // writer) fails the copy instead of publishing files of two moments (Codex full audit 24.10, P2-4), as for a ZIP backup.
        var before = files.Select(x => (Path: x, Stamp: Stamp(x))).ToList();
        var staging = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".copying-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            long copied = 0;
            var buffer = new byte[1 << 20];
            foreach (var directory in directories)
            {
                cancel.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                    for (int read; (read = input.Read(buffer)) > 0;)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        progress?.Report(copied += read);
                    }
                    output.Flush(true);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    throw new IOException($"Файл {Path.GetRelativePath(source, file)} занят другой программой. Завершите все сеансы 1С с этой базой и повторите.", ex);
                }
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
            }
            cancel.ThrowIfCancellationRequested();
            // Attributes last: a read-only file must not block writing the copy.
            foreach (var file in files) CopyAttributes(file, Path.Combine(staging, Path.GetRelativePath(source, file)));
            // Before the copy becomes a base: no session opened meanwhile, no file changed. A writer this computer does not see
            // (another computer on a network folder) is not found here.
            CheckClosed(source);
            if (before.FirstOrDefault(x => Stamp(x.Path) != x.Stamp) is { Path: { } changed })
                throw new IOException($"Файл {Path.GetRelativePath(source, changed)} изменился во время копирования: копия не цельная. Завершите все сеансы 1С с этой базой и повторите.");
            // The target was checked to be absent or empty; an empty folder gives way to the copy.
            if (Directory.Exists(target)) Directory.Delete(target);
            Directory.Move(staging, target);
        }
        catch (Exception reason)
        {
            try { RemoveStaging(staging); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IncompleteCopyException(staging, reason, cleanup); }
            throw;
        }
    }

    // Linux gives no sharing violation for a base open in 1C: its processes are found in /proc instead, before and after the copy.
    private static void CheckClosed(string source)
    {
        if (OpenFiles.Under(source) is { Count: > 0 } holders)
            throw new IOException($"База открыта в другой программе. Завершите все сеансы 1С с этой базой и повторите. {OpenFiles.Describe(holders)}");
    }

    private static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
    }

    // Read-only, hidden, system and the other file attributes on Windows, the permission bits elsewhere.
    private static void CopyAttributes(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(destination, File.GetAttributes(source) & (FileAttributes.ReadOnly | FileAttributes.Hidden |
                FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed));
        else File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    // The staging folder is ours and has no links; read-only copies would stop Directory.Delete on Windows.
    private static void RemoveStaging(string staging)
    {
        if (!Directory.Exists(staging)) return;
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(staging, true);
    }

    /// <summary>The copy of the record: the same settings, a new ID, name and folder.</summary>
    public static BaseEntry Clone(BaseEntry source, string name, string location) =>
        source with { Id = Guid.NewGuid(), Name = name.Trim(), Location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)) };

    // Links are not followed: a link inside the base folder could lead anywhere, so such a base is not copied.
    private static (List<string> Files, List<string> Directories) Tree(string source)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        var files = new List<string>(); var directories = new List<string>();
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", options))
        {
            if (entry.LinkTarget is not null) throw new IOException($"В каталоге базы есть ссылка {Path.GetRelativePath(source, entry.FullName)}; такую базу скопируйте вручную.");
            (entry is FileInfo ? files : directories).Add(entry.FullName);
        }
        return (files, directories);
    }

    /// <summary>The path with every existing link component (symlink, junction) replaced by its final target;
    /// the part that does not exist yet stays as written.</summary>
    internal static string Physical(string path, int depth = 0)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (depth > 32) throw new IOException("Слишком длинная цепочка ссылок в пути " + path);
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var next = Path.Combine(current, parts[index]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists) return Path.Combine([next, .. parts[(index + 1)..]]);
            if (info.LinkTarget is null) { current = next; continue; }
            // The target of a link may itself lie behind other links. A volume mounted into a folder and having no drive
            // letter (C:\PORTABLE → «Volume{guid}\») resolves to that folder itself: it is the physical name, like a drive
            // letter. Every other name of that volume resolves to the same folder, so names still compare as one place.
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            current = target is null || Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)).Equals(next,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? next : Physical(target, depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool IsSharingViolation(IOException ex) => OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is 32 or 33;
}

/// <summary>The copy failed or was cancelled and its staging folder could not be removed.</summary>
public sealed class IncompleteCopyException(string leftover, Exception reason, Exception cleanup)
    : IOException($"Незавершённая копия не удалена: {leftover}. Удалите этот каталог вручную.", new AggregateException(reason, cleanup))
{
    public string Leftover { get; } = leftover;
    public Exception Reason { get; } = reason;
    public bool Cancelled => Reason is OperationCanceledException;
}
