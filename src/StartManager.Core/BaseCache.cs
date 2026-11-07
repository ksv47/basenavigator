using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;

namespace StartManager.Core;

/// <summary>Where the 1C platform keeps the per-base folders: the program cache and the user settings.</summary>
public enum BaseCacheKind { Program, UserSettings }

/// <summary>
/// Windows: the program cache in %LOCALAPPDATA%\1C\1cv8, the user settings in %APPDATA%\1C\1cv8.
/// Linux: both in ~/.1cv8/1C/1cv8, where a base folder mixes settings (*.pfl) and cache.
/// </summary>
public sealed record BaseCacheRoots(string Program, string User)
{
    public static BaseCacheRoots Current => OperatingSystem.IsWindows()
        ? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1C", "1cv8"),
              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1cv8"))
        : new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".1cv8", "1C", "1cv8"),
              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".1cv8", "1C", "1cv8"));
    /// <summary>Settings and cache share one folder: clearing the cache keeps the settings files.</summary>
    public bool Shared => string.Equals(Path.GetFullPath(Program).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(User).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public string Root(BaseCacheKind kind) => kind == BaseCacheKind.Program ? Program : User;

    /// <summary>The cache families of the original's «Версия» (FormCLearCache): 8.3 and newer in 1cv8, 8.2 in 1cv82, 8.1 in 1cv81.</summary>
    public static readonly string[] Families = ["8.3", "8.2", "8.1"];

    /// <summary>The roots of a family for a profile folder (another account, Windows) or for this user (null). Linux has only 1cv8.</summary>
    public static BaseCacheRoots? For(string family, string? profile = null)
    {
        var folder = family switch { "8.2" => "1cv82", "8.1" => "1cv81", _ => "1cv8" };
        if (!OperatingSystem.IsWindows())
            return profile is null && folder == "1cv8" ? Current : null;
        var local = profile is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Combine(profile, "AppData", "Local");
        var roaming = profile is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(profile, "AppData", "Roaming");
        // The folders above the roots must not be links either: a Local\1C made a junction to Roaming\1C would let the program cache
        // clearing remove the user settings (Inside guards only from the root down). Such a profile is skipped.
        string[] above = profile is null ? [local, Path.Combine(local, "1C"), roaming, Path.Combine(roaming, "1C")]
            : [Path.Combine(profile, "AppData"), local, Path.Combine(local, "1C"), roaming, Path.Combine(roaming, "1C")];
        if (above.Any(LinkedFolder)) return null;
        return new(Path.Combine(local, "1C", folder), Path.Combine(roaming, "1C", folder));
    }

    private static bool LinkedFolder(string path)
    {
        try { return Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}

/// <summary>An account of the computer whose 1C cache may be cleared: its name and profile folder.</summary>
public sealed record CacheAccount(string Name, string Profile, bool Current);

/// <summary>
/// The original's «Учетные записи» of «Очистка временных файлов»: the profiles of the computer. Windows only; the folders come from
/// the registry (ProfileList), not guessed; only the accounts of people (S-1-5-21-…), whose profile folder exists. Another account's
/// folders are read and cleared only by an administrator, and only its program cache (decision of the user 11.10.2026).
/// </summary>
public static class CacheAccounts
{
    public static IReadOnlyList<CacheAccount> List()
    {
        var own = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var result = new List<CacheAccount>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                foreach (var sid in list?.GetSubKeyNames() ?? [])
                {
                    if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                    using var key = list!.OpenSubKey(sid);
                    if (key?.GetValue("ProfileImagePath") is not string raw) continue;
                    var path = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(raw));
                    if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) continue;
                    result.Add(new(Path.GetFileName(path), path, path.Equals(own, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        if (!result.Any(x => x.Current)) result.Insert(0, new(Environment.UserName, own, true));
        return [.. result.OrderByDescending(x => x.Current).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}

public sealed record BaseCacheResult(int Removed, IReadOnlyList<string> Failed);

/// <summary>
/// Folders of one base, named as the platform names them: the ID from the .v8i list for a base started from the list,
/// the GUID from IdConnStrMap of 1cv8u.pfl for a base started by its connection string, and Srvr__…__Ref__…__ of old platforms.
/// </summary>
public static class BaseCache
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    // Pairs {"S","<connection string>"},{"S","<GUID>"}; quotes inside the connection string are doubled.
    private static readonly Regex MapPair = new(@"\{\s*""S""\s*,\s*""(?<conn>(?:[^""]|"""")*)""\s*\}\s*,\s*\{\s*""S""\s*,\s*""(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})""\s*\}",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Names(BaseEntry entry, bool persistentId, BaseCacheRoots roots)
    {
        var names = new List<string>();
        void Add(string name) { if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name); }
        if (persistentId) Add(entry.Id.ToString("D"));
        foreach (var guid in MapGuids(entry, Path.Combine(roots.User, "1cv8u.pfl"))) Add(guid);
        if (entry.Kind == ConnectionKind.Server && SplitServer(entry.Location) is var (server, reference))
        {
            Add($"Srvr__{Sanitize(server)}__Ref__{Sanitize(reference)}__");
            // Only the documented equivalence: no port means the default port 1541. Another port is another cluster.
            if (Cluster(server) is var (host, port) && port == DefaultPort)
                foreach (var form in new[] { host, $"{host}:{DefaultPort}" })
                    Add($"Srvr__{Sanitize(form)}__Ref__{Sanitize(reference)}__");
        }
        return names;
    }

    /// <summary>Existing folders of the base in the root of the kind, directly and in version subfolders (1cv8\8.2.x\…).</summary>
    public static IReadOnlyList<string> Find(BaseEntry entry, bool persistentId, BaseCacheKind kind, BaseCacheRoots? roots = null)
    {
        roots ??= BaseCacheRoots.Current;
        var root = roots.Root(kind);
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        var names = Names(entry, persistentId, roots);
        var parents = new List<string> { root };
        try { parents.AddRange(Directory.GetDirectories(root).Where(x => Regex.IsMatch(Path.GetFileName(x), @"^\d+(\.\d+){1,3}$"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var parent in parents)
            foreach (var name in names)
            {
                var folder = Path.Combine(parent, name);
                if (Directory.Exists(folder) && Inside(root, folder)) result.Add(folder);
            }
        return result;
    }

    /// <summary>Size of what clearing removes; the settings files stay when settings and cache share a folder.</summary>
    /// <param name="selective">The original's «выборочная очистка»: the settings files (*.pfl, *.reg) stay.</param>
    public static long Size(string folder, BaseCacheKind kind, BaseCacheRoots? roots = null, bool selective = false)
    {
        var keepSettings = selective || kind == BaseCacheKind.Program && (roots ?? BaseCacheRoots.Current).Shared;
        long total = 0;
        foreach (var file in Files(folder))
            if (!(keepSettings && IsSettingsFile(file)))
                try { total += new FileInfo(file).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>
    /// Removes the folders. A whole folder is first renamed, so a folder held by a running 1C stays intact and is reported.
    /// Linux renames and removes open files, so there a folder with a file open in any visible process is skipped
    /// (<see cref="OpenFiles"/>) and reported with the holders.
    /// When settings and cache share a folder (Linux), clearing the cache removes everything except *.pfl and *.reg.
    /// </summary>
    /// <param name="selective">The original's «выборочная очистка» («с сохранением свойств профилей»): the settings files (*.pfl,
    /// *.reg) stay, everything else of the folder goes; «грубая» (false) removes the whole folder.</param>
    public static BaseCacheResult Clear(IEnumerable<string> folders, BaseCacheKind kind, BaseCacheRoots? roots = null, bool selective = false)
    {
        roots ??= BaseCacheRoots.Current;
        var keepSettings = selective || kind == BaseCacheKind.Program && roots.Shared;
        var root = Path.GetFullPath(roots.Root(kind));
        int removed = 0; var failed = new List<string>();
        // The folders above the root are checked again now: a link put there after the folders were found (the 1C folder of Local
        // made a junction to the one of Roaming) must not lead the removal into another root.
        if (LinkedAbove(root)) return new(0, [.. folders]);
        foreach (var folder in folders)
        {
            var full = Path.GetFullPath(folder);
            // Only folders of the base really inside the platform root are touched: no link on the way from the root.
            if (!Inside(root, full)) { failed.Add(folder); continue; }
            try
            {
                if (OpenFiles.Under(full) is { Count: > 0 } holders) { failed.Add(folder + " — " + OpenFiles.Describe(holders)); continue; }
                if (keepSettings) { if (RemoveExceptSettings(full)) removed++; else failed.Add(folder); continue; }
                var temporary = full + ".deleting_" + Guid.NewGuid().ToString("N");
                Directory.Move(full, temporary);
                if (!Inside(root, temporary) || LinkedAbove(root)) { failed.Add(folder); continue; }
                foreach (var file in Files(temporary))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Directory.Delete(temporary, true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(folder); }
        }
        return new(removed, failed);
    }

    private static bool RemoveExceptSettings(string folder)
    {
        var complete = true;
        foreach (var file in Files(folder).Where(x => !IsSettingsFile(x)).ToList())
            try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        foreach (var directory in Directories(folder).OrderByDescending(x => x.Length))
            try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        return complete;
    }

    /// <summary>
    /// The path lies under the root by name and by fact: the root and every folder from it down to the path exist
    /// and none is a junction or a symbolic link, so a link cannot lead the removal outside the root.
    /// </summary>
    private static bool Inside(string root, string path)
    {
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(top + Path.DirectorySeparatorChar, PathComparison)) return false;
        for (var current = full; current.Length >= top.Length; current = Path.GetDirectoryName(current) ?? "")
        {
            if (!Directory.Exists(current) || IsLink(current)) return false;
            if (current.Length == top.Length) return true;
        }
        return false;
    }

    /// <summary>A link among the folders above a root: on Windows 1C, Local/Roaming and AppData; elsewhere 1C and .1cv8 (the home folder
    /// itself may be a link on some systems and is not checked there).</summary>
    internal static bool LinkedAbove(string root)
    {
        var levels = OperatingSystem.IsWindows() ? 3 : 2;
        var current = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
        for (var i = 0; i < levels && current is not null; i++, current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && IsLink(current)) return true;
        return false;
    }

    private static bool IsSettingsFile(string file) =>
        Path.GetExtension(file).Equals(".pfl", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".reg", StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // Links are not followed: only the base's own files are counted and removed.
    private static IEnumerable<string> Directories(string folder)
    {
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.Count > 0)
        {
            string[] children;
            try { children = Directory.GetDirectories(pending.Pop()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.Where(x => !IsLink(x))) { yield return child; pending.Push(child); }
        }
    }

    private static IEnumerable<string> Files(string folder)
    {
        foreach (var directory in Directories(folder).Prepend(folder))
        {
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static IEnumerable<string> MapGuids(BaseEntry entry, string mapFile)
    {
        string text;
        try { if (!File.Exists(mapFile)) yield break; text = File.ReadAllText(mapFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (Match match in MapPair.Matches(text))
            if (Matches(match.Groups["conn"].Value.Replace("\"\"", "\""), entry)) yield return match.Groups["guid"].Value.ToLowerInvariant();
    }

    private static bool Matches(string connection, BaseEntry entry)
    {
        var parts = new DbConnectionStringBuilder();
        try { parts.ConnectionString = connection; } catch (ArgumentException) { return false; }
        string Part(string key) => parts.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
        if (entry.Kind == ConnectionKind.File)
            return Part("File") is { Length: > 0 } file && Normalize(file).Equals(Normalize(entry.Location), PathComparison);
        if (entry.Kind == ConnectionKind.Web)
            return Part("ws") is { Length: > 0 } web && BaseEntry.SameWebAddress(web, entry.Location);
        // The whole cluster address is compared; only "host" and "host:1541" are the same cluster.
        return SplitServer(entry.Location) is var (server, reference) && Part("Ref").Equals(reference, StringComparison.OrdinalIgnoreCase)
            && Cluster(Part("Srvr")) == Cluster(server);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
    private static (string Server, string Reference)? SplitServer(string location)
    {
        var slash = location.LastIndexOf('\\');
        return slash > 0 && slash < location.Length - 1 ? (location[..slash].Trim(), location[(slash + 1)..].Trim()) : null;
    }
    private const int DefaultPort = 1541;
    /// <summary>Host in lower case and port (1541 when absent). A list of hosts or an unparsable port stays distinct.</summary>
    private static (string Host, int Port) Cluster(string server)
    {
        var value = server.Trim().ToLowerInvariant();
        // A list of hosts or an unrecognised address is compared as a whole: it never proves that files are ours.
        if (value.Contains(',')) return (value, -1);
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0) return (value, -1);
            var rest = value[(end + 1)..];
            if (rest.Length == 0) return (value, DefaultPort);
            return rest.StartsWith(':') && int.TryParse(rest[1..], out var v6) ? (value[..(end + 1)], v6) : (value, -1);
        }
        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
            return int.TryParse(value[(colon + 1)..], out var port) ? (value[..colon], port) : (value, -1);
        if (colon >= 0) return (value, -1);
        return (value, DefaultPort);
    }
    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value) result.Append("\\/:*?\"<>|".Contains(c) ? '_' : c);
        return result.ToString();
    }
}
