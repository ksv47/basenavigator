namespace StartManager.Core;

/// <summary>A common list of 1CEStart (CommonInfoBases= of 1CEStart.cfg) as this load saw it: Bases is how many of its bases are
/// shown; Problem says why the file gave none (not found, read-only and skipped, not readable), null when it was read.</summary>
public sealed record CommonListState(string Path, int Bases, string? Problem);

/// <summary>A base of a common list: shown and started, never written. ListPath is the file it was read from; PersistentId tells
/// whether its ID is written in the record (else it is computed and changes with the record).</summary>
public sealed record CommonBase(BaseEntry Entry, string ListPath, bool PersistentId);

public sealed record CommonLoad(IReadOnlyList<CommonBase> Bases, IReadOnlyList<CommonListState> Lists)
{
    public static readonly CommonLoad Empty = new([], []);
}

/// <summary>
/// The original's «Общие списки баз»: the lists 1CEStart itself shows besides its own one. They are named by CommonInfoBases= of the
/// user's 1CEStart.cfg, then of the common one, then of each file named by CommonCfgLocation=; a relative name is taken from the folder
/// of its 1CEStart.cfg. Nothing is stored of them in the profile and nothing is ever written to them: their bases go into the tree after
/// the bases of the open list, and a base whose ID is already shown (the open list, an earlier common list) is left out, as the
/// original does. The open list is filtered out when the tree is built, not here: it may be switched or changed after the load. «Игнорировать файлы … „Только чтение“» leaves out a whole file with that attribute.
/// </summary>
public static class CommonLists
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The common lists in the order 1CEStart reads them, without repeats; files that do not exist are kept (the page shows them).
    /// <paramref name="unreadable"/> gets the settings files that could not be read: one of <paramref name="startConfigs"/> that exists
    /// but is not readable, and any file named by CommonCfgLocation that is missing or not readable (a share that is offline). The
    /// list is then incomplete, which matters to whoever removes what no list names (the cache of «Отсутствующие конфигурации»).</summary>
    public static IReadOnlyList<string> Discover(IEnumerable<string>? startConfigs = null, ICollection<string>? unreadable = null)
    {
        var configs = (startConfigs ?? PlatformCatalog.StartConfigFiles()).Select(Path.GetFullPath).ToList();
        var initial = configs.Count;
        var seen = new HashSet<string>(PathComparer);
        // A standard file that does not exist is simply absent, unless a CommonCfgLocation names it too.
        var absent = new HashSet<string>(PathComparer);
        var result = new List<string>();
        // CommonCfgLocation of the files read first adds further files once; a loop of references ends on the repeat.
        for (var index = 0; index < configs.Count; index++)
        {
            var config = configs[index];
            if (!seen.Add(config))
            {
                if (index >= initial && absent.Remove(config)) unreadable?.Add(config);
                continue;
            }
            string text;
            // ReadAllText detects the BOM: the user's file is UTF-8, the common one written by the installer UTF-16.
            try { text = File.ReadAllText(config); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && index < initial) { absent.Add(config); continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { unreadable?.Add(config); continue; }
            var folder = Path.GetDirectoryName(config) ?? "";
            foreach (var value in Values(text, "CommonInfoBases"))
                if (Resolve(value, folder) is { } list && !result.Contains(list, PathComparer)) result.Add(list);
            foreach (var value in Values(text, "CommonCfgLocation"))
                if (Resolve(value, folder) is { } location)
                    configs.Add(Directory.Exists(location) ? Path.Combine(location, Path.GetFileName(config)) : location);
        }
        return result;
    }

    /// <summary>The common lists a copy of a list must never replace: those loaded before (<paramref name="known"/>) and those the
    /// settings of 1CEStart name now. A settings file that cannot be read may name others, so then nothing is written.</summary>
    public static IReadOnlyList<string> GuardedFromCopy(IEnumerable<string> known, IEnumerable<string>? startConfigs = null)
    {
        var unreadable = new List<string>();
        var found = Discover(startConfigs, unreadable);
        if (unreadable.Count > 0)
            throw new IOException("Настройки 1CEStart не прочитаны (" + string.Join(", ", unreadable) +
                "): выбранный файл может оказаться общим списком, а он не перезаписывается. Повторите, когда настройки станут доступны.");
        return [.. known.Concat(found).Distinct(PathComparer)];
    }

    /// <summary>The list file the properties of a base show at their bottom, as the original's FV8BasesFile: the common list of a
    /// common base, else the open .v8i (<paramref name="openList"/>, also for a new base, which is written there). The local list of
    /// the profile is no .v8i: null, no line.</summary>
    public static string? ListFileOf(CommonBase? common, string? openList) =>
        common?.ListPath ?? (string.IsNullOrEmpty(openList) ? null : openList);

    private static IEnumerable<string> Values(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index > 0 && line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) yield return line[(index + 1)..].Trim();
        }
    }

    // 1CEStart writes the name without quotes; quotes typed by hand are taken off. Environment variables are not expanded by 1CEStart.
    private static string? Resolve(string value, string folder)
    {
        var name = value.Trim().Trim('"').Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try { return Path.GetFullPath(Path.IsPathFullyQualified(name) ? name : Path.Combine(folder, name)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Reads the lists for the tree: every base of each list, except one whose ID an earlier list already gave.</summary>
    /// <summary>The own «Дополнительные параметры» of a base of a common list (null: the string of the list) put into the settings of the
    /// program, under the source of that list; the list itself is never written. The source keeps every other local setting of the
    /// base (comment, authentication, platform rule…, saved when the same file was open as the list): only AdditionalParameters of its
    /// record changes, and a base without one gets a record only for a string of its own (Grok audit 27.10, P1). The same instance
    /// comes back when nothing changes. A record without ID= is known by a hash of its text, which another program changes: such a
    /// base gets no string of its own, as it gets no favorite (Grok audit 27.10, P2).</summary>
    public static Library WithParameters(Library profile, string settingsPath, CommonBase common, string? parameters)
    {
        var key = ActivityStore.Source(settingsPath, common.ListPath);
        profile.Sources.TryGetValue(key, out var saved);
        var existing = saved?.Bases.FirstOrDefault(x => x.Id == common.Entry.Id);
        if (existing?.AdditionalParameters == parameters) return profile;
        if (!common.PersistentId)
            throw new InvalidOperationException($"В записи общего списка {common.ListPath} нет постоянного ID: собственные дополнительные параметры " +
                "для этой базы не сохраняются. Задайте их в самом списке.");
        var bases = saved?.Bases.ToList() ?? [];
        var record = (existing ?? common.Entry) with { AdditionalParameters = parameters };
        var index = bases.FindIndex(x => x.Id == common.Entry.Id);
        if (index >= 0) bases[index] = record; else bases.Add(record);
        var sources = new Dictionary<string, SourceSettings>(profile.Sources) { [key] = (saved ?? new SourceSettings()) with { Bases = bases } };
        return profile with { Sources = sources };
    }

    /// <param name="profile">The settings of the program with <paramref name="settingsPath"/>: a base of a common list gets the local
    /// settings saved for it under the source of its list (the own «Дополнительные параметры» of a list that is never written).</param>
    public static CommonLoad Load(IEnumerable<string> lists, bool ignoreReadOnly, Library? profile = null, string? settingsPath = null)
    {
        var ids = new HashSet<Guid>();
        var bases = new List<CommonBase>();
        var states = new List<CommonListState>();
        foreach (var path in lists)
        {
            try
            {
                if (!File.Exists(path)) { states.Add(new(path, 0, "файл не найден")); continue; }
                if (ignoreReadOnly && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) { states.Add(new(path, 0, "только чтение, не загружается")); continue; }
                var store = new V8iStore(path);
                var local = profile is not null && settingsPath is not null &&
                    profile.Sources.TryGetValue(ActivityStore.Source(settingsPath, path), out var saved) ? new Library { Bases = [.. saved.Bases] } : new Library();
                var read = store.Load(local);
                var added = 0;
                foreach (var entry in read.Bases)
                    if (ids.Add(entry.Id)) { bases.Add(new(entry, path, store.HasPersistentId(entry.Id))); added++; }
                states.Add(new(path, added, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            { states.Add(new(path, 0, "не прочитан: " + LegacyImport.ErrorMessage(ex))); }
        }
        return new(bases, states);
    }
}
