using System.Text;

namespace StartManager.Core;

/// <summary>The original's «Сервис → Список информационных баз»: bases that are gone, a changed drive letter, a copy of the list.</summary>
public static class ListMaintenance
{
    /// <summary>What is known about the folder of a file base.</summary>
    public enum Presence { Found, Missing, Unknown }

    /// <summary>File bases proven to be gone. A network drive that is not connected looks the same: the caller shows the names
    /// before anything is removed. Server and web bases, paths of another OS (a list shared by Windows and Linux), relative
    /// paths and folders this user may not read are never reported.</summary>
    public static IReadOnlyList<BaseEntry> Missing(IEnumerable<BaseEntry> bases) =>
        [.. bases.Where(x => x.Kind == ConnectionKind.File && Check(x.Location) == Presence.Missing)];

    public static Presence Check(string location)
    {
        if (ForeignPath(location) || !Path.IsPathFullyQualified(location)) return Presence.Unknown;
        try
        {
            if (File.Exists(Path.Combine(location, "1Cv8.1CD"))) return Presence.Found;
            if (!Directory.Exists(location)) return Absent(location);
            // A folder that cannot be listed hides its files: File.Exists says no for a file it may not see.
            using var entries = Directory.EnumerateFileSystemEntries(location).GetEnumerator();
            entries.MoveNext();
            return Presence.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException) { return Presence.Unknown; }
    }

    // Directory.Exists says no both for a folder that is gone and for one under a parent this user may not read. The nearest
    // folder that is seen decides: when it can be listed and has no such name, the base is gone; otherwise nothing is known.
    private static Presence Absent(string location)
    {
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        var parent = Path.GetDirectoryName(child);
        while (parent is not null && !Directory.Exists(parent)) { child = parent; parent = Path.GetDirectoryName(parent); }
        // No part of the path is seen, as a disconnected drive: the question names this case before anything is removed.
        if (parent is null) return Presence.Missing;
        var name = Path.GetFileName(child);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Directory.EnumerateFileSystemEntries(parent).Any(x => Path.GetFileName(x).Equals(name, comparison))
            ? Presence.Unknown : Presence.Missing;
    }

    /// <summary>A path written by another OS: «/home/…» on Windows, «C:\…» or «\\server\…» elsewhere.</summary>
    public static bool ForeignPath(string location) => OperatingSystem.IsWindows()
        ? location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
        : location.Length >= 2 && char.IsAsciiLetter(location[0]) && location[1] == ':' || location.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>File bases on drive <paramref name="from"/> moved to drive <paramref name="to"/>: «X:», «X:\…» or «X:/…»;
    /// network paths and other drives stay. Returns the changed library and the number of changed bases.</summary>
    public static (Library Library, int Changed) ReplaceDrive(Library library, char from, char to)
    {
        from = char.ToUpperInvariant(from); to = char.ToUpperInvariant(to);
        if (from is < 'A' or > 'Z' || to is < 'A' or > 'Z') throw new ArgumentException("Буква диска — латинская буква от A до Z.");
        if (from == to) throw new ArgumentException("Выберите разные буквы дисков.");
        var changed = 0;
        var bases = library.Bases.Select(entry =>
        {
            if (entry.Kind != ConnectionKind.File || !OnDrive(entry.Location, from)) return entry;
            changed++;
            return entry with { Location = to + entry.Location[1..] };
        }).ToList();
        return (library with { Bases = bases }, changed);
    }

    public static int CountOnDrive(IEnumerable<BaseEntry> bases, char drive) =>
        bases.Count(x => x.Kind == ConnectionKind.File && OnDrive(x.Location, char.ToUpperInvariant(drive)));

    private static bool OnDrive(string location, char drive) =>
        location.Length >= 2 && char.ToUpperInvariant(location[0]) == drive && location[1] == ':' &&
        (location.Length == 2 || location[2] is '\\' or '/');

    /// <summary>A .v8i list with the bases and groups of a local list, in the format of 1CEStart: a record per group and per base
    /// with its ID, Folder and Connect; the platform version (8.3 when unknown, as a new record of the shared list) and the client.
    /// A value with a line break or another control character cannot be written: the export is refused, nothing is changed.</summary>
    public static string ToV8i(Library library)
    {
        LibraryStore.Validate(library);
        static string Value(string value, string what) => value.Any(char.IsControl)
            ? throw new ArgumentException($"{what} «{value.Replace("\r", " ").Replace("\n", " ")}» содержит перевод строки или управляющий символ; такой список не выгружается. Исправьте название.")
            : value;
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append("\r\n");
        foreach (var group in Groups.All(library))
        {
            Line("[" + Value(group.Split('/')[^1], "Группа") + "]");
            Line("ID=" + GroupId(group));
            Line("Folder=/" + Value(Groups.Parent(group), "Группа"));
        }
        foreach (var entry in library.Bases)
        {
            Line("[" + Value(entry.Name, "Наименование") + "]");
            Line("Connect=" + Value(BaseClipboard.ConnectionString(entry), "Путь базы"));
            Line("ID=" + entry.Id);
            Line("Folder=/" + Value(entry.Group, "Группа"));
            Line("Version=" + (entry.PlatformVersion.Length > 0 ? Value(entry.PlatformVersion, "Версия") : "8.3"));
            if (entry.ClientType.Length > 0) Line("App=" + Value(entry.ClientType, "Клиент"));
            // «Запрашивать имя и пароль» of the page, as 1CEStart writes it; «Выбирать автоматически» needs no line.
            if (entry.AskCredentials) Line("WA=0");
            if (entry.WebServerAskCredentials is { } askWeb) Line("WSA=" + (askWeb ? "1" : "0"));
            // The proxy password is not exported: it stays in the saved passwords of the profile.
            if (entry.ConnectionSpeed is { Length: > 0 }) Line("ClientConnectionSpeed=" + entry.ConnectionSpeed);
            if (entry.UseProxy is { } proxy) Line("UseProxy=" + (int)proxy);
            if (entry.ProxyServer.Length > 0) Line("ProxyServer=" + Value(entry.ProxyServer, "Прокси-сервер"));
            if (entry.ProxyPort.Length > 0) Line("ProxyPort=" + Value(entry.ProxyPort, "Порт прокси"));
            if (entry.ProxyUser.Length > 0) Line("ProxyUser=" + Value(entry.ProxyUser, "Пользователь прокси"));
        }
        return text.ToString();
    }

    // A stable ID of a group: the same list exported twice gives the same file.
    private static Guid GroupId(string group) =>
        new(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("group\0" + group.ToLowerInvariant())).AsSpan(0, 16));

    /// <summary>Writes the list to a new file or replaces a chosen one; UTF-8 with BOM, as 1CEStart writes it.
    /// <paramref name="protectedFiles"/> are the files of the program (the profile, its history, the open list and their
    /// backups): the copy never replaces them, also when the chosen name reaches them through a link.</summary>
    public static void Export(string path, byte[] content, IEnumerable<string>? protectedFiles = null)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var physical = BaseDuplicate.Physical(full);
        foreach (var file in protectedFiles ?? [])
        {
            var other = Path.GetFullPath(file);
            if (full.Equals(other, comparison) || physical.Equals(BaseDuplicate.Physical(other), comparison))
                throw new ArgumentException("Выберите другой файл: этот файл использует программа (открытый список или настройки).");
        }
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static byte[] Utf8WithBom(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
}
