namespace StartManager.Core;

/// <summary>An installed template of a configuration or a demonstration base: the folder <c>vendor\solution\version</c> of a templates
/// catalog and a file a base is made from (.cf or .dt). A version folder with several such files gives several templates.</summary>
/// <param name="Alternatives">The folder has more than one file to make a base from: the name of the file tells them apart.</param>
/// <param name="Platform">The platform the manifest names (<c>AppVersion</c>), e.g. 8.3; empty without a manifest. Only shown: 1C itself decides
/// (a template of 8.5 said 8.3 in its manifest).</param>
public sealed record InfobaseTemplate(string Vendor, string Solution, string Version, string File, string Folder, bool Alternatives = false, string Platform = "")
{
    public string Title => $"{Vendor} · {Solution} · {Label}";
    /// <summary>The version, with the file name when the folder has several files.</summary>
    public string Label => Alternatives ? $"{Version} ({Path.GetFileName(File)})" : Version;
}

/// <param name="Truncated">The search stopped at its limit: not every template is listed.</param>
public sealed record TemplateScan(IReadOnlyList<InfobaseTemplate> Templates, bool Truncated);

/// <summary>The templates the platform installed (the original's «Создать информационную базу из шаблона»). The catalogs are those of
/// <c>ConfigurationTemplatesLocation</c> in 1CEStart settings and the default one (<c>%APPDATA%\1C\1cv8\tmplts</c>, on Linux
/// <c>~/.1cv8/1C/1cv8/tmplts</c>). The layout is vendor → solution → version, as the documentation of the platform describes it. The
/// manifest <c>1cv8.mft</c> of a version folder names its templates (see <see cref="FromManifest"/>); without one the names are the names
/// of the folders (the version is written with underscores there) and every .cf and .dt is a template.</summary>
public static class TemplateCatalog
{
    /// <summary>Templates listed at most.</summary>
    private const int Limit = 5000;
    /// <summary>Folders looked into at most: the work is bounded by this, not only by the number of results (empty trees, slow shares).</summary>
    private const int VisitLimit = 20000;

    public static IEnumerable<string> Roots(IEnumerable<string>? configFiles = null)
    {
        var configured = PlatformCatalog.StartConfigValues(configFiles ?? PlatformCatalog.StartConfigFiles(), "ConfigurationTemplatesLocation")
            .Select(x => OperatingSystem.IsWindows() ? Environment.ExpandEnvironmentVariables(x.Trim().Trim('"')) : x.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified);
        string? standard = null;
        if (OperatingSystem.IsWindows())
        { if (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } data) standard = Path.Combine(data, "1C", "1cv8", "tmplts"); }
        else if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home) standard = Path.Combine(home, ".1cv8", "1C", "1cv8", "tmplts");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return configured.Concat(standard is null ? [] : [standard]).Select(Path.GetFullPath).Distinct(comparer).ToArray();
    }

    /// <summary>The templates of the catalogs, grouped by vendor and solution, the newest version first (see <see cref="ScanAll"/>).</summary>
    public static IReadOnlyList<InfobaseTemplate> Scan(IEnumerable<string>? roots = null) => ScanAll(roots).Templates;

    /// <summary>The same with a cancellation token and the signal that the limit was reached. The walk is read-only and stops as a whole at
    /// <c>VisitLimit</c> entries looked at (folders of every level, skipped links and files alike) or <c>Limit</c> templates; an unreadable
    /// folder is skipped. Cancellation is checked at every entry; one slow call of the file system itself cannot be interrupted.</summary>
    public static TemplateScan ScanAll(IEnumerable<string>? roots = null, CancellationToken cancel = default, int limit = Limit, int visitLimit = VisitLimit)
    {
        var found = new List<InfobaseTemplate>();
        var visited = 0; var truncated = false;
        bool Full() { if (found.Count >= limit) { truncated = true; return true; } return false; }
        // An entry that was there but is not looked at: the walk ran out of its budget.
        bool Spend() { cancel.ThrowIfCancellationRequested(); if (visited >= visitLimit) { truncated = true; return false; } visited++; return true; }
        // Every entry of the folder is charged, the kind not wanted here as well: the type is told after the entry is counted.
        IEnumerable<string> Entries(string path, bool files)
        {
            IEnumerator<FileSystemInfo> enumerator;
            try { enumerator = new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { yield break; }
            using (enumerator)
                while (true)
                {
                    FileSystemInfo child;
                    try
                    {
                        if (!enumerator.MoveNext()) yield break;
                        child = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
                    if (!Spend()) yield break;
                    if (files ? child is FileInfo : child is DirectoryInfo && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                        yield return child.FullName;
                }
        }
        foreach (var root in roots ?? Roots())
        {
            cancel.ThrowIfCancellationRequested();
            if (Full() || visited >= visitLimit) { truncated = true; break; }
            foreach (var vendor in Entries(root, false))
            {
                if (Full()) break;
                foreach (var solution in Entries(vendor, false))
                {
                    if (Full()) break;
                    foreach (var version in Entries(solution, false))
                    {
                        if (Full()) break;
                        var entries = Entries(version, true).ToList();
                        // The manifest names the templates of the folder as the platform shows them; without one (or with nothing usable
                        // in it) every .cf and .dt is a template (1cv8.cf and 1cv8.dt first). An update (.cfu) cannot make a base.
                        var listed = entries.FirstOrDefault(x => Path.GetFileName(x).Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) is { } manifest
                            ? FromManifest(manifest, version, entries) : [];
                        if (listed.Count == 0)
                        {
                            var files = entries.Where(Makes).ToList();
                            files.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : StringComparer.OrdinalIgnoreCase.Compare(a, b));
                            listed = [.. files.Select(file => new InfobaseTemplate(Path.GetFileName(vendor), Path.GetFileName(solution),
                                Path.GetFileName(version).Replace('_', '.'), file, version, files.Count > 1))];
                        }
                        foreach (var template in listed)
                        {
                            if (Full()) break;
                            found.Add(template);
                        }
                    }
                }
            }
        }
        var ordered = found.OrderBy(x => x.Vendor, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Solution, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(x => Numbers(x.Version), Comparer<long[]>.Create(Compare)).ThenBy(x => x.Folder, StringComparer.Ordinal)
            .ThenBy(x => Rank(x.File)).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(ordered, truncated);
    }

    private const string ManifestName = "1cv8.mft";
    /// <summary>A manifest larger than this is not a manifest: it is not read.</summary>
    private const int ManifestLimit = 64 * 1024;

    private static bool Makes(string file) => Path.GetExtension(file).Equals(".cf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".dt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The templates of a manifest (seen in the templates of 1C, e.g. the standard subsystems library):
    /// <c>Vendor=…</c>, <c>Version=…</c>, then a section <c>[ConfigN]</c> for each template with <c>Catalog=group/name</c> (its place in the
    /// tree of the platform) and <c>Source=file</c> (relative to the folder). A section whose file is not one of the folder's .cf or .dt is left out.</summary>
    internal static IReadOnlyList<InfobaseTemplate> FromManifest(string manifest, string folder, IReadOnlyCollection<string> files)
    {
        string text;
        try
        {
            if (new FileInfo(manifest).Length > ManifestLimit) return [];
            using var reader = new StreamReader(manifest, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        var head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<Dictionary<string, string>>();
        var current = head;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (line[1..^1].Trim().StartsWith("Config", StringComparison.OrdinalIgnoreCase)) sections.Add(current);
                continue;
            }
            if (line.IndexOf('=') is > 0 and var split) current[line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        var version = head.GetValueOrDefault("Version") is { Length: > 0 } v ? v : Path.GetFileName(folder).Replace('_', '.');
        var vendor = head.GetValueOrDefault("Vendor") is { Length: > 0 } named ? named : "";
        var platform = head.GetValueOrDefault("AppVersion") ?? "";
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<(string Group, string Name, string File)>();
        foreach (var section in sections)
        {
            if (section.GetValueOrDefault("Source") is not { Length: > 0 } source || section.GetValueOrDefault("Catalog") is not { Length: > 0 } catalog) continue;
            // Only a file of this very folder: a name with a folder in it (or ..) is not followed.
            if (source.IndexOfAny(['/', '\\']) >= 0 || files.FirstOrDefault(x => comparer.Equals(Path.GetFileName(x), source)) is not { } file || !Makes(file)) continue;
            var parts = catalog.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            result.Add(parts.Length == 1 ? (vendor.Length > 0 ? vendor : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(folder))) ?? "", parts[0], file)
                : (parts[0], string.Join(" / ", parts[1..]), file));
        }
        // The same file under the same name twice is one template; under two names it is two.
        result = [.. result.Distinct()];
        return [.. result.Select(x => new InfobaseTemplate(x.Group, x.Name, version, x.File, folder,
            result.Count(y => y.Group == x.Group && y.Name == x.Name) > 1, platform))];
    }

    private static int Rank(string file) => Path.GetFileName(file).ToLowerInvariant() switch { "1cv8.cf" => 0, "1cv8.dt" => 1, _ => Path.GetExtension(file).Equals(".cf", StringComparison.OrdinalIgnoreCase) ? 2 : 3 };

    private static long[] Numbers(string version) => [.. version.Split('.').Select(x => long.TryParse(x, out var n) ? n : -1)];
    private static int Compare(long[]? a, long[]? b)
    {
        a ??= []; b ??= [];
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
