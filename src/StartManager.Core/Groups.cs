namespace StartManager.Core;

public static class Groups
{
    public static string Normalize(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static bool Contains(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    public static List<string> All(Library library)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in library.Groups.Concat(library.Bases.Select(x => x.Group)))
        {
            var path = group;
            while (path.Length > 0) { result.Add(path); path = Parent(path); }
        }
        return result.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Library Rename(Library library, string original, string target)
    {
        target = Normalize(target);
        if (target.Length == 0 || target.Any(char.IsControl)) throw new ArgumentException("Введите имя группы.");
        var groups = All(library);
        if (groups.Any(x => x.Equals(target, StringComparison.OrdinalIgnoreCase) && !x.Equals(original, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Такая группа уже существует.");
        if (original.Length == 0) return library with { Groups = groups.Append(target).ToList() };
        if (!groups.Contains(original, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Группа не найдена.");
        if (target.StartsWith(original + "/", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Нельзя переместить группу внутрь себя.");
        string Map(string value) => Contains(original, value) ? target + value[original.Length..] : value;
        return library with { Groups = groups.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Bases = library.Bases.Select(x => x with { Group = Map(x.Group) }).ToList(),
            GroupFolders = MapFolders(library.GroupFolders, Map) };
    }

    /// <summary>The items of one level of the tree: its groups (full paths) and its bases, already in the order of the column.
    /// Groups go first; with <paramref name="mixed"/> (the original's «Отображать группы сверху» off under the sort by name)
    /// groups and bases are ordered together by their shown name, a group first of equal names.</summary>
    public static IReadOnlyList<(string? Group, BaseEntry? Entry)> Level(IEnumerable<string> groups, IEnumerable<BaseEntry> bases, bool mixed, bool descending)
    {
        var items = groups.Select(x => (Name: x.Split('/')[^1], Group: (string?)x, Entry: (BaseEntry?)null))
            .Concat(bases.Select(x => (x.Name, Group: (string?)null, Entry: (BaseEntry?)x)));
        if (mixed)
        {
            var names = StringComparer.CurrentCultureIgnoreCase;
            items = descending ? items.OrderByDescending(x => x.Name, names).ThenBy(x => x.Entry is not null)
                : items.OrderBy(x => x.Name, names).ThenBy(x => x.Entry is not null);
        }
        return [.. items.Select(x => (x.Group, x.Entry))];
    }

    public static Library RemoveEmpty(Library library, string group)
    {
        if (library.Bases.Any(x => Contains(group, x.Group)) || All(library).Any(x => x.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Сначала переместите базы и вложенные группы. Удаляется только пустая группа.");
        return library with { Groups = All(library).Where(x => !x.Equals(group, StringComparison.OrdinalIgnoreCase)).ToList(),
            GroupFolders = library.GroupFolders.Where(x => !Contains(group, Normalize(x.Group))).ToList() };
    }

    /// <summary>The folder of the group itself, empty when it has none.</summary>
    public static string Folder(Library library, string group) =>
        library.GroupFolders.LastOrDefault(x => Normalize(x.Group).Equals(group, StringComparison.OrdinalIgnoreCase))?.Folder ?? "";

    /// <summary>The folder for a new base of the group: its own or, as the original's GetParentFolder, its parent's (one level only).</summary>
    public static string InheritedFolder(Library library, string group)
    {
        group = Normalize(group);
        if (group.Length == 0) return "";
        return Folder(library, group) is { Length: > 0 } own ? own : Parent(group) is { Length: > 0 } parent ? Folder(library, parent) : "";
    }

    /// <summary>The library with the group's folder set; an empty folder removes the setting. Unknown fields of the record stay.</summary>
    public static Library WithFolder(Library library, string group, string folder)
    {
        group = Normalize(group); folder = folder.Trim();
        if (folder.Any(char.IsControl)) throw new ArgumentException("Каталог группы содержит недопустимые символы.");
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder)) throw new ArgumentException("Каталог группы: укажите полный путь или оставьте поле пустым.");
        var existing = library.GroupFolders.LastOrDefault(x => Normalize(x.Group).Equals(group, StringComparison.OrdinalIgnoreCase));
        var rest = library.GroupFolders.Where(x => !Normalize(x.Group).Equals(group, StringComparison.OrdinalIgnoreCase));
        return library with { GroupFolders = folder.Length == 0 ? rest.ToList()
            : rest.Append((existing ?? new GroupFolder()) with { Group = group, Folder = folder }).ToList() };
    }

    /// <summary>The folders of the groups that are still there (a restored local list).</summary>
    public static List<GroupFolder> Existing(IEnumerable<GroupFolder> folders, Library library)
    {
        var groups = new HashSet<string>(All(library), StringComparer.OrdinalIgnoreCase);
        return MapFolders(folders.Where(x => groups.Contains(Normalize(x.Group))), x => x);
    }

    /// <summary>The folders matched with the group records of a .v8i (group path → record ID, null without one): a folder with
    /// an ID goes to the group of that record wherever it is now, or is dropped when the record is gone; a folder without one
    /// stays with an existing group of its path and takes the ID of its record.</summary>
    public static List<GroupFolder> Reconcile(IEnumerable<GroupFolder> folders, IReadOnlyDictionary<string, Guid?> records, IEnumerable<string> groups)
    {
        var existing = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        var ids = records.Where(x => x.Value is not null).GroupBy(x => x.Value!.Value).ToList();
        var byId = ids.Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single().Key);
        // An ID two group records share (Codex re-check 13.10) tells nothing: such a folder keeps to its path and gets no ID.
        var ambiguous = ids.Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        var result = new List<GroupFolder>();
        // A folder found by its record's ID goes first: a folder kept only by its path does not take its group (Grok audit 13.10).
        var ordered = folders.OrderBy(x => x.Id is { } known && byId.ContainsKey(known) ? 0 : 1).ToList();
        var byIdGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ordered)
        {
            GroupFolder? matched;
            if (folder.Id is { } id && byId.TryGetValue(id, out var path)) matched = folder with { Group = path };
            else if (folder.Id is { } gone && !ambiguous.Contains(gone)) matched = null;
            else
            {
                var group = Normalize(folder.Group);
                matched = !existing.Contains(group) ? null : folder with { Group = group,
                    Id = records.TryGetValue(group, out var record) && record is { } own && byId.ContainsKey(own) ? own : null };
            }
            if (matched is null) continue;
            if (byIdGroups.Contains(matched.Group)) continue;
            if (folder.Id is { } found && byId.ContainsKey(found)) byIdGroups.Add(matched.Group);
            result.RemoveAll(x => x.Group.Equals(matched.Group, StringComparison.OrdinalIgnoreCase));
            result.Add(matched);
        }
        return result;
    }

    /// <summary>The folders follow their groups when the groups get new paths; two folders for one group keep the later one.</summary>
    public static List<GroupFolder> MapFolders(IEnumerable<GroupFolder> folders, Func<string, string> map)
    {
        var result = new List<GroupFolder>();
        foreach (var folder in folders)
        {
            var target = map(Normalize(folder.Group));
            result.RemoveAll(x => x.Group.Equals(target, StringComparison.OrdinalIgnoreCase));
            result.Add(folder with { Group = target });
        }
        return result;
    }
}
