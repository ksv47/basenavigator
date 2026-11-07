using System.Text.RegularExpressions;

namespace StartManager.Core;

/// <summary>The IDs of the known bases; Unavailable names the sources that could not be read (then nothing is removed).</summary>
public sealed record OrphanKnowledge(HashSet<Guid> Ids, IReadOnlyList<string> Unavailable)
{
    public bool Complete => Unavailable.Count == 0;
}

/// <summary>The cache of a configuration no known base uses: its folders in the platform's cache and settings roots.</summary>
public sealed record OrphanCacheEntry(Guid Id, IReadOnlyList<string> ProgramFolders, IReadOnlyList<string> UserFolders, long Size, DateTime? Modified)
{
    public IEnumerable<string> Folders => ProgramFolders.Concat(UserFolders);
}

/// <summary>
/// The original's «Отсутствующие конфигурации»: folders of the 1C cache named by a GUID that no known base has. A base is known
/// when its ID is in the open list, in any list remembered in the profile, in the standard 1CEStart list or a common list of
/// 1CEStart.cfg, or when the platform mapped its connection string to that GUID (1cv8u.pfl). Folders not named by a GUID
/// (Srvr__…__Ref__… of old platforms and the platform's own) are never reported.
/// </summary>
public static class OrphanCache
{
    private static readonly Regex GuidName = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex AnyGuid = new(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant);

    /// <summary>Every GUID a base known to this computer may use for its cache, and the sources that could not be read.
    /// The lists remembered in the profile are read again from their files: another window or program may have added a base
    /// there. A source that is configured but cannot be read (a list on a disconnected share, a locked file, a folder this
    /// user may not read) makes the result incomplete, and nothing may then be removed.</summary>
    /// <param name="profile">The profile file of the window: it is read again and joined with <paramref name="library"/>, so a source or
    /// a local base another copy of the program has saved there since the window loaded it counts too (Codex re-check 5, RC5-2). A
    /// profile that exists but cannot be read makes the result incomplete.</param>
    public static OrphanKnowledge KnownIds(Library library, IEnumerable<string>? lists = null, IEnumerable<string>? startConfigs = null, BaseCacheRoots? roots = null,
        string? profile = null)
    {
        roots ??= BaseCacheRoots.Current;
        var unavailable = new List<string>();
        var libraries = new List<Library> { library };
        if (profile is not null)
            // Only a missing profile is absent; anything else that keeps it from being read leaves the knowledge incomplete.
            try { if (LibraryStore.ReadExisting(profile) is { } stored) libraries.Add(stored); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            { unavailable.Add(profile); }
        var known = new HashSet<Guid>();
        var optional = OptionalFolders(roots);
        var files = new List<string>(lists ?? [V8iStore.StandardPath]);
        foreach (var one in libraries)
        {
            known.UnionWith(one.Bases.Select(x => x.Id));
            foreach (var source in one.Sources.Values) known.UnionWith(source.Bases.Select(x => x.Id));
            if (one.ListPath.Length > 0) files.Add(one.ListPath);
            files.AddRange(one.Sources.Keys.Where(x => x.StartsWith("v8i:", StringComparison.Ordinal)).Select(x => x[4..]));
        }
        // The same lists the tree shows: CommonCfgLocation followed, relative names and quotes resolved; a settings file that cannot
        // be read makes the knowledge incomplete.
        files.AddRange(Core.CommonLists.Discover(startConfigs ?? PlatformCatalog.StartConfigFiles(), unavailable));
        // Any GUID in a list counts: an ID line, a group or a record the program cannot open itself.
        foreach (var file in files.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            if (ReadText(file, unavailable, optional) is { } text) AddGuids(known, text);
        foreach (var map in roots.Shared ? [roots.User] : new[] { roots.User, roots.Program })
            if (ReadText(Path.Combine(map, "1cv8u.pfl"), unavailable, optional) is { } text) AddGuids(known, text);
        return new(known, unavailable);
    }

    /// <summary>The lists of CommonInfoBases in the 1CEStart settings.</summary>
    public static IEnumerable<string> CommonLists(IEnumerable<string> startConfigs)
    {
        var ignored = new List<string>();
        foreach (var file in startConfigs)
            if (ReadText(file, ignored, []) is { } text) foreach (var list in CommonListsOf(text)) yield return list;
    }

    private static IEnumerable<string> CommonListsOf(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index > 0 && line[..index].Trim().Equals("CommonInfoBases", StringComparison.OrdinalIgnoreCase))
                yield return line[(index + 1)..].Trim();
        }
    }

    // A missing file in an existing folder is simply absent (a list never created, a remembered list that was deleted).
    // A file that exists but cannot be read, or a folder that cannot be reached, is unknown: the caller must not remove.
    private static string? ReadText(string file, List<string> unavailable, IReadOnlyCollection<string> optional)
    {
        // The file is read right away: File.Exists and Directory.Exists answer «no» also when access is denied, which must
        // not pass for «absent». Only a missing file, or a missing folder of 1C that is created on its first run, is absent.
        try { return File.ReadAllText(file); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));
            for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
                if (optional.Contains(Path.TrimEndingDirectorySeparator(current))) return null;
            unavailable.Add(file); return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { unavailable.Add(file); return null; }
    }

    // Folders of 1C that simply do not exist before the platform or its starter were ever run: their absence proves nothing.
    private static HashSet<string> OptionalFolders(BaseCacheRoots roots)
    {
        var result = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var folder in new[] { Path.GetDirectoryName(V8iStore.StandardPath), roots.User, roots.Program }
                     .Concat(PlatformCatalog.StartConfigFiles().Select(Path.GetDirectoryName)))
            if (folder is not null) result.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return result;
    }

    private static void AddGuids(HashSet<Guid> known, string text)
    {
        foreach (Match match in AnyGuid.Matches(text)) known.Add(Guid.Parse(match.Value));
    }

    /// <summary>The GUID folders of the cache and settings roots (and their version subfolders) that belong to no known base.</summary>
    public static IReadOnlyList<OrphanCacheEntry> Find(OrphanKnowledge knowledge, BaseCacheRoots? roots = null, CancellationToken cancel = default) =>
        Find(knowledge.Ids, roots, cancel);

    public static IReadOnlyList<OrphanCacheEntry> Find(HashSet<Guid> known, BaseCacheRoots? roots = null, CancellationToken cancel = default)
    {
        roots ??= BaseCacheRoots.Current;
        var program = Folders(roots.Program, known); var user = roots.Shared ? [] : Folders(roots.User, known);
        var result = new List<OrphanCacheEntry>();
        foreach (var id in program.Keys.Union(user.Keys).Order())
        {
            cancel.ThrowIfCancellationRequested();
            var p = program.GetValueOrDefault(id) ?? []; var u = user.GetValueOrDefault(id) ?? [];
            long size = 0; DateTime? modified = null;
            foreach (var folder in p.Concat(u))
            {
                size += BaseCache.Size(folder, p.Contains(folder) ? ProgramKind(roots) : BaseCacheKind.UserSettings, roots);
                try { var time = Directory.GetLastWriteTime(folder); if (modified is null || time > modified) modified = time; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            result.Add(new(id, p, u, size, modified));
        }
        return result;
    }

    /// <summary>
    /// Removes the folders of the orphans whole, settings included, as the question before it says. When settings and cache share a
    /// folder (Linux) the program folder is the settings folder too: it goes whole, not cleared down to *.pfl and *.reg as the cache
    /// of a known base is (there the settings stay, and an emptied folder would make the row come back).
    /// </summary>
    public static BaseCacheResult Clear(IEnumerable<OrphanCacheEntry> orphans, BaseCacheRoots? roots = null)
    {
        roots ??= BaseCacheRoots.Current;
        var chosen = orphans.ToList();
        var results = new[]
        {
            BaseCache.Clear(chosen.SelectMany(x => x.ProgramFolders).Where(Directory.Exists), ProgramKind(roots), roots),
            BaseCache.Clear(chosen.SelectMany(x => x.UserFolders).Where(Directory.Exists), BaseCacheKind.UserSettings, roots),
        };
        return new(results.Sum(x => x.Removed), [.. results.SelectMany(x => x.Failed)]);
    }

    /// <summary>
    /// The deletion of «Отсутствующие конфигурации» after the confirmation: the profile is read again, the chosen orphans are
    /// classified and the still unknown ones removed, all under the lock of the profile file that <see cref="LibraryStore.Save"/>
    /// takes, so another window cannot save a source or a base between the decision and the removal (Codex re-check 6, RC6-3):
    /// its save waits or is refused as for any held profile. The lock is waited for about 1.5 s, then refused with a reason;
    /// knowledge that is not complete refuses too (IOException with what could not be read). The folder of a profile not saved
    /// yet is created for the lock; when it cannot be made or locked, nothing is removed.
    /// </summary>
    public static BaseCacheResult ClearUnknown(IEnumerable<OrphanCacheEntry> chosen, Library library, string profile, IEnumerable<string>? lists = null,
        IEnumerable<string>? startConfigs = null, BaseCacheRoots? roots = null) => ClearUnknown(chosen, library, profile, lists, startConfigs, roots, null);

    /// <param name="decided">Tests: called under the lock between the classification and the removal.</param>
    internal static BaseCacheResult ClearUnknown(IEnumerable<OrphanCacheEntry> chosen, Library library, string profile, IEnumerable<string>? lists,
        IEnumerable<string>? startConfigs, BaseCacheRoots? roots, Action? decided)
    {
        // The folder of a profile not saved yet is made first, so the lock exists before the decision also on the first run: a
        // first save of another window waits for it as any other (Codex re-check 7, RC7-2). No folder, no lock — no removal.
        FileStream gate;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(profile)) ?? throw new IOException($"Нет каталога профиля {profile}."));
            gate = FilePersistence.Lock(profile);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException)
        {
            throw new IOException($"Профиль {profile} недоступен для блокировки ({ex.Message}); кэш не удаляется.", ex);
        }
        using (gate)
        {
            var known = KnownIds(library, lists, startConfigs, roots, profile);
            if (!known.Complete)
                throw new IOException("Не удалось прочитать: " + string.Join("; ", known.Unavailable.Take(3)) + ". Пока эти списки или профиль недоступны, кэш не удаляется.");
            var still = chosen.Where(x => !known.Ids.Contains(x.Id)).ToList();
            decided?.Invoke();
            return Clear(still, roots);
        }
    }

    // With one shared folder an orphan's folder is both cache and settings: counted and removed whole.
    private static BaseCacheKind ProgramKind(BaseCacheRoots roots) => roots.Shared ? BaseCacheKind.UserSettings : BaseCacheKind.Program;

    private static Dictionary<Guid, List<string>> Folders(string root, HashSet<Guid> known)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (!Directory.Exists(root)) return result;
        var parents = new List<string> { root };
        try { parents.AddRange(Directory.GetDirectories(root).Where(x => Regex.IsMatch(Path.GetFileName(x), @"^\d+(\.\d+){1,3}$"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var parent in parents)
        {
            string[] folders;
            try { folders = Directory.GetDirectories(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                // A link is not the platform's own folder: it is left alone.
                if (!GuidName.IsMatch(name) || new DirectoryInfo(folder).LinkTarget is not null) continue;
                var id = Guid.Parse(name);
                if (known.Contains(id)) continue;
                if (!result.TryGetValue(id, out var list)) result[id] = list = [];
                list.Add(folder);
            }
        }
        return result;
    }
}
