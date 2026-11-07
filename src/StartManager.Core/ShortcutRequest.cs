namespace StartManager.Core;

public sealed record ShortcutRequest(string Settings, string List, Guid Id, bool Designer)
{
    public string[] Arguments() => ["--settings", Settings, "--list", List, "--launch", Id.ToString(), "--mode", Designer ? "designer" : "enterprise"];

    public static ShortcutRequest? Parse(string[] args)
    {
        if (!args.Contains("--launch")) return null;
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--settings" or "--list" or "--launch" or "--mode") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Неверные параметры ярлыка.");
        }
        if (!values.TryGetValue("--settings", out var settings) || !Path.IsPathFullyQualified(settings) ||
            !values.TryGetValue("--list", out var list) || (list.Length > 0 && !Path.IsPathFullyQualified(list)) ||
            !values.TryGetValue("--launch", out var rawId) || !Guid.TryParse(rawId, out var id) ||
            !values.TryGetValue("--mode", out var mode) || mode is not ("enterprise" or "designer"))
            throw new ArgumentException("Ярлык должен содержать путь к настройкам, список, базу и режим запуска.");
        return new(settings, list, id, mode == "designer");
    }

    public (Library Library, BaseEntry Entry) Resolve()
    {
        if (!File.Exists(Settings)) throw new FileNotFoundException("Файл настроек ярлыка не найден.", Settings);
        var settings = new LibraryStore(Settings).Load();
        var library = LibrarySources.Select(settings, Settings, List);
        var persistentIdentity = true;
        if (List.Length > 0)
        {
            var source = new V8iStore(List);
            library = source.Load(library);
            persistentIdentity = source.HasPersistentId(Id);
        }
        var entry = library.Bases.SingleOrDefault(x => x.Id == Id)
            ?? throw new InvalidOperationException("База из ярлыка отсутствует в списке.");
        if (!persistentIdentity) throw new InvalidOperationException("В записи .v8i нет постоянного ID. Сохраните свойства базы и создайте ярлык заново.");
        return (library, entry);
    }

    public static string WindowsQuote(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string DesktopExec(string executable, IEnumerable<string> args)
    {
        if (executable.Contains('=')) throw new ArgumentException("Путь к программе не должен содержать знак = для ярлыка Linux.");
        static string Quote(string text)
        {
            if (text.Any(char.IsControl)) throw new ArgumentException("Параметры ярлыка содержат управляющие символы.");
            var value = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
            return value.Replace("\\", "\\\\");
        }
        return string.Join(" ", new[] { executable }.Concat(args).Select(Quote));
    }
}
