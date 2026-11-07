namespace StartManager.Core;

/// <summary>A file base found on the disk: the folder with 1Cv8.1CD, its size and the time of the last change.</summary>
public sealed record FoundBase(string Folder, long Size, DateTime Modified);

public enum SizeCondition { Any, Greater, Less, Equal }

/// <summary>The original's «Правила поиска»: the date of the last change not later than a day (the whole day counts), the size
/// of 1Cv8.1CD compared with a number of megabytes: «больше» and «меньше» exactly, «равен» — the whole megabytes the file
/// holds (5 МБ is 5 and anything up to, not including, 6).</summary>
public sealed record FoundFilter(DateTime? NotLaterThan = null, SizeCondition Size = SizeCondition.Any, double Megabytes = 0)
{
    public bool Matches(FoundBase found)
    {
        if (NotLaterThan is { } day && found.Modified.Date > day.Date) return false;
        var megabytes = found.Size / 1048576.0;
        return Size switch
        {
            SizeCondition.Greater => megabytes > Megabytes,
            SizeCondition.Less => megabytes < Megabytes,
            SizeCondition.Equal => Math.Floor(megabytes) == Math.Floor(Megabytes),
            _ => true
        };
    }
}

/// <summary>
/// The original's «Поиск информационных баз на компьютере»: folders with 1Cv8.1CD under the chosen paths. Links and junctions
/// are not followed (no loops, no second visit of a folder), folders this user may not read are skipped, a found base is not
/// searched inside. Nothing is written.
/// </summary>
public static class BaseFinder
{
    private static readonly string[] SkippedNames = ["$Recycle.Bin", "System Volume Information", "$WinREAgent", "node_modules", ".git"];
    private static readonly string[] SkippedLinux = ["/proc", "/sys", "/dev", "/run", "/snap"];

    /// <param name="cacheRoots">Folders of the platform's cache: its configuration caches (cgf-cache) hold 1Cv8.1CD files
    /// that are not bases. By default the cache of this user.</param>
    public static IEnumerable<FoundBase> Scan(IEnumerable<string> roots, CancellationToken cancel = default, Action<int>? visited = null,
        BaseCacheRoots? cacheRoots = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparison);
        cacheRoots ??= BaseCacheRoots.Current;
        var caches = new[] { cacheRoots.Program, cacheRoots.User }.Select(Physical).Distinct(comparison).ToArray();
        var pending = new Stack<string>();
        // A chosen root is taken by its physical path: a link or junction and its target are one folder, and a root inside the
        // platform cache, the recycle bin or another skipped folder is skipped as their children are.
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Physical).Reverse())
            if (!Excluded(root, caches)) pending.Push(root);
        var count = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (!seen.Add(Path.TrimEndingDirectorySeparator(folder))) continue;
            if (!OperatingSystem.IsWindows() && SkippedLinux.Any(x => folder == x || folder.StartsWith(x + "/", StringComparison.Ordinal))) continue;
            visited?.Invoke(++count);
            FoundBase? found = null;
            List<string> children;
            try
            {
                // One look-up per folder, not a listing of its files. Windows compares names without case; on Linux 1C writes 1Cv8.1CD.
                var file = new[] { "1Cv8.1CD", "1cv8.1cd", "1CV8.1CD" }.Select(x => Path.Combine(folder, x)).FirstOrDefault(File.Exists);
                if (file is not null) { var info = new FileInfo(file); found = new(Path.TrimEndingDirectorySeparator(folder), info.Length, info.LastWriteTime); }
                children = found is null ? Directory.EnumerateDirectories(folder, "*", options).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (found is not null) { yield return found; continue; }
            // Children are real folders (links are not listed), so their paths are physical: the cache is recognised by its path too,
            // also when it was moved elsewhere through a link.
            foreach (var child in children.Where(x => !SkippedNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) && !PlatformCache(x) &&
                         !caches.Any(cache => x.Equals(cache, comparison == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                         .OrderByDescending(x => x, StringComparer.CurrentCultureIgnoreCase))
                pending.Push(child);
        }
    }

    private static string Physical(string path) => BaseDuplicate.Physical(Path.GetFullPath(path));

    private static bool Excluded(string folder, IEnumerable<string> caches)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (caches.Any(cache => folder.Equals(cache, comparison) || folder.StartsWith(cache + Path.DirectorySeparatorChar, comparison))) return true;
        for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
            if (SkippedNames.Contains(Path.GetFileName(current), StringComparer.OrdinalIgnoreCase) || PlatformCache(current)) return true;
        return false;
    }

    // The cache of 1C of any user («…/1C/1cv8», on Linux «…/.1cv8/1C/1cv8») and its configuration caches.
    private static bool PlatformCache(string folder) =>
        Path.GetFileName(folder).Equals("cgf-cache", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(folder).Equals("1cv8", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(Path.GetDirectoryName(folder) ?? "").Equals("1C", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders where the search starts by default: the fixed drives on Windows, the home folder and /media, /mnt elsewhere.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
            return [.. DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Select(x => x.RootDirectory.FullName)];
        return [.. new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/media", "/mnt" }.Where(Directory.Exists)];
    }

    /// <summary>The physical folders of the file bases of the list: a base reached through a link is the same base.</summary>
    public static HashSet<string> ListedFolders(IEnumerable<BaseEntry> bases)
    {
        var result = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in bases.Where(x => x.Kind == ConnectionKind.File && Path.IsPathFullyQualified(x.Location)))
        {
            try { result.Add(Path.TrimEndingDirectorySeparator(Physical(entry.Location))); result.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Location))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Whether a folder is already a file base of the list.</summary>
    public static bool Listed(IEnumerable<BaseEntry> bases, string folder) => Listed(ListedFolders(bases), folder);

    public static bool Listed(HashSet<string> listed, string folder)
    {
        try { return listed.Contains(Path.TrimEndingDirectorySeparator(Physical(folder))) || listed.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The name of a new record, as the original's «Правила добавления»: an optional prefix, the folder name and
    /// optionally the date of the last change.</summary>
    public static string Name(FoundBase found, string prefix, bool date)
    {
        var name = Path.GetFileName(found.Folder);
        if (name.Length == 0) name = found.Folder;
        return (prefix + name + (date ? " " + found.Modified.ToString("dd.MM.yyyy") : "")).Trim();
    }
}
