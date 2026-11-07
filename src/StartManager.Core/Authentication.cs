using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

/// <summary>Which saved password: 1С:Предприятие, the configurator, the configuration repository or the proxy of a base on a web server.</summary>
public enum PasswordRole { Enterprise, Designer, Repository, Proxy }

/// <summary>The original's page «Аутентификация» without the passwords: those live in <see cref="PasswordVault"/>.
/// A local setting of the base, like its comment: the shared list is not changed by it.</summary>
public sealed record AuthenticationSettings
{
    public bool EnterpriseLogin { get; init; }
    public string EnterpriseUser { get; init; } = "";
    public bool DesignerLogin { get; init; }
    public string DesignerUser { get; init; } = "";
    /// <summary>«Использовать аутентификацию ОС»: /WA+, no name or password is passed.</summary>
    public bool OsAuthentication { get; init; }
    public bool Repository { get; init; }
    public string RepositoryPath { get; init; } = "";
    public string RepositoryUser { get; init; } = "";
    /// <summary>Fields of another version or tool, kept as they are through loads, saves and the properties dialog (Codex full
    /// audit 24.10, P2-6).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public bool IsEmpty => this == new AuthenticationSettings();

    // The unknown fields compare by their value: a profile read again gives equal settings, not other ones.
    public bool Equals(AuthenticationSettings? other) => other is not null &&
        (EnterpriseLogin, EnterpriseUser, DesignerLogin, DesignerUser, OsAuthentication, Repository, RepositoryPath, RepositoryUser) ==
        (other.EnterpriseLogin, other.EnterpriseUser, other.DesignerLogin, other.DesignerUser, other.OsAuthentication, other.Repository, other.RepositoryPath, other.RepositoryUser) &&
        SameExtra(Extra, other.Extra);

    public override int GetHashCode() => HashCode.Combine(EnterpriseLogin, EnterpriseUser, DesignerLogin, DesignerUser, OsAuthentication, Repository, RepositoryPath, RepositoryUser);

    private static bool SameExtra(Dictionary<string, JsonElement>? a, Dictionary<string, JsonElement>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a is null || a.All(x => b!.TryGetValue(x.Key, out var y) && JsonElement.DeepEquals(x.Value, y)));

    public void Validate()
    {
        if (EnterpriseUser is null || DesignerUser is null || RepositoryPath is null || RepositoryUser is null)
            throw new ArgumentException("Повреждены параметры аутентификации.");
        if (new[] { EnterpriseUser, DesignerUser, RepositoryPath, RepositoryUser }.Any(x => x.Any(char.IsControl)))
            throw new ArgumentException("Имя пользователя и каталог хранилища не должны содержать переносы строк и управляющие символы.");
        if (Repository && string.IsNullOrWhiteSpace(RepositoryPath))
            throw new ArgumentException("Укажите каталог хранилища конфигурации или снимите флажок «Подключить хранилище конфигурации».");
    }
}

/// <summary>The saved passwords of a base.</summary>
public sealed record VaultRead(IReadOnlyDictionary<PasswordRole, string> Passwords);

/// <summary>The pending notice about removed passwords of an earlier build: the bases not yet shown, and the running total of
/// removals it covers (<see cref="PasswordVault.Acknowledge"/>).</summary>
/// The generation names the counters: an earlier build drops them, the next removal starts a new generation, and an
/// acknowledgement of another generation changes nothing (Codex re-check 5, RC5-1).
public sealed record RetiredNotice(int Bases, int UpTo, string? Generation = null);

/// <summary>The saved passwords of the bases of one list, for a start: read when the start needs one.</summary>
public sealed class ListPasswords(PasswordVault vault, Library library, string source)
{
    public string Source => source;
    public IReadOnlyDictionary<PasswordRole, string> Read(Guid id) => vault.Read(library, source, id).Passwords;
}

/// <summary>Saved passwords of one profile: «passwords.json» next to the settings, every value sealed with AES-GCM under a
/// random key in «passwords.key» of the same folder. The key travels with the folder: a portable profile moved to another
/// computer or OS keeps its passwords. Whoever copies both files reads them, as with the original StartManager; a damaged
/// file or a wrong key is refused instead of giving 1C a wrong password. On Linux both files are readable by the owner only.</summary>
public sealed class PasswordVault
{
    private const string Associated = "BaseNavigator password v1\0";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    // RetiredTotal: bases whose passwords of an earlier build were removed, a running total that only grows; ShownUpTo: the part
    // of it the main window has shown. An earlier build drops both fields: then the notice is lost, nothing else.
    private sealed record Content(int Version, Dictionary<string, string> Passwords,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetiredTotal = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShownUpTo = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RetiredGeneration = null);
    private readonly record struct Notice(int Total, int Shown, string? Generation);

    public PasswordVault(string settingsPath)
    {
        SettingsPath = Path.GetFullPath(settingsPath);
        var folder = Path.GetDirectoryName(SettingsPath) ?? throw new ArgumentException("Не задан каталог настроек.");
        PathName = Path.Combine(folder, "passwords.json");
        KeyPath = Path.Combine(folder, "passwords.key");
    }

    public string SettingsPath { get; }
    public string PathName { get; }
    public string KeyPath { get; }

    /// <summary>The notice about removed passwords of an earlier build (<see cref="RetireLegacy"/>).</summary>
    public static string RetiredMessage(int bases) =>
        $"Сохранённые пароли прежней версии удалены у баз: {bases} — введите их заново в свойствах.";

    // «<list key>/<base ID>/<role>»: the same base ID may be in several lists with other servers and users (Codex full audit
    // 24.10, P1-1). The list key is the one of the profile's sources (LibrarySources.Key). Builds up to 0.4.0.0 wrote
    // «<base ID>/<role>»; such values are never read for a base and are removed at the start (RetireLegacy).
    private static string Name(string source, Guid id, PasswordRole role) => source + "/" + Legacy(id, role);
    private static string Legacy(Guid id, PasswordRole role) => id.ToString("D") + "/" + role;

    /// <summary>The list, base and role of a name; the list is null for a name of an earlier build.</summary>
    private static (string? Source, Guid Id, PasswordRole Role)? Parse(string name)
    {
        var slash = name.LastIndexOf('/');
        if (slash <= 0 || !Enum.GetNames<PasswordRole>().Contains(name[(slash + 1)..], StringComparer.Ordinal)) return null;
        var before = name.LastIndexOf('/', slash - 1);
        if (!Guid.TryParseExact(name[(before + 1)..slash], "D", out var id)) return null;
        return (before < 0 ? null : name[..before], id, Enum.Parse<PasswordRole>(name[(slash + 1)..]));
    }

    /// <summary>The passwords of the open list's bases (<see cref="LibrarySources.Key"/>), or of the list given.</summary>
    public ListPasswords For(Library library, string? source = null) => new(this, library, source ?? LibrarySources.Key(library, SettingsPath));

    /// <summary>The saved passwords of a base of a list; a role without a password is absent. A value of an earlier build
    /// («<base ID>/<role>») is never given to a base (<see cref="RetireLegacy"/>).</summary>
    /// <param name="library">The profile with its open list: a moved portable folder.</param>
    public VaultRead Read(Library library, string source, Guid id)
    {
        var result = new Dictionary<PasswordRole, string>();
        if (!File.Exists(PathName)) return new(result);
        Restrict();
        var content = Load();
        // A portable folder moved since the save: its list keys are those of the old place until the profile is saved.
        var names = new Dictionary<PasswordRole, string>();
        foreach (var name in content.Passwords.Keys)
            if (Parse(name) is { Source: { } list } parsed && parsed.Id == id &&
                (list == source || library.Moved?.Key(list) == source && !names.ContainsKey(parsed.Role)))
                names[parsed.Role] = name;
        if (names.Count > 0)
        {
            var key = ReadKey() ?? throw new IOException($"Сохранённые пароли не читаются: нет файла ключа {KeyPath}. Восстановите его из копии папки настроек или введите пароли заново.");
            foreach (var (role, name) in names) result[role] = Open(key, name, content.Passwords[name]);
        }
        return new(result);
    }

    /// <summary>Removes every value of an earlier build («<base ID>/<role>», 0.3.0.x): such a value is never moved to a list nor
    /// given to a base (decision of the user 25.10.2026, after Codex re-check 3). Run at the start of the program, under the lock
    /// of the vault; the profile is not needed. The number of bases is added, in the same write, to the notice the vault keeps
    /// until the main window has shown it (<see cref="PendingRetired"/>, <see cref="Acknowledge"/>). A vault that cannot be
    /// changed now (no key, busy) throws: the values stay, still given to no base, and the next start tries again.
    /// The number of bases whose values were removed by this call.</summary>
    public int RetireLegacy()
    {
        if (!File.Exists(PathName) || !Load().Passwords.Keys.Any(x => Parse(x) is { Source: null })) return 0;
        var removed = 0;
        Change((passwords, _) =>
        {
            var legacy = passwords.Keys.Select(x => (Name: x, Parsed: Parse(x))).Where(x => x.Parsed is { Source: null }).ToList();
            foreach (var (name, _) in legacy) passwords.Remove(name);
            removed = legacy.Select(x => x.Parsed!.Value.Id).Distinct().Count();
        }, notice => removed == 0 ? notice
            // Counters an earlier build dropped (no generation) start a new generation.
            : notice with { Total = notice.Total + removed, Generation = notice.Generation ?? Guid.NewGuid().ToString("N") });
        return removed;
    }

    /// <summary>The notice not yet shown: the bases whose passwords of an earlier build were removed, and the running total it
    /// covers (to be given back to <see cref="Acknowledge"/>); no bases when none.</summary>
    public RetiredNotice PendingRetired()
    {
        if (!File.Exists(PathName)) return new(0, 0);
        var content = Load();
        var (total, shown) = (content.RetiredTotal ?? 0, content.ShownUpTo ?? 0);
        return new(total - shown, total, content.RetiredGeneration);
    }

    /// <summary>The main window has shown <paramref name="seen"/>: the removals counted up to its total leave the pending notice,
    /// later ones stay. A second or older acknowledgement — another window showing the same notice — changes nothing (Codex
    /// re-check 4, N1), nor does one of another generation (the counters were dropped by an earlier build meanwhile, RC5-1).</summary>
    public void Acknowledge(RetiredNotice seen)
    {
        if (seen.UpTo <= 0 || seen.Generation is null || PendingRetired() is not { Bases: > 0 } pending ||
            pending.Generation != seen.Generation || seen.UpTo <= pending.UpTo - pending.Bases) return;
        Change((_, _) => { }, notice => notice.Generation != seen.Generation ? notice
            : notice with { Shown = Math.Max(notice.Shown, Math.Min(seen.UpTo, notice.Total)) });
    }

    public string? Get(Library library, string source, Guid id, PasswordRole role) => Read(library, source, id).Passwords.GetValueOrDefault(role);

    /// <summary>Replaces the given roles of a base of a list: an empty or null password removes the saved one. Other bases,
    /// lists and roles stay as they are, sealed. A missing key with saved passwords is refused: a new key would make the
    /// others unreadable.</summary>
    public void Write(string source, Guid id, IReadOnlyDictionary<PasswordRole, string?> changes)
    {
        if (changes.Count == 0) return;
        Change((passwords, key) =>
        {
            foreach (var (role, password) in changes)
            {
                var name = Name(source, id, role);
                if (string.IsNullOrEmpty(password)) passwords.Remove(name);
                else passwords[name] = Seal(key(), name, password);
            }
        });
    }

    /// <summary>Forgets the saved passwords of a base removed from a list; the same ID in other lists keeps its own.</summary>
    public void Remove(string source, Guid id) => Change((passwords, _) =>
    {
        foreach (var role in Enum.GetValues<PasswordRole>()) passwords.Remove(Name(source, id, role));
    });

    /// <summary>Tests: runs before the passwords of a moved folder are moved (a vault that cannot be changed throws there).</summary>
    internal static Action<string>? BeforeAdopt;

    /// <summary>A moved portable folder: the passwords of its lists follow the new list keys, as the history does
    /// (<see cref="ActivityStore.Adopt"/>). A key the new place already has is newer and stays. No vault — nothing to move.
    /// A vault without its key or with values that do not open throws (decided under the lock, Codex re-check 2, F2): the
    /// caller keeps the old anchor and tries again later.</summary>
    public void Adopt(ProfileRelocation move)
    {
        BeforeAdopt?.Invoke(PathName);
        Change((passwords, key) =>
        {
            foreach (var name in passwords.Keys.ToList())
            {
                if (Parse(name) is not { Source: { } list } parsed || move.Key(list) is var target && target == list) continue;
                var moved = Name(target, parsed.Id, parsed.Role);
                if (!passwords.ContainsKey(moved)) passwords[moved] = Seal(key(), moved, Open(key(), name, passwords[name]));
                passwords.Remove(name);
            }
        });
    }

    /// <summary>Changes the saved names under the lock. Every saved value must open with the key before anything is written:
    /// a damaged key or value would otherwise leave passwords under two keys, and the one replaced could never be read again.
    /// Values of an earlier build are kept (<see cref="RetireLegacy"/> removes them); <paramref name="notice"/> changes the
    /// pending notice of removed ones in the same write.</summary>
    private void Change(Action<Dictionary<string, string>, Func<byte[]>> change, Func<Notice, Notice>? notice = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var guard = FilePersistence.Lock(PathName);
        Restrict();
        var content = File.Exists(PathName) ? Load() : new Content(1, []);
        var passwords = new Dictionary<string, string>(content.Passwords);
        var key = ReadKey();
        if (key is null && passwords.Count > 0)
            throw new IOException($"Пароли не сохранены: нет файла ключа {KeyPath}, а сохранённые раньше пароли зашифрованы им. Восстановите файл ключа или удалите {PathName}, чтобы начать заново.");
        if (key is not null)
            foreach (var (name, sealedValue) in passwords)
                try { Open(key, name, sealedValue); }
                catch (InvalidDataException ex)
                { throw new InvalidDataException($"Пароли не сохранены: сохранённые раньше пароли не открываются ключом {KeyPath}. Восстановите файлы паролей из копии папки настроек или удалите {PathName} и {KeyPath}, чтобы начать заново.", ex); }
        change(passwords, () => key ??= CreateKey());
        var before = new Notice(content.RetiredTotal ?? 0, content.ShownUpTo ?? 0, content.RetiredGeneration);
        var (total, shown, generation) = notice is null || notice(before) is var changed && changed == before
            ? (content.RetiredTotal, content.ShownUpTo, content.RetiredGeneration) : ((int?)changed.Total, (int?)changed.Shown, changed.Generation);
        if (total == content.RetiredTotal && shown == content.ShownUpTo && generation == content.RetiredGeneration && passwords.Count == content.Passwords.Count && passwords.All(x => content.Passwords.TryGetValue(x.Key, out var v) && v == x.Value)) return;
        // The permissions come from the key file: owner only on Linux, the folder's on Windows.
        FilePersistence.Replace(PathName, JsonSerializer.SerializeToUtf8Bytes(new Content(1, passwords, total, shown, generation), Options),
            File.Exists(KeyPath) ? KeyPath : null);
        Restrict();
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Linux: both files readable by the owner only, also when they came with a copied folder with broader rights.
    /// A file whose rights cannot be narrowed is refused rather than used.</summary>
    private void Restrict()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var file in new[] { KeyPath, PathName })
        {
            if (!File.Exists(file) || File.GetUnixFileMode(file) == OwnerOnly) continue;
            try { File.SetUnixFileMode(file, OwnerOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"Файл паролей доступен не только владельцу, и права не удалось сузить: {file}. Установите права 600 (chmod 600).", ex); }
        }
    }

    private Content Load()
    {
        try
        {
            var content = JsonSerializer.Deserialize<Content>(File.ReadAllBytes(PathName));
            if (content is not { Version: 1, Passwords: not null } || content.Passwords.Any(x => string.IsNullOrEmpty(x.Value)) ||
                content.RetiredTotal < 0 || content.ShownUpTo < 0 || content.ShownUpTo > (content.RetiredTotal ?? 0))
                throw new InvalidDataException();
            return content;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException($"Файл сохранённых паролей повреждён: {PathName}. Восстановите его из копии или удалите и введите пароли заново.", ex); }
    }

    private byte[]? ReadKey()
    {
        if (!File.Exists(KeyPath)) return null;
        var key = File.ReadAllBytes(KeyPath);
        if (key.Length != 32) throw new InvalidDataException($"Файл ключа паролей повреждён: {KeyPath}.");
        return key;
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(KeyPath, options))
        {
            // Creation is subject to umask; the mode is exact before the key is written.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(key); stream.Flush(true);
        }
        return key;
    }

    // nonce (12) | tag (16) | ciphertext; the base and role are authenticated: a value moved to another base does not open.
    private static string Seal(byte[] key, string name, string password)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var output = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes(Associated + name));
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String(output);
    }

    private string Open(byte[] key, string name, string sealedValue)
    {
        try
        {
            var data = Convert.FromBase64String(sealedValue);
            if (data.Length < 28) throw new CryptographicException();
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(Associated + name));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new InvalidDataException($"Сохранённый пароль не расшифрован: файл {PathName} или ключ {KeyPath} изменён. Введите пароль заново.", ex); }
    }
}

/// <summary>A saved proxy password keeps the proxy it was typed for: address, port and user (Grok audit 14.10). It is given only
/// to that proxy; for another one the base has no saved proxy password until it is typed again.</summary>
public static class ProxyPassword
{
    private const char Separator = '\0';

    public static string Pack(BaseEntry proxy, string password) =>
        string.Join(Separator, proxy.ProxyServer.Trim(), proxy.ProxyPort.Trim(), proxy.ProxyUser.Trim(), password);

    public static string? Unpack(BaseEntry proxy, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var parts = stored.Split(Separator, 4);
        return parts.Length == 4 && (parts[0], parts[1], parts[2]) == (proxy.ProxyServer.Trim(), proxy.ProxyPort.Trim(), proxy.ProxyUser.Trim()) &&
            parts[3].Length > 0 ? parts[3] : null;
    }
}

/// <summary>The keys of the page «Аутентификация» for one start.</summary>
public static class LaunchAuthentication
{
    /// <summary>After the connection, before the additional parameters. OS authentication passes /WA+ and nothing else;
    /// a saved user passes /N, his password /P and /WA- (the name typed here is meant, not the OS account); otherwise
    /// «Запрашивать имя и пароль» of the list (WA=0) passes /WA-. The repository is for the configurator only.</summary>
    public static IReadOnlyList<string> Arguments(BaseEntry entry, bool designer, Func<PasswordRole, string?> password)
    {
        var result = new List<string>();
        var auth = entry.Authentication ?? new AuthenticationSettings();
        var (login, user, role) = designer ? (auth.DesignerLogin, auth.DesignerUser, PasswordRole.Designer)
            : (auth.EnterpriseLogin, auth.EnterpriseUser, PasswordRole.Enterprise);
        if (auth.OsAuthentication) result.Add("/WA+");
        else if (login && user.Length > 0)
        {
            result.Add("/N"); result.Add(user);
            if (password(role) is { Length: > 0 } secret) { result.Add("/P"); result.Add(secret); }
            result.Add("/WA-");
        }
        else if (entry.AskCredentials) result.Add("/WA-");
        // The web server asks for the name and password (WSA=1 of the list); without it 1C authenticates by the OS (/WSA+ by default).
        if (!designer && entry.Kind == ConnectionKind.Web && entry.WebServerAskCredentials == true) result.Add("/WSA-");
        if (!designer && entry.Kind == ConnectionKind.Web) result.AddRange(WebConnection(entry, password));
        if (designer && auth.Repository && auth.RepositoryPath.Length > 0)
        {
            result.Add("/ConfigurationRepositoryF"); result.Add(auth.RepositoryPath);
            if (auth.RepositoryUser.Length > 0) { result.Add("/ConfigurationRepositoryN"); result.Add(auth.RepositoryUser); }
            if (password(PasswordRole.Repository) is { Length: > 0 } secret) { result.Add("/ConfigurationRepositoryP"); result.Add(secret); }
        }
        return result;
    }

    /// <summary>«Скорость соединения» and «Использование Proxy» of the page «Подключение» for the thin client: /O Normal|Low;
    /// «Не используется» /NoProxy; «Явное указание настроек» /Proxy -PSrv -PPort [-PUser [-PPwd]]. «Автоматическое определение»
    /// and a record without UseProxy= pass nothing: 1C finds the proxy itself. The password saved here goes before ProxyPassword= of the list.</summary>
    public static IReadOnlyList<string> WebConnection(BaseEntry entry, Func<PasswordRole, string?> password)
    {
        var result = new List<string>();
        if (entry.ConnectionSpeed is { Length: > 0 } speed) { result.Add("/O"); result.Add(speed); }
        if (entry.UseProxy == ProxyMode.None) result.Add("/NoProxy");
        else if (entry.UseProxy == ProxyMode.Manual)
        {
            CheckProxy(entry.ProxyServer, entry.ProxyPort);
            result.AddRange(["/Proxy", "-PSrv", entry.ProxyServer.Trim(), "-PPort", entry.ProxyPort.Trim()]);
            if (entry.ProxyUser.Trim() is { Length: > 0 } user)
            {
                result.Add("-PUser"); result.Add(user);
                if ((ProxyPassword.Unpack(entry, password(PasswordRole.Proxy)) is { Length: > 0 } saved ? saved : entry.ListProxyPassword) is { Length: > 0 } secret)
                { result.Add("-PPwd"); result.Add(secret); }
            }
        }
        return result;
    }

    /// <summary>«Явное указание настроек» needs the address and a port 1–65535.</summary>
    public static void CheckProxy(string server, string port)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(port))
            throw new ArgumentException("Прокси-сервер: укажите адрес и порт или выберите другой способ в «Использование Proxy».");
        if (server.Trim().Any(x => char.IsWhiteSpace(x) || x == '"'))
            throw new ArgumentException("Адрес прокси-сервера не должен содержать пробелы и кавычки.");
        if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            throw new ArgumentException("Порт прокси-сервера — число от 1 до 65535.");
    }

    /// <summary>The same key on the page and in the additional parameters: 1C would take one of the two, the user would not
    /// know which. Refused with the name of the key.</summary>
    public static void CheckConflicts(IReadOnlyList<string> added, IReadOnlyList<string> parameters)
    {
        // The name may be written together with the key (/N"Иванов"), as the original does; /NoProxy is another switch.
        static string? Kind(string arg) => arg.ToUpperInvariant() switch
        {
            "/WA" or "/WA+" or "/WA-" => "/WA",
            "/WSA" or "/WSA+" or "/WSA-" => "/WSA",
            // The settings of /Proxy count as /Proxy: the page passes them together (Codex audit 14.10, P2).
            "/PROXY" or "/NOPROXY" or "-PSRV" or "-PPORT" or "-PUSER" or "-PPWD" or "-PPASSWD" => "/Proxy",
            "/O" => "/O",
            var key when key.StartsWith("/N", StringComparison.Ordinal) => "/N",
            var key when key.StartsWith("/CONFIGURATIONREPOSITORY", StringComparison.Ordinal) => "/ConfigurationRepository",
            _ => null
        };
        // The keys these pages add take a value each, except /WA±, /WSA±, /Proxy and /NoProxy: their values are never keys.
        static bool Bare(string arg) => arg.StartsWith("/WA", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("/WSA", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("/Proxy", StringComparison.OrdinalIgnoreCase) || arg.Equals("/NoProxy", StringComparison.OrdinalIgnoreCase);
        var ours = new HashSet<string>();
        for (var i = 0; i < added.Count; i++)
        {
            if (Kind(added[i]) is { } kind) ours.Add(kind);
            if (!Bare(added[i])) i++;
        }
        // In the parameters the value of a known key (/C /N) is a value, not a switch.
        for (var i = 0; i < parameters.Count; i++)
        {
            if (Kind(parameters[i]) is { } kind && ours.Contains(kind))
                throw new ArgumentException($"Ключ {kind} задан и на странице «{(kind is "/WSA" or "/O" or "/Proxy" ? "Подключение" : "Аутентификация")}», и в дополнительных параметрах запуска. Оставьте его в одном месте.");
            if (LaunchParameters.TakesValue(parameters[i])) i++;
        }
    }

    /// <summary>Adds the keys to a start built for the connection: right after its first three arguments.</summary>
    public static void Apply(ProcessStartInfo start, BaseEntry entry, bool designer, Func<PasswordRole, string?> password)
    {
        var added = Arguments(entry, designer, password);
        if (added.Count == 0) return;
        CheckConflicts(added, start.ArgumentList.Skip(3).ToList());
        for (var i = 0; i < added.Count; i++) start.ArgumentList.Insert(3 + i, added[i]);
    }
}

/// <summary>The passwords of the original StartManager (v8config.smc), its «old» method (NewMethodEncryption off), as in
/// its code (archive/legacy/Projects/_Unit186.pas, sub_00847BD4 and sub_00847D48), over single-byte Windows-1251 codes:
/// a password with a Cyrillic letter (0xC0–0xFF) is «~» + a stream cipher (state 0x1D7, ×0xF2BE, +0x2DC7, 32-bit);
/// any other is shifted by the digits of the key SLAVKA240601 (digits and 0x21–0x7A; other bytes +0x30 without the key).
/// Decryption is the original's own: it subtracts the key from every byte, so a space it stored as «P» comes back as the
/// original reads it. Only read for the import; new passwords go to <see cref="PasswordVault"/>.</summary>
public static class LegacyPasswords
{
    private const string Key = "SLAVKA240601";
    private const uint Seed = 0x1D7, Multiplier = 0xF2BE, Increment = 0x2DC7;

    private static Encoding Ansi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    private static int Shift(int index) => Key[index % Key.Length] - '0';

    public static string Decrypt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var data = Ansi().GetBytes(value);
        if (data[0] == (byte)'~')
        {
            // sub_00847B2C: the state follows the encrypted bytes.
            var plain = new byte[data.Length - 1]; var state = Seed;
            for (var i = 1; i < data.Length; i++)
            {
                plain[i - 1] = (byte)(data[i] ^ (byte)(state >> 8));
                state = unchecked((state + data[i]) * Multiplier + Increment);
            }
            return Ansi().GetString(plain);
        }
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((data[i] - Shift(i) + 0x100) & 0xFF);
        return Ansi().GetString(data);
    }

    /// <summary>The original's encryption, for tests that build its files with a known password.</summary>
    public static string Encrypt(string value)
    {
        var data = Ansi().GetBytes(value);
        if (data.Any(x => x >= 0xC0))
        {
            // sub_00847A7C: the state follows the bytes it writes.
            var output = new byte[data.Length + 1]; output[0] = (byte)'~'; var state = Seed;
            for (var i = 0; i < data.Length; i++)
            {
                output[i + 1] = (byte)(data[i] ^ (byte)(state >> 8));
                state = unchecked((state + output[i + 1]) * Multiplier + Increment);
            }
            return Ansi().GetString(output);
        }
        for (var i = 0; i < data.Length; i++)
            data[i] = data[i] is >= 0x30 and <= 0x39 or >= 0x21 and < 0x7B ? (byte)((data[i] + Shift(i)) % 0x100) : (byte)(data[i] + 0x30);
        return Ansi().GetString(data);
    }
}
