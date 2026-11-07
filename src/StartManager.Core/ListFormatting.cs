using System.Globalization;

namespace StartManager.Core;

public enum NameCase { None, Upper, Lower, Sentence, Words }

/// <summary>The original's «Форматирование списка баз»: the case of the names, trailing spaces, a replaced string; for groups,
/// for bases or both.</summary>
public sealed record NameFormat(NameCase Case = NameCase.None, bool TrimEnd = false, string ReplaceFrom = "", string ReplaceTo = "",
    bool Groups = true, bool Bases = true);

public static class ListFormatting
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(string name, NameFormat format)
    {
        var result = name;
        if (format.ReplaceFrom.Length > 0) result = result.Replace(format.ReplaceFrom, format.ReplaceTo, StringComparison.Ordinal);
        if (format.TrimEnd) result = result.TrimEnd();
        return format.Case switch
        {
            NameCase.Upper => result.ToUpper(Russian),
            NameCase.Lower => result.ToLower(Russian),
            NameCase.Sentence => result.Length == 0 ? result : char.ToUpper(result[0], Russian) + result[1..].ToLower(Russian),
            NameCase.Words => Russian.TextInfo.ToTitleCase(result.ToLower(Russian)),
            _ => result
        };
    }

    /// <summary>The whole result of the formatting, written by one save: the library with the new names and the map of every
    /// renamed group (old full path → new full path) for <see cref="V8iStore.Save"/>. A segment is judged as the list will
    /// store it (<see cref="StartManager.Core.Groups.Normalize"/>): a «\» or «/», an empty name or two groups with one name are
    /// refused before anything is written.</summary>
    public static (Library Library, IReadOnlyDictionary<string, string> GroupMap, int Bases, int Groups) Plan(Library library, NameFormat format)
    {
        var all = StartManager.Core.Groups.All(library);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Target(string group)
        {
            if (map.TryGetValue(group, out var done)) return done;
            var parent = StartManager.Core.Groups.Parent(group);
            var original = group.Split('/')[^1];
            var segment = format.Groups ? Format(original, format) : original;
            if (segment.Contains('/') || segment.Contains('\\') || segment.Any(char.IsControl) || StartManager.Core.Groups.Normalize(segment).Length == 0)
                throw new ArgumentException($"Группа «{group}» получила бы недопустимое имя «{segment}».");
            segment = StartManager.Core.Groups.Normalize(segment);
            return map[group] = parent.Length == 0 ? segment : Target(parent) + "/" + segment;
        }
        foreach (var group in all) Target(group);
        var clash = all.GroupBy(x => map[x], StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (clash is not null) throw new ArgumentException($"Группы {string.Join(", ", clash.Select(x => "«" + x + "»"))} получили бы одно имя «{clash.Key}».");
        var bases = FormatBases(library, format).Select(x => x.Group.Length == 0 ? x : x with { Group = map.GetValueOrDefault(x.Group, x.Group) }).ToList();
        var changedBases = bases.Zip(library.Bases).Count(x => x.First.Name != x.Second.Name);
        var renamed = map.Where(x => !x.Key.Equals(x.Value, StringComparison.Ordinal)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var result = library with { Bases = bases, Groups = [.. library.Groups.Select(x => map.GetValueOrDefault(x, x))],
            GroupFolders = StartManager.Core.Groups.MapFolders(library.GroupFolders, x => map.GetValueOrDefault(x, x)) };
        LibraryStore.Validate(result);
        return (result, renamed, changedBases, renamed.Count(x => !x.Key.Split('/')[^1].Equals(x.Value.Split('/')[^1], StringComparison.Ordinal)));
    }

    /// <summary>The bases with formatted names; a name that would become empty or have a line break is refused.</summary>
    public static List<BaseEntry> FormatBases(Library library, NameFormat format) => [.. library.Bases.Select(entry =>
    {
        if (!format.Bases) return entry;
        var name = Format(entry.Name, format);
        if (name.Trim().Length == 0 || name.Any(char.IsControl)) throw new ArgumentException($"База «{entry.Name}» получила бы недопустимое имя «{name}».");
        return name == entry.Name ? entry : entry with { Name = name };
    })];
}
