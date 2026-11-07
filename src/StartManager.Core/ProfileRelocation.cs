namespace StartManager.Core;

/// <summary>
/// Adapts a profile that was moved or copied with a portable folder. Keys are stored with absolute paths
/// (local:&lt;settings&gt;, v8i:&lt;list&gt;); ProfilePath or the single local key in a file tells where it was written.
/// Only paths inside an old portable folder are moved; the file format stays compatible with older builds.
/// The contents of .v8i lists are never rewritten: like 1C itself they keep absolute file-base addresses.
/// </summary>
public sealed class ProfileRelocation
{
    private const string Local = "local:";
    private const string List = "v8i:";
    private readonly string currentLocal;
    private readonly string[] oldRoots;
    private readonly string? newRoot;

    /// <summary>Program folder of the portable profile; set by the application, null for other profiles.</summary>
    public static string? PortableRoot { get; set; }

    private ProfileRelocation(string currentLocal, string[] oldRoots, string? newRoot)
    { this.currentLocal = currentLocal; this.oldRoots = oldRoots; this.newRoot = newRoot; }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Null when the profile was not moved or the old location cannot be established unambiguously.</summary>
    /// <param name="savedAt">ProfilePath written with the file; older files fall back to their single local key.</param>
    public static ProfileRelocation? Detect(string? savedAt, IEnumerable<string> keys, string settingsPath)
    {
        var current = ActivityStore.Source(settingsPath, "");
        var locals = keys.Where(x => x.StartsWith(Local, StringComparison.Ordinal))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        string oldSettings;
        if (!string.IsNullOrEmpty(savedAt) && Path.IsPathFullyQualified(savedAt)) oldSettings = savedAt;
        else if (locals.Length == 1) oldSettings = locals[0][Local.Length..];
        else return null;
        if (string.Equals(Local + oldSettings, current, Comparison) ||
            locals.Any(x => !string.Equals(x, Local + oldSettings, Comparison) && !string.Equals(x, current, Comparison))) return null;
        if (PortableRoot is { } root && Path.IsPathFullyQualified(oldSettings))
        {
            // Same layout inside the folder (Settings/library.json), otherwise it is not the same portable profile.
            var relative = Path.GetRelativePath(root, Path.GetFullPath(settingsPath));
            var candidate = Path.GetDirectoryName(Path.GetDirectoryName(oldSettings));
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && candidate is not null &&
                string.Equals(Path.Combine(candidate, relative), oldSettings, Comparison) && !Broad(candidate, root))
                return new(current, [candidate], Path.GetFullPath(root));
        }
        return new(current, [], null);
    }

    /// <summary>A folder that holds far more than the program: a drive root, a home folder or a folder above the current
    /// home. Paths under it are not moved, since the lists, bases and clients there stayed where they were.
    /// A folder right in Users or home counts as a home folder unless it has the name of the program folder it became.</summary>
    internal static bool Broad(string folder, string? movedTo = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), Comparison)) return true;
        var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
        var programName = movedTo is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(movedTo)));
        if ((parent.Equals("Users", StringComparison.OrdinalIgnoreCase) || parent.Equals("home", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(Path.GetFileName(full), programName, Comparison)) return true;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return false;
        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        return string.Equals(home, full, Comparison) || home.StartsWith(full + Path.DirectorySeparatorChar, Comparison);
    }

    public string Key(string key)
    {
        if (key.StartsWith(Local, StringComparison.Ordinal)) return currentLocal;
        if (key.StartsWith(List, StringComparison.Ordinal) && Move(key[List.Length..]) is { } moved)
            return ActivityStore.Source("", moved);
        return key;
    }

    public string Relocate(string path) => Move(path) ?? path;

    private string? Move(string path)
    {
        if (newRoot is null) return null;
        foreach (var root in oldRoots)
            if (Relative(root, path) is { } relative) return Path.TrimEndingDirectorySeparator(Path.Combine(newRoot, relative));
        return null;
    }

    // Compares normalized full paths, so "..", alternative separators and the root itself are handled.
    private static string? Relative(string root, string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return null;
        string full, baseFull;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (string.Equals(full, baseFull, Comparison)) return "";
        var prefix = baseFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison) ? full[prefix.Length..] : null;
    }

    public Library Apply(Library library)
    {
        GroupFolder Folder(GroupFolder folder) => folder with { Folder = Relocate(folder.Folder) };
        BaseEntry Entry(BaseEntry entry) => entry with
        {
            Executable = Relocate(entry.Executable),
            ExternalProcessing = Relocate(entry.ExternalProcessing),
            Location = entry.Kind == ConnectionKind.File ? Relocate(entry.Location) : entry.Location,
            // A backup folder inside the moved folder moves with it (a relative one is not a path and stays).
            BackupFolder = Relocate(entry.BackupFolder),
            // A tcp:// or http:// repository is not a path and stays as it is.
            Authentication = entry.Authentication is { } auth ? auth with { RepositoryPath = Relocate(auth.RepositoryPath) } : null
        };
        var sources = new Dictionary<string, SourceSettings>();
        foreach (var (key, value) in library.Sources)
        {
            var target = Key(key);
            // A key that already exists at the new place is newer; the moved copy never overwrites it.
            if (sources.ContainsKey(target) && target != key) continue;
            sources[target] = value with { Bases = [.. value.Bases.Select(Entry)], GroupFolders = [.. value.GroupFolders.Select(Folder)] };
        }
        return library with
        {
            ListPath = Relocate(library.ListPath), Executable = Relocate(library.Executable),
            // A platform folder inside the moved program folder moves with it; any other one stays.
            PlatformFolders = [.. library.PlatformFolders.Select(Relocate)],
            // The same for the client of a branch and the folder of new bases.
            Branches = [.. library.Branches.Select(x => x with { Executable = Relocate(x.Executable) })], BasesFolder = Relocate(library.BasesFolder),
            BackupFolder = Relocate(library.BackupFolder),
            Bases = [.. library.Bases.Select(Entry)], GroupFolders = [.. library.GroupFolders.Select(Folder)], Sources = sources, Moved = this
        };
    }

    /// <summary>
    /// Writes a detected move once. Profile paths, keys and the anchor are in one file, and the history is
    /// adopted first, so a failed write leaves a state that is detected again on the next load.
    /// </summary>
    public static Library Complete(Library loaded, LibraryStore profile)
    {
        if (loaded.Moved is null) return loaded;
        profile.Save(loaded);
        // The passwords not moved yet: the profile keeps the old anchor and this copy the move, so they are still found.
        return profile.RelocationWarning is null ? loaded with { Moved = null } : loaded;
    }

    public Activity Apply(Activity activity) => activity with
    {
        Favorites = [.. activity.Favorites.Select(x => x with { Source = Key(x.Source) }).DistinctBy(x => (x.Source, x.Id))],
        History = [.. activity.History.Select(x => x with { Source = Key(x.Source) })],
        // Unmoved keys go first, so a view already written at the new place wins over the moved copy.
        Views = [.. activity.Views.OrderBy(x => Key(x.Source) == x.Source ? 0 : 1)
            .Select(x => x with { Source = Key(x.Source) }).DistinctBy(x => x.Source)]
    };
}
