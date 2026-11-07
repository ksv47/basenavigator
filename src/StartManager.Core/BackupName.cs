namespace StartManager.Core;

/// <summary>What the macros of a backup name stand for: the base, its «Префикс:», the computer and the time of the backup.</summary>
public sealed record BackupMacroValues(string BaseName, string Prefix, string ComputerName, DateTime Time);

/// <summary>The name of a backup file and the folders of a scenario, as the original makes them (_Unit186.pas:25645-26371).</summary>
public static class BackupName
{
    public const string DefaultTemplate = "{Prefix}_{IBName}_{Date4}";

    /// <summary>The macros in the order the original replaces them, with their values; dates as Delphi's FormatDateTime gives them
    /// (yymmdd, yyyymmdd, yy, yyyy, mm, m, dd, d, hh, h, nn, n, ss, s): 24-hour clock, invariant digits.</summary>
    private static readonly (string Macro, Func<BackupMacroValues, string> Value)[] Table =
    [
        ("{IBName}", x => x.BaseName), ("{Prefix}", x => x.Prefix), ("{Comp_Name}", x => x.ComputerName),
        ("{Date2}", x => $"{x.Time.Year % 100:00}{x.Time.Month:00}{x.Time.Day:00}"), ("{Date4}", x => $"{x.Time.Year:0000}{x.Time.Month:00}{x.Time.Day:00}"),
        ("{Y2}", x => $"{x.Time.Year % 100:00}"), ("{Y4}", x => $"{x.Time.Year:0000}"), ("{MM}", x => $"{x.Time.Month:00}"), ("{M}", x => $"{x.Time.Month}"),
        ("{DD}", x => $"{x.Time.Day:00}"), ("{D}", x => $"{x.Time.Day}"), ("{HH}", x => $"{x.Time.Hour:00}"), ("{H}", x => $"{x.Time.Hour}"),
        ("{NN}", x => $"{x.Time.Minute:00}"), ("{N}", x => $"{x.Time.Minute}"), ("{SS}", x => $"{x.Time.Second:00}"), ("{S}", x => $"{x.Time.Second}")
    ];

    /// <summary>The macros in the order the original replaces them; the list of the scenario window shows them so.</summary>
    public static readonly IReadOnlyList<string> Macros = [.. Table.Select(x => x.Macro)];

    /// <summary>The characters a file name may not have, those of Windows on every system: the name of a copy stays valid when
    /// the folder is a Windows share or the file is carried to Windows.</summary>
    private static readonly char[] Invalid = [.. Enumerable.Range(0, 32).Select(x => (char)x), '"', '*', '/', ':', '<', '>', '?', '\\', '|'];

    public static string Extension(BackupFormat format) => format switch
    {
        BackupFormat.Dt => ".dt", BackupFormat.Cf => ".cf", BackupFormat.Zip => ".zip",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>{IBName} and {Prefix}: the original replaces them always; the others only when the template has them in this exact
    /// spelling (AnsiPos, case-sensitive, before StringReplace — _Unit186.pas:25727 and on).</summary>
    private static bool AnyCase(string macro) => macro is "{IBName}" or "{Prefix}";

    /// <summary>The template with every macro replaced as the original does: one after another in the order of <see cref="Macros"/>,
    /// each everywhere and regardless of case (StringReplace with rfReplaceAll, rfIgnoreCase). {IBName} and {Prefix} are replaced
    /// always; any other macro only when its exact spelling is in the text (then its other spellings go too, as there). A macro
    /// that a value brings in (a base named «Отчёт {Y4}») is replaced by the later ones, as there.</summary>
    public static string Expand(string template, BackupMacroValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        foreach (var (macro, value) in Table)
            if (AnyCase(macro) || template.Contains(macro, StringComparison.Ordinal))
                template = template.Replace(macro, value(values), StringComparison.OrdinalIgnoreCase);
        return template;
    }

    /// <summary>The characters a file name may not have removed.</summary>
    public static string Clean(string name) => name.IndexOfAny(Invalid) < 0 ? name : string.Concat(name.Where(x => Array.IndexOf(Invalid, x) < 0));

    /// <summary>The file name of a backup: the macros replaced, dots made «-», invalid characters removed, a leading «_» dropped
    /// when the prefix is empty (so «{Prefix}_{IBName}» of a base without one starts with its name) and the extension of the
    /// format added. An empty name is refused: the original would have made a file of the extension alone.</summary>
    public static string FileName(string template, BackupMacroValues values, BackupFormat format)
    {
        var name = Clean(Expand(template, values).Replace(".", "-", StringComparison.Ordinal));
        if (name.StartsWith('_') && values.Prefix.Length == 0) name = name[1..];
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Имя файла по шаблону получилось пустым. Измените шаблон имени файла в сценарии.");
        return name + Extension(format);
    }

    /// <summary>A folder of a scenario with its macros replaced. The text of the path stays as it is; each value is cleaned of the
    /// characters a file name may not have, so a base name never adds a folder level. Unknown «{…}» stay. A folder level that has
    /// a macro and ends up of dots and spaces alone («..», «.», «. .» — also from two macros side by side, a macro next to a typed
    /// dot or a macro that gives nothing, as «.{Prefix}.» with an empty prefix) is refused: it would leave the folder (Windows
    /// also drops trailing dots and spaces of a level). Dots typed by the user in a level without a macro stay. Macros follow the
    /// case rule of <see cref="Expand"/>.</summary>
    public static string Folder(string path, BackupMacroValues values)
    {
        ArgumentNullException.ThrowIfNull(path);
        // The case rule of the name: {IBName} and {Prefix} always, any other macro when its exact spelling is in the path —
        // then in every spelling (Codex recheck, part 1).
        var enabled = Table.Where(x => AnyCase(x.Macro) || path.Contains(x.Macro, StringComparison.Ordinal)).ToArray();
        var result = new System.Text.StringBuilder();
        // Level by level: a macro has no separator and its value is cleaned of them, so substitution never moves a boundary.
        // Both separators on every system: the check is stricter than the system needs, never weaker.
        for (int start = 0, end; start <= path.Length; start = end + 1)
        {
            end = path.IndexOfAny(['/', '\\'], start);
            if (end < 0) end = path.Length;
            var level = new System.Text.StringBuilder();
            var macro = false;
            // One pass from left to right: a value is never searched for macros again.
            for (var at = start; at < end;)
            {
                var known = path[at] == '{' ? Array.FindIndex(enabled, x => string.Compare(path, at, x.Macro, 0, x.Macro.Length, StringComparison.OrdinalIgnoreCase) == 0) : -1;
                if (known < 0) { level.Append(path[at]); at++; continue; }
                level.Append(Clean(enabled[known].Value(values)));
                macro = true;
                at += enabled[known].Macro.Length;
            }
            // A level with a macro that ends up of dots and spaces alone — also when the macro gave nothing — is refused.
            var text = level.ToString();
            if (macro && text.Length > 0 && text.All(x => x is '.' or ' '))
                throw new ArgumentException($"Каталог «{path}» после подстановки макросов содержит уровень «{text}», который выводит за пределы каталога. Измените имя базы, префикс или путь в сценарии.");
            result.Append(text);
            if (end < path.Length) result.Append(path[end]);
        }
        return result.ToString();
    }
}

/// <summary>Where a backup goes when the user does not choose: the order of the original's «Выполнение сценария»
/// (TFormScriptExecute.PathUpdate, _Unit206.pas:6353-6586).</summary>
public static class BackupTarget
{
    /// <summary>The default folder of a backup of <paramref name="entry"/>, as the original chooses it:
    /// <list type="number">
    /// <item>«Каталог:» of the base's page «Резервное копирование»; a relative one is taken inside the general «Каталог по-умолчанию»;</item>
    /// <item>«1. Каталог:» of the scenario (its macros are replaced later, with the name);</item>
    /// <item>«Backup» inside the folder of a file base that exists;</item>
    /// <item>a folder of this base inside the general «Каталог по-умолчанию» (the last 11 characters of its ID, as the original names it).</item>
    /// </list>
    /// The original always has a general folder (its own one in «Документы» when none is set); this program does not make one,
    /// so a base that reaches the last step without it — a server base, a file base whose folder is gone — is refused with
    /// the places to set it.</summary>
    /// <param name="generalFolder">«Каталог по-умолчанию» of the settings (<see cref="Library.BackupFolder"/>).</param>
    /// <param name="folderExists">Checks the folder of a file base; Directory.Exists when null.</param>
    public static string DefaultFolder(BaseEntry entry, BackupScenario? scenario, string generalFolder, Func<string, bool>? folderExists = null)
    {
        var general = generalFolder.Trim();
        // Checked only where it is used: a relative general folder does not block the base's, the scenario's or «Backup» (Codex recheck).
        string General() => IsAbsolute(general) ? general
            : throw new ArgumentException($"Общий «Каталог по-умолчанию» «{general}» должен быть полным путём. Исправьте его в настройках.");
        var own = entry.BackupFolder.Trim();
        if (own.Length > 0)
        {
            if (IsAbsolute(own)) return own;
            // «\sub» is inside the general folder: the original glues a path with a root «\» to it.
            if (general.Length > 0) return Join(General(), own.TrimStart('\\'));
            throw new ArgumentException($"Каталог резервных копий базы «{own}» указан относительно общего каталога, но общий «Каталог по-умолчанию» не задан. Укажите полный путь в свойствах базы или задайте общий каталог в настройках.");
        }
        if (scenario is not null && scenario.Folder1.Trim() is { Length: > 0 } first) return first;
        if (entry.Kind == ConnectionKind.File && entry.Location.Trim() is { Length: > 0 } location && (folderExists ?? Directory.Exists)(location))
            return Path.Combine(location, "Backup");
        if (general.Length > 0) return Join(General(), entry.Id.ToString("D")[^11..]);
        throw new ArgumentException(entry.Kind == ConnectionKind.File
            ? "Каталог базы не найден, и каталог для резервных копий не задан. Укажите его в свойствах базы («Резервное копирование»), в сценарии или общий «Каталог по-умолчанию» в настройках."
            : "Для этой базы не задан каталог резервных копий. Укажите его в свойствах базы («Резервное копирование»), в сценарии или общий «Каталог по-умолчанию» в настройках.");
    }

    /// <summary>A full path on Windows or on Linux, whichever this system is: a profile shared by both keeps «/var/backups»,
    /// «D:\Архив» and «\\server\share» as they are instead of putting them inside the general folder (Grok audit, part 1).
    /// «\sub» and «C:sub» are not full paths.</summary>
    public static bool IsAbsolute(string path) => Path.IsPathFullyQualified(path) || path.StartsWith('/') ||
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>A part inside a folder with the separator of that folder's system, so a folder of the other system stays whole.</summary>
    private static string Join(string folder, string part)
    {
        var separator = folder.StartsWith('/') ? '/'
            : folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.Length > 1 && folder[1] == ':' ? '\\' : Path.DirectorySeparatorChar;
        return folder.TrimEnd('/', '\\') + separator + part;
    }
}
