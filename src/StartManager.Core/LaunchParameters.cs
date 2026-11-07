using System.Diagnostics;
using System.Text;

namespace StartManager.Core;

/// <summary>One launch only. Null options use the saved properties; an empty string explicitly clears them.</summary>
public sealed record LaunchOptions(string? Parameters, string? ExternalProcessing)
{
    public PlatformRule? Platform { get; init; }
    public bool Elevated { get; init; }
    /// <summary>The original's «Запустить с аутентификацией»: 1C asks for the name and password itself (/WA-).
    /// Nothing is typed into or stored by this program.</summary>
    public bool AskLogin { get; init; }

    public static LaunchOptions Login { get; } = new(null, null) { AskLogin = true };

    public BaseEntry Apply(BaseEntry entry) => entry with
    {
        AdditionalParameters = Parameters ?? entry.AdditionalParameters,
        ExternalProcessing = ExternalProcessing ?? entry.ExternalProcessing,
        Executable = Platform is null ? entry.Executable : "",
        PlatformRule = Platform ?? entry.PlatformRule
    };
}

public static class LaunchParameters
{
    public sealed record Parameter(string Key, string Description, bool Value = false);
    public static IReadOnlyList<Parameter> Catalog { get; } = Array.AsReadOnly(new[]
    {
        new Parameter("/UC", "Код разрешения запуска при блокировке соединений", true),
        new Parameter("/ClearCache", "Очистить кэш клиента при запуске (платформа 8.3.10 и новее)"),
        new Parameter("/Debug", "Запустить клиент в режиме отладки"),
        new Parameter("/DebuggerURL", "Адрес отладчика", true),
        new Parameter("/C", "Строка параметров для прикладного решения", true),
        new Parameter("/L", "Язык интерфейса платформы, например ru", true),
        new Parameter("/VL", "Региональные настройки, например ru_RU", true),
        new Parameter("/Out", "Файл служебных сообщений", true),
        new Parameter("/DisableStartupMessages", "Не показывать начальные сообщения"),
        new Parameter("/DisableStartupDialogs", "Не показывать начальные диалоги"),
        new Parameter("/AppAutoCheckMode", "Автоматически определять режим приложения")
    });

    /// <summary>Tokenizes without a shell. Double quotes group spaces; backslashes before a quote follow Windows argv
    /// rules on both OSes. Other backslashes (including Windows paths on Linux) remain literal.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        if (text is null || text.Any(c => char.IsControl(c) && c != '\t'))
            throw new ArgumentException("Строка параметров содержит недопустимые символы.");
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false; var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
                continue;
            }
            started = true;
            if (c == '\\')
            {
                var count = 1;
                while (i + 1 < text.Length && text[i + 1] == '\\') { count++; i++; }
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    token.Append('\\', count / 2); i++;
                    if (count % 2 != 0) token.Append('"'); else quoted = !quoted;
                }
                else token.Append('\\', count);
            }
            else if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { token.Append('"'); i++; }
                else quoted = !quoted;
            }
            else token.Append(c);
        }
        if (quoted) throw new ArgumentException("В строке параметров не закрыта двойная кавычка.");
        if (started) result.Add(token.ToString());
        return result;
    }

    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static IReadOnlyList<string> Validate(string text)
    {
        var args = Parse(text);
        string? valueFor = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var currentValueFor = valueFor;
            valueFor = null;
            // /Out has a file value. A Unix path with a directory component is not a new switch.
            // Keep checking bare /P..., /F..., /Execute, etc. even after a key requiring a value.
            if (string.Equals(currentValueFor, "/Out", StringComparison.OrdinalIgnoreCase) &&
                arg.StartsWith('/') && arg.IndexOf('/', 1) > 1) continue;
            // Check every token, including values: a missing value must never hide another switch.
            // The same keys as the hint and AskLogin, proxy keys included (Codex re-check 2).
            if (NeedsValue(arg))
            {
                if (currentValueFor is null)
                {
                    if (i + 1 >= args.Count) throw new ArgumentException("У параметра запуска отсутствует значение.");
                    valueFor = arg;
                }
                continue;
            }
            var key = arg.ToUpperInvariant();
            if (key.StartsWith("-PPWD") || key.StartsWith("-PPASSWD") ||
                key.StartsWith("/P") && key != "/PROXY" || key.StartsWith("/WSP") ||
                key.StartsWith("/MODIFYPASSWORD") || key.StartsWith("/ACCESSTOKEN") ||
                key.StartsWith("/CONFIGURATIONREPOSITORYP"))
                throw new ArgumentException("Пароли в строке параметров не поддерживаются. Введите пароль в окне входа 1С.");
            if (!arg.StartsWith('/')) continue;
            if (key.StartsWith("/F") || key.StartsWith("/S") && !key.StartsWith("/SLEV") && key != "/SAFEMODE" &&
                !key.StartsWith("/SIMULATESERVERCALLDELAY") && !key.StartsWith("/SAONRESTART") ||
                key.StartsWith("/WS") && !key.StartsWith("/WSA") && !key.StartsWith("/WSN") || key.StartsWith("/IBCONNECTIONSTRING") ||
                key.StartsWith("/IBNAME") || key.StartsWith("/RUNSHORTCUT"))
                throw new ArgumentException("Подключение задаётся в свойствах базы, а не в строке параметров.");
            if (key.StartsWith("/@"))
                throw new ArgumentException("Файл командной строки не поддерживается. Укажите параметры в этом поле.");
            if (key.StartsWith("/EXECUTE"))
                throw new ArgumentException("Укажите обработку в поле «Внешняя обработка».");
        }
        return args;
    }

    /// <summary>Whether the string gives the access code of a locked base (/UC, /UCкод) as a key, not as the value of another key.
    /// False for a text that does not parse.</summary>
    public static bool HasAccessCode(string text)
    {
        IReadOnlyList<string> args;
        try { args = Parse(text); }
        catch (ArgumentException) { return false; }
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("/UC", StringComparison.OrdinalIgnoreCase)) return true;
            if (TakesValue(args[i])) i++;
        }
        return false;
    }

    /// <summary>A hint, not an error: 1C reads arguments in order, so a switch right after a key that needs a value
    /// becomes that value. Names only switches, never values. Null when nothing looks wrong or the text is invalid.</summary>
    public static string? MissingValueWarning(string text)
    {
        IReadOnlyList<string> args;
        try { args = Parse(text); }
        catch (ArgumentException) { return null; }
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (!NeedsValue(args[i])) continue;
            var next = args[i + 1];
            if (NeedsValue(next) || Catalog.Any(x => next.Equals(x.Key, StringComparison.OrdinalIgnoreCase)))
                return $"У параметра {args[i]} не указано значение: 1С примет «{next}» за его значение, а не за отдельный ключ. " +
                    $"Если это не так, укажите значение после {args[i]}.";
            i++; // the next token is the value
        }
        return null;
    }

    /// <summary>Whether one of <paramref name="keys"/> is given as a key after the mode, the connection switch and its address; the value of
    /// a key that takes one (<c>/C "/RunModeManagedApplication"</c>) is not a key (Codex re-check 12.10).</summary>
    public static bool HasKey(IList<string> args, params string[] keys)
    {
        for (var i = 3; i < args.Count; i++)
        {
            if (TakesValue(args[i])) { i++; continue; }
            if (keys.Any(x => args[i].Equals(x, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>A key whose next argument is its value (/C, /N, /UC, the repository keys, …).</summary>
    public static bool TakesValue(string arg) => NeedsValue(arg) ||
        new[] { "/Execute", "/ConfigurationRepositoryF", "/ConfigurationRepositoryN", "/ConfigurationRepositoryP", "-PPwd" }.Any(x => arg.Equals(x, StringComparison.OrdinalIgnoreCase));

    private static bool NeedsValue(string arg) =>
        Catalog.Any(x => x.Value && arg.Equals(x.Key, StringComparison.OrdinalIgnoreCase)) ||
        arg.Equals("/N", StringComparison.OrdinalIgnoreCase) || arg.Equals("/WSN", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/O", StringComparison.OrdinalIgnoreCase) || ProxyKeys.Any(x => arg.Equals(x, StringComparison.OrdinalIgnoreCase));

    // Proxy settings after /Proxy that take a value; -PPwd and -PPasswd are refused by Validate.
    private static readonly string[] ProxyKeys = ["-PSrv", "-PPort", "-PUser"];

    /// <summary>Replaces any /WA switch of the saved parameters: OS authentication would skip the login window.
    /// The first three arguments are the mode, the connection switch and its address; a value of a known key
    /// (<c>/C /WA</c>) is left as it is, just as 1C would read it. After a key this program does not know
    /// (<c>/ConfigurationRepositoryN /WA+</c>) the token may be its value: the start is refused instead of guessing.</summary>
    public static void AskLogin(ProcessStartInfo start)
    {
        var args = start.ArgumentList;
        var unknownKey = false;
        for (var i = 3; i < args.Count; i++)
        {
            var arg = args[i];
            if (TakesValue(arg))
            {
                // Validate refuses this; a missing value must never be filled with the /WA- added below.
                if (i + 1 >= args.Count) throw new ArgumentException("У параметра запуска отсутствует значение.");
                i++; unknownKey = false; continue;
            }
            if (IsWindowsAuthentication(arg))
            {
                // The preceding token is not named: it may be a value (Codex re-check P3).
                if (unknownKey)
                    throw new ArgumentException("Запуск с аутентификацией не выполнен: в строке параметров /WA стоит сразу после ключа, " +
                        "который БазоНавигатор не распознаёт, и может быть его значением. Уберите /WA из параметров или укажите значение этого ключа явно.");
                args.RemoveAt(i--);
                continue;
            }
            // Keys of 1C start with / and the proxy keys with -.
            unknownKey = (arg.StartsWith('/') || arg.StartsWith('-')) && !KnownWithoutValue(arg);
        }
        args.Add("/WA-");
    }

    private static bool IsWindowsAuthentication(string arg) => arg.Equals("/WA", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/WA+", StringComparison.OrdinalIgnoreCase) || arg.Equals("/WA-", StringComparison.OrdinalIgnoreCase);

    // Switches without a value: those of the catalog, the ones this program adds after the parameters and a few
    // common flags of 1C (Kimi release check 0.3.0.3).
    private static bool KnownWithoutValue(string arg) =>
        Catalog.Any(x => !x.Value && arg.Equals(x.Key, StringComparison.OrdinalIgnoreCase)) ||
        new[] { "/AppAutoCheckVersion-", "/AppAutoCheckVersion+", "/AppAutoCheckVersion", "/RunModeOrdinaryApplication",
            "/RunModeManagedApplication", "/Proxy", "/NoProxy", "/UseHwLicenses+", "/UseHwLicenses-", "/UseHwLicenses", "/WSA+", "/WSA-", "/WSA" }
            .Any(x => arg.Equals(x, StringComparison.OrdinalIgnoreCase));

    public static bool HasClientOptions(BaseEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.EffectiveParameters) || !string.IsNullOrWhiteSpace(entry.ExternalProcessing);

    public static void Append(ProcessStartInfo start, BaseEntry entry, bool designer)
    {
        IReadOnlyList<string> parameters;
        try { parameters = Validate(entry.EffectiveParameters); }
        catch (ArgumentException ex) when (entry.AdditionalParameters is null && entry.ListParameters.Length > 0)
        {
            throw new ArgumentException("Не удалось применить дополнительные параметры из списка баз (.v8i). " + ex.Message +
                " Исправьте или очистите строку «Дополнительные параметры» в свойствах базы, в разделе «Сведения».");
        }
        foreach (var arg in parameters) start.ArgumentList.Add(arg);
        if (!designer && !string.IsNullOrWhiteSpace(entry.ExternalProcessing))
        {
            ValidateProcessing(entry.ExternalProcessing);
            start.ArgumentList.Add("/Execute"); start.ArgumentList.Add(entry.ExternalProcessing);
        }
    }

    public static void ValidateProcessing(string path)
    {
        if (path.Length > 0 && (!Path.IsPathFullyQualified(path) || !File.Exists(path)))
            throw new ArgumentException("Внешняя обработка не найдена. Укажите полный путь к файлу или отключите её запуск.");
    }
}
