namespace StartManager.Core;

/// <summary>A process with a file open under a folder.</summary>
public sealed record FileHolder(int Pid, string Name)
{
    public override string ToString() => $"{Name} ({Pid})";
}

/// <summary>
/// Linux: which processes hold files under a folder, from the links in /proc/&lt;pid&gt;/fd. Windows itself refuses to rename
/// or share a file held by 1C; Linux does not, so the cache cleaning and the copy of a base ask /proc first.
/// Only the processes this user may inspect are seen: 1C of another user or of another computer on a shared folder
/// stays invisible. A file opened between the check and the operation is not caught either.
/// </summary>
public static class OpenFiles
{
    public static IReadOnlyList<FileHolder> Under(string folder, string procRoot = "/proc")
    {
        // Windows protects open files by its sharing rules; other systems than Linux are not supported.
        if (!OperatingSystem.IsLinux()) return [];
        if (!Directory.Exists(procRoot)) throw new IOException("Не удалось проверить открытые файлы: " + procRoot + " недоступен.");
        // /proc shows the physical path: a folder reached through a link is compared by its target.
        var targets = new[] { Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), BaseDuplicate.Physical(folder) }.Distinct().ToArray();
        var result = new List<FileHolder>();
        // /proc that cannot be listed proves nothing: the operation is refused instead of treated as free.
        List<string> processes;
        try { processes = Directory.EnumerateDirectories(procRoot).Where(x => int.TryParse(Path.GetFileName(x), out _)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("Не удалось проверить открытые файлы: " + procRoot + " недоступен.", ex); }
        if (processes.Count == 0) throw new IOException("Не удалось проверить открытые файлы: в " + procRoot + " нет процессов.");
        foreach (var process in processes)
        {
            var pid = int.Parse(Path.GetFileName(process));
            foreach (var descriptor in Descriptors(process))
            {
                string? link;
                try { link = new FileInfo(Path.Combine(process, "fd", descriptor)).LinkTarget; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (link is null || !targets.Any(target => Contains(target, link))) continue;
                result.Add(new(pid, Name(process)));
                break;
            }
        }
        return result;
    }

    /// <summary>Whether the file named by a /proc link lies in the folder; a removed file ends with « (deleted)».</summary>
    public static bool Contains(string folder, string link)
    {
        const string deleted = " (deleted)";
        if (link.EndsWith(deleted, StringComparison.Ordinal)) link = link[..^deleted.Length];
        folder = folder.TrimEnd('/');
        return link.Equals(folder, StringComparison.Ordinal) || link.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    /// <summary>The message for a refused operation: who holds the files.</summary>
    public static string Describe(IReadOnlyList<FileHolder> holders) =>
        "Файлы открыты: " + string.Join(", ", holders.Take(5)) + (holders.Count > 5 ? $" и ещё {holders.Count - 5}" : "") + ".";

    private static string Name(string process)
    {
        try { return File.ReadAllText(Path.Combine(process, "comm")).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "процесс"; }
    }

    // The descriptor numbers come from fdinfo, whose entries are plain files. Listing fd itself makes .NET stat the target of
    // every link to tell a folder from a file, and a file open on a slow network or FUSE mount turns that into minutes
    // (measured 01.10.2026: 0.02 s against 230 s for one pass). The link itself is then read without touching its target.
    // fdinfo that is missing, denied or empty must not read as «nothing open» where fd itself still shows the descriptors:
    // then the slow listing of fd is used, as before. An empty fd costs nothing there, having no links to stat.
    // A process may end or deny access while it is listed.
    private static List<string> Descriptors(string process)
    {
        try
        {
            var fast = Directory.EnumerateFiles(Path.Combine(process, "fdinfo")).Select(Path.GetFileName).OfType<string>().ToList();
            if (fast.Count > 0) return fast;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { return [.. Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")).Select(Path.GetFileName).OfType<string>()]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
