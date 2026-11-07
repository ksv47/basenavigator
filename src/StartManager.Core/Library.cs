using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

/// <summary>Web — a base published on a web server (ws= in .v8i): the thin client connects by its address, a browser opens it.</summary>
public enum ConnectionKind { File, Server, Web }
public enum AfterLaunchAction { KeepWindow, Minimize, Close }
public enum DoubleClickAction { Enterprise, Designer }
/// <summary>The double click of one base (the original's «Прочие свойства», ActionDblClick 1–4 and 6; «Snegopat» is not supported).</summary>
public enum BaseDoubleClick { Enterprise, EnterpriseLogin, EnterpriseParameters, Designer, Properties }
/// <summary>Automatic update of the program, as in the original «Автообновление»; Off is the default.</summary>
public enum UpdateMode { Off, Notify, Download }
/// <summary>«Использование Proxy» of the original's page «Подключение», in the order of its list (UseProxy= of the .v8i).</summary>
public enum ProxyMode { None, Auto, Manual }

public sealed record BaseEntry
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public ConnectionKind Kind { get; init; }
    public string Location { get; init; } = "";
    public string Group { get; init; } = "";
    public string Executable { get; init; } = "";
    public string PlatformVersion { get; init; } = "";
    public PlatformRule? PlatformRule { get; init; }
    public string Comment { get; init; } = "";
    /// <summary>«Конфигурация:» of the page «Сведения» (edtConfig of the original, its Descr from v8updates.lst): typed by hand
    /// until the choice from the list of updates is made (decision of the user 14.10.2026). Local, like the comment.</summary>
    public string Configuration { get; init; } = "";
    /// <summary>«Релиз:» of the page «Сведения» (ConfigVersion of the original's v8config.smc, _Unit205.pas:25813): shown after the
    /// name in the list as « [релиз]». Any text, as the original does not check its form. Local, like the comment.</summary>
    public string ConfigurationRelease { get; init; } = "";
    /// <summary>ConfigId of the original (the section of its v8updates.lst): kept for the check of updates, not shown. Local.</summary>
    public string ConfigurationId { get; init; } = "";
    /// <summary>The name as the list shows it: as the original (_Unit207.pas:45478), « [релиз]» follows a set release.</summary>
    [JsonIgnore] public string ListName => ConfigurationRelease.Trim() is { Length: > 0 } release ? Name + " [" + release + "]" : Name;
    /// <summary>«Дополнительные параметры» kept in the profile: a base without a list file, a read-only list, or a string saved
    /// by an earlier build (moved to the list when the properties are saved). Null uses <see cref="ListParameters"/>; an empty
    /// string is an explicit local override.</summary>
    public string? AdditionalParameters { get; init; }
    /// <summary>AdditionalParameters= of the .v8i record: read from and written to the list, as the original does (1CEStart reads it too).</summary>
    [JsonIgnore] public string ListParameters { get; init; } = "";
    [JsonIgnore] public string EffectiveParameters => AdditionalParameters ?? ListParameters;
    public string ExternalProcessing { get; init; } = "";
    /// <summary>The page «Аутентификация» (names, OS authentication, repository); null when nothing is set. Local.</summary>
    public AuthenticationSettings? Authentication { get; init; }
    /// <summary>«Запрашивать имя и пароль» (WA=0 of the .v8i record) instead of «Выбирать автоматически» (WA=1 or none).
    /// Read from and written to the shared list; kept in the settings for a local list.</summary>
    public bool AskCredentials { get; init; }
    /// <summary>«Способ аутентификации» of the page «Подключение» for a base on a web server: WSA=1 «Запрашивать имя и пароль»
    /// (the thin client gets /WSA-), WSA=0 «Выбирать автоматически»; null when the record has no usable WSA= (the page shows
    /// «Запрашивать», as the original does; nothing is passed). Read from and written to the shared list.</summary>
    public bool? WebServerAskCredentials { get; init; }
    /// <summary>«Скорость соединения» of a base on a web server: ClientConnectionSpeed= of the list, «Normal» or «Low» (the thin
    /// client gets /O); empty is «Выбирать при запуске», a record without the line; null is a line with another value (the original
    /// wrote the proxy address there): nothing is passed and the line stays until a speed is chosen. Read from and written to the shared list.</summary>
    public string? ConnectionSpeed { get; init; } = "";
    /// <summary>«Использование Proxy» of a base on a web server: UseProxy= of the list (0, 1, 2 as the original writes it); null when
    /// the record has no usable UseProxy=, which passes nothing (1C finds the proxy itself). Read from and written to the shared list.</summary>
    public ProxyMode? UseProxy { get; init; }
    /// <summary>ProxyServer=, ProxyPort=, ProxyUser= of the list: the proxy of «Явное указание настроек». Its password is saved in
    /// <see cref="PasswordVault"/> (<see cref="PasswordRole.Proxy"/>); a ProxyPassword= already in the list is read only.</summary>
    public string ProxyServer { get; init; } = "";
    public string ProxyPort { get; init; } = "";
    public string ProxyUser { get; init; } = "";
    /// <summary>ProxyPassword= of the .v8i record (the original writes it in plain text). Read only: the line stays as it is; a password
    /// saved by this program goes first.</summary>
    [JsonIgnore] public string ListProxyPassword { get; init; } = "";
    /// <summary>UseProxy= of the .v8i record as written, to tell a value 1C does not know from no line. Read only.</summary>
    [JsonIgnore] public string ListUseProxy { get; init; } = "";
    /// <summary>«Запретить удаление из списка баз» (DisallowDelete of the original). Local: guards this program's commands only.</summary>
    public bool DisallowDelete { get; init; }
    /// <summary>«Запуск от имени администратора» of the page «Сведения» (RunAsAdmin of the original): every start of the base is
    /// elevated. Local; Windows only.</summary>
    public bool RunAsAdmin { get; init; }
    /// <summary>«Перед запуском ИБ проверять версию Агента сервера» of the page «Подключение» (CheckRAgent of the original): before a
    /// server base starts, the client version is compared with the server agent of this computer (<see cref="ServerAgents"/>).
    /// Local; Windows only.</summary>
    public bool CheckServerAgent { get; init; }
    /// <summary>«Толстый клиент (УП)» of «Режим» (App=3 of the original's own settings): with App=ThickClient the base starts with
    /// /RunModeManagedApplication; without it App=ThickClient is «Толстый клиент (ОП)» and starts with /RunModeOrdinaryApplication.
    /// Local: the list keeps only App=ThickClient, as the original writes it.</summary>
    public bool ManagedApplication { get; init; }
    /// <summary>«Действие при двойном щелчке» of this base; null uses the general setting. Local.</summary>
    public BaseDoubleClick? DoubleClick { get; init; }
    /// <summary>App= of the .v8i record (Auto, ThinClient, ThickClient, WebClient). Read only: the line stays in the file as it is.</summary>
    /// <summary>App= of the list (Auto/ThinClient/ThickClient/WebClient): «Режим» of the properties. A shared list keeps it in the
    /// record, a profile without a list in the profile.</summary>
    public string ClientType { get; init; } = "";
    /// <summary>«Сценарий:» of the page «Резервное копирование» (BackupScriptID of the original); null uses the default scenario,
    /// as does an ID whose scenario was removed (<see cref="BackupScenarioSet.For"/>). Local, like the comment.</summary>
    public Guid? BackupScriptId { get; init; }
    /// <summary>«Каталог:» of the page «Резервное копирование» (BackupFolder): where backups of the base go; a relative path is
    /// inside the general «Каталог по-умолчанию» (<see cref="BackupTarget.DefaultFolder"/>). Local.</summary>
    public string BackupFolder { get; init; } = "";
    /// <summary>«Префикс:» of the page «Резервное копирование» (BackupPrefix): the macro {Prefix} of the file name. Local.</summary>
    public string BackupPrefix { get; init; } = "";
    /// <summary>AppArch= of the .v8i record as a preference (PreferX86/PreferX64), Any without it. Read only, like App=.</summary>
    [JsonIgnore] public PlatformArchitecture ListArchitecture { get; init; }
    public override string ToString() => Name;

    public void Validate()
    {
        PlatformRule?.Validate();
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Введите название базы.");
        if (string.IsNullOrWhiteSpace(Location)) throw new ArgumentException("Укажите каталог базы или сервер\\базу.");
        if (Group is null || Executable is null || PlatformVersion is null || Comment is null ||
            ListParameters is null || ExternalProcessing is null || Group != Groups.Normalize(Group))
            throw new ArgumentException("Повреждены свойства подключения.");
        if (Configuration is null || ConfigurationRelease is null || ConfigurationId is null ||
            new[] { Configuration, ConfigurationRelease, ConfigurationId }.Any(x => x.Any(char.IsControl)))
            throw new ArgumentException("Сведения о конфигурации содержат недопустимые символы.");
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Неизвестный тип подключения.");
        if (DoubleClick is { } action && !Enum.IsDefined(action)) throw new ArgumentException("Неизвестное действие при двойном щелчке.");
        if (UseProxy is { } proxy && !Enum.IsDefined(proxy)) throw new ArgumentException("Неизвестный способ использования прокси.");
        if (ConnectionSpeed is not (null or "" or "Normal" or "Low")) throw new ArgumentException("Неизвестная скорость соединения.");
        if (ProxyServer is null || ProxyPort is null || ProxyUser is null || ListProxyPassword is null || ListUseProxy is null ||
            new[] { ProxyServer, ProxyPort, ProxyUser }.Any(x => x.Any(char.IsControl)))
            throw new ArgumentException("Повреждены параметры прокси-сервера.");
        if (BackupFolder is null || BackupPrefix is null || BackupFolder.Any(char.IsControl) || BackupPrefix.Any(char.IsControl))
            throw new ArgumentException("Каталог и префикс резервного копирования не должны содержать переносы строк и управляющие символы.");
        Authentication?.Validate();
        if (Location.Any(char.IsControl) || Location.Contains('"'))
            throw new ArgumentException("Адрес содержит недопустимые символы.");
        if (Kind == ConnectionKind.Server && (Location.IndexOf('\\') <= 0 || Location.EndsWith('\\')))
            throw new ArgumentException("Серверная база: сервер\\имя_базы (например, srv1c\\accounting).");
        if (Kind == ConnectionKind.Web && !IsWebAddress(Location))
            throw new ArgumentException("База на веб-сервере: адрес http:// или https://, например https://server/accounting.");
    }

    /// <summary>What a double click does with this base: its own choice, else the general one. The configurator does not
    /// connect to a base on a web server, so there it opens 1С:Предприятие, as the general setting always did.</summary>
    public BaseDoubleClick DoubleClickFor(DoubleClickAction general)
    {
        var action = DoubleClick ?? (general == DoubleClickAction.Designer ? BaseDoubleClick.Designer : BaseDoubleClick.Enterprise);
        return action == BaseDoubleClick.Designer && Kind == ConnectionKind.Web ? BaseDoubleClick.Enterprise : action;
    }

    public static bool IsWebAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    /// <summary>Two addresses of one publication. Only the proven equivalents are ignored: the case of the scheme and host,
    /// the default port and a final «/». The path and the query keep their case: a web server may publish /ERP and /erp as
    /// different bases, and the cache cleaning must never take the folder of another base.</summary>
    public static bool SameWebAddress(string left, string right)
    {
        if (!Uri.TryCreate(left.Trim(), UriKind.Absolute, out var a) || !Uri.TryCreate(right.Trim(), UriKind.Absolute, out var b)) return false;
        static string Path(Uri uri) => uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "";
        return a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) &&
            a.Port == b.Port && Path(a).Equals(Path(b), StringComparison.Ordinal) && a.Query.Equals(b.Query, StringComparison.Ordinal);
    }
}

public sealed record Library
{
    public bool PreventMultipleInstances { get; init; }
    public AfterLaunchAction AfterLaunch { get; init; }
    public DoubleClickAction DoubleClick { get; init; }
    public UpdateMode UpdateMode { get; init; }
    /// <summary>owner/name of another GitHub repository with releases; empty — the default one.</summary>
    public string UpdateRepository { get; init; } = "";
    /// <summary>«Хранить прежних версий»: how many earlier versions the archive of the program keeps (1–10). A profile of an
    /// older build has no such field and gets the default.</summary>
    public int KeepVersions { get; init; } = Updater.DefaultKeep;
    public PlatformRule? PlatformRule { get; init; }
    public int Version { get; init; } = 1;
    public string Executable { get; init; } = "";
    /// <summary>Folders the program searches for installed platforms besides the standard ones (settings, «Используемая платформа»;
    /// the original's «Пути поиска платформы» of the page «Исполняемые файлы»). Full paths, without repeats; kept in this profile.</summary>
    public List<string> PlatformFolders { get; init; } = [];
    /// <summary>«Использовать общие списки информационных баз» (UseCommonBases of the original): the bases of the common lists of
    /// 1CEStart are shown after the open list, read only (<see cref="CommonLists"/>).</summary>
    public bool UseCommonLists { get; init; }
    /// <summary>«Игнорировать файлы списка баз, имеющих атрибут „Только чтение“» (V8iIgnorReadOnlyFiles): such a common list is not loaded.</summary>
    public bool SkipReadOnlyCommonLists { get; init; }
    /// <summary>«Основная версия платформы» (V8DefVersion): the branch of a record without Version=; empty — DefaultVersion of 1CEStart.</summary>
    public string MainVersion { get; init; } = "";
    /// <summary>«Единый каталог информационных баз» (V8DefaultFolder): where a new file base is proposed; empty — none.</summary>
    public string BasesFolder { get; init; } = "";
    /// <summary>«Каталог по-умолчанию:» of «Параметры резервного копирования» (BackupFolder of the original's settings.cnf):
    /// the base of a relative folder of a base and the last choice of <see cref="BackupTarget.DefaultFolder"/>; empty — none.</summary>
    public string BackupFolder { get; init; } = "";
    /// <summary>«Настройки платформы» by branch (<see cref="PlatformBranches"/>); a branch without an entry uses the general setting.</summary>
    public List<BranchSettings> Branches { get; init; } = [];
    /// <summary>The original's «Кнопку "1С:Стартер"» (ShowStarterBtn) switched off; shown by default.</summary>
    public bool HideStarterButton { get; init; }
    public List<BaseEntry> Bases { get; init; } = [];
    public List<string> Groups { get; init; } = [];
    /// <summary>«Каталог» of the groups (the original's Directory of a group in v8config.smc): a local setting of this profile, kept
    /// by the list it belongs to; the list file itself has no place for it.</summary>
    public List<GroupFolder> GroupFolders { get; init; } = [];
    public string ListPath { get; init; } = "";
    public Dictionary<string, SourceSettings> Sources { get; init; } = [];
    public AppearanceSettings Appearance { get; init; } = new();
    public JsonElement? Diagnostics { get; init; }
    /// <summary>Where the file was last written; detects a moved portable folder.</summary>
    public string? ProfilePath { get; init; }
    /// <summary>Move detected by this load; never serialized.</summary>
    [JsonIgnore] public ProfileRelocation? Moved { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record SourceSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public List<BaseEntry> Bases { get; init; } = [];
    public List<string> Groups { get; init; } = [];
    public List<GroupFolder> GroupFolders { get; init; } = [];
}

/// <summary>The folder associated with a group (its full path in the tree): opened by «Открыть каталог» and proposed for a new
/// file base of the group.</summary>
public sealed record GroupFolder
{
    public string Group { get; init; } = "";
    public string Folder { get; init; } = "";
    /// <summary>The ID of the group's record in the .v8i, as the original keys it: the folder follows a group renamed by another
    /// program and is not given to another group made later with the same name. Empty for a group without its own record.</summary>
    public Guid? Id { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public static class BaseEdit
{
    /// <summary>The fields «Кластер серверов» and «Имя инф. базы в кластере» of a server base kept as server\base: the server
    /// before the first backslash, as <see cref="BaseClipboard.ConnectionString"/> splits it.</summary>
    public static (string Cluster, string Infobase) SplitServer(string location)
    {
        var split = location.IndexOf('\\');
        return split < 0 ? (location, "") : (location[..split], location[(split + 1)..]);
    }

    /// <summary>The location of a server base from its two fields; both are required and the server has no backslash. Fields left
    /// as <paramref name="original"/> gave them return it as <see cref="AsDialogReturns"/> compares it: trimming each part on its own
    /// would count as a change of the connection and go over one changed in another window (Codex audit 13.10).</summary>
    public static string ServerLocation(string cluster, string infobase, string? original = null)
    {
        if (original is not null && (cluster, infobase) == SplitServer(original)) return original.Trim();
        cluster = cluster.Trim(); infobase = infobase.Trim();
        if (cluster.Length == 0 || infobase.Length == 0) throw new ArgumentException("Укажите кластер серверов и имя информационной базы в кластере.");
        if (cluster.Contains('\\')) throw new ArgumentException("Кластер серверов: укажите сервер без «\\» (имя базы — в отдельном поле).");
        return cluster + "\\" + infobase;
    }

    /// <summary>The bases of <paramref name="current"/> whose removal is forbidden in their properties and which
    /// <paramref name="next"/> no longer has: a command that replaces the whole list refuses them.</summary>
    public static IReadOnlyList<BaseEntry> ForbiddenRemovals(IEnumerable<BaseEntry> current, IEnumerable<BaseEntry> next)
    {
        var kept = next.Select(x => x.Id).ToHashSet();
        return [.. current.Where(x => x.DisallowDelete && !kept.Contains(x.Id))];
    }

    /// <summary>Bases read from a list text that carries no local settings (a shared list, the original's snapshot): a base
    /// with the same ID keeps the client, the version rule, the page «Прочие свойства» and the unknown fields it has now.</summary>
    public static List<BaseEntry> KeepLocal(IEnumerable<BaseEntry> read, IEnumerable<BaseEntry> current)
    {
        var now = current.ToDictionary(x => x.Id);
        return [.. read.Select(x => now.TryGetValue(x.Id, out var was)
            ? x with { Executable = was.Executable, PlatformRule = was.PlatformRule, DisallowDelete = was.DisallowDelete, DoubleClick = was.DoubleClick, RunAsAdmin = was.RunAsAdmin, CheckServerAgent = was.CheckServerAgent, ManagedApplication = was.ManagedApplication,
                Configuration = was.Configuration, ConfigurationRelease = was.ConfigurationRelease, ConfigurationId = was.ConfigurationId,
                BackupScriptId = was.BackupScriptId, BackupFolder = was.BackupFolder, BackupPrefix = was.BackupPrefix, Extra = was.Extra } : x)];
    }

    /// <summary>An edit made in a dialog opened before the list was read again (after a conflict) applied to the record as it
    /// is now: what the user changed in the dialog wins, what another program changed and the user did not stays. Null when
    /// the base is no longer in the list.</summary>
    public static BaseEntry? OnCurrent(IEnumerable<BaseEntry> bases, BaseEntry original, BaseEntry edited)
    {
        if (bases.FirstOrDefault(x => x.Id == original.Id) is not { } now) return null;
        // Every field the user did not change is taken as it is now: local settings changed in another window or by another program
        // meanwhile (comment, parameters, authentication…) are not overwritten by the values the dialog was opened with.
        T Pick<T>(Func<BaseEntry, T> field) => EqualityComparer<T>.Default.Equals(field(edited), field(original)) ? field(now) : field(edited);
        var connection = edited.Kind != original.Kind || edited.Location != original.Location;
        // «Дополнительные параметры» are one choice: where the string is kept (profile or list) and the string itself. The string the
        // user typed wins; a string left as shown only moves from the profile to the list (WithParameters) while both are as the dialog
        // saw them: after another window or program saved the parameters meanwhile, the old dialog keeps theirs (Codex audit 27.10, P2).
        var typed = edited.EffectiveParameters.Trim() != original.EffectiveParameters.Trim();
        var unchanged = now.AdditionalParameters == original.AdditionalParameters && now.ListParameters == original.ListParameters;
        var parameters = typed || unchanged ? edited : now;
        var mode = edited.ClientType != original.ClientType || edited.ManagedApplication != original.ManagedApplication;
        var proxy = edited.UseProxy != original.UseProxy || edited.ProxyServer != original.ProxyServer ||
            edited.ProxyPort != original.ProxyPort || edited.ProxyUser != original.ProxyUser;
        // A changed rule keeps the unknown fields of the rule as it is now (Codex audit 12.10): only its version and bitness are edited.
        var rule = Pick(x => x.PlatformRule);
        if (!Equals(edited.PlatformRule, original.PlatformRule) && rule is not null && now.PlatformRule is { } current &&
            ReferenceEquals(rule.Extra, original.PlatformRule?.Extra))
            rule = rule with { Extra = current.Extra };
        // The same for the page «Аутентификация» (Grok audit of the fixes 25.10, P3): a changed known field keeps the unknown
        // fields another window has saved meanwhile.
        var authentication = Pick(x => x.Authentication);
        if (!Equals(edited.Authentication, original.Authentication) && authentication is not null && now.Authentication is { } nowAuthentication &&
            ReferenceEquals(authentication.Extra, original.Authentication?.Extra))
            authentication = authentication with { Extra = nowAuthentication.Extra };
        return now with
        {
            Name = Pick(x => x.Name), Group = Pick(x => x.Group),
            Kind = connection ? edited.Kind : now.Kind, Location = connection ? edited.Location : now.Location,
            Executable = Pick(x => x.Executable), PlatformRule = rule, Comment = Pick(x => x.Comment),
            Configuration = Pick(x => x.Configuration), ConfigurationRelease = Pick(x => x.ConfigurationRelease), ConfigurationId = Pick(x => x.ConfigurationId),
            AdditionalParameters = parameters.AdditionalParameters, ListParameters = parameters.ListParameters, ExternalProcessing = Pick(x => x.ExternalProcessing),
            Authentication = authentication, AskCredentials = Pick(x => x.AskCredentials),
            WebServerAskCredentials = Pick(x => x.WebServerAskCredentials), ConnectionSpeed = Pick(x => x.ConnectionSpeed),
            // The proxy is one choice: the way and its address, port and user go together.
            UseProxy = proxy ? edited.UseProxy : now.UseProxy, ProxyServer = proxy ? edited.ProxyServer : now.ProxyServer,
            ProxyPort = proxy ? edited.ProxyPort : now.ProxyPort, ProxyUser = proxy ? edited.ProxyUser : now.ProxyUser, DisallowDelete = Pick(x => x.DisallowDelete), RunAsAdmin = Pick(x => x.RunAsAdmin), CheckServerAgent = Pick(x => x.CheckServerAgent),
            // «Режим» is one choice: App= and «(УП)» go together, from the edit when either changed (Codex audit 12.10).
            ClientType = mode ? edited.ClientType : now.ClientType, ManagedApplication = mode ? edited.ManagedApplication : now.ManagedApplication,
            DoubleClick = Pick(x => x.DoubleClick),
            BackupScriptId = Pick(x => x.BackupScriptId), BackupFolder = Pick(x => x.BackupFolder), BackupPrefix = Pick(x => x.BackupPrefix),
            Extra = Pick(x => x.Extra)
            // Read from the list only (PlatformVersion, ListArchitecture): always as it is now.
        };
    }

    /// <summary>The base as the properties dialog gives it back when nothing is changed: the name, folder, group, client, external
    /// processing and authentication trimmed, empty authentication as none. <see cref="OnCurrent"/> compares the dialog's result with it, so a value the dialog only
    /// normalised does not count as the user's change (Codex audit 12.10).</summary>
    public static BaseEntry AsDialogReturns(BaseEntry entry) => entry with
    {
        Name = entry.Name.Trim(), Location = entry.Location.Trim(), Group = Groups.Normalize(entry.Group),
        Executable = entry.Executable.Trim(), ExternalProcessing = entry.ExternalProcessing.Trim(),
        Configuration = entry.Configuration.Trim(), ConfigurationRelease = entry.ConfigurationRelease.Trim(),
        ProxyServer = entry.ProxyServer.Trim(), ProxyPort = entry.ProxyPort.Trim(), ProxyUser = entry.ProxyUser.Trim(),
        BackupFolder = entry.BackupFolder.Trim(), BackupPrefix = entry.BackupPrefix.Trim(),
        Authentication = entry.Authentication is { } a && (a with { EnterpriseUser = a.EnterpriseUser.Trim(), DesignerUser = a.DesignerUser.Trim(),
            RepositoryPath = a.RepositoryPath.Trim(), RepositoryUser = a.RepositoryUser.Trim() }) is var trimmed && !trimmed.IsEmpty ? trimmed : null
    };

    /// <summary>«Дополнительные параметры» of the properties applied to the base. A writable list keeps the string in its record
    /// (AdditionalParameters=), and a string of the profile left by an earlier build moves there: the profile gets null. Otherwise
    /// (no list file, a read-only list) the profile keeps it; a string left as the list gives it keeps inheriting the list.</summary>
    public static BaseEntry WithParameters(BaseEntry entry, string text, bool inList)
    {
        if (inList) return entry with { AdditionalParameters = null, ListParameters = text.Trim() };
        return entry.AdditionalParameters is null && text == entry.ListParameters ? entry : entry with { AdditionalParameters = text };
    }

    /// <summary>The proxy a saved proxy password belongs to: its address, port and user as the dialog returns them.</summary>
    public static bool SameProxy(BaseEntry left, BaseEntry right) =>
        (left.ProxyServer.Trim(), left.ProxyPort.Trim(), left.ProxyUser.Trim()) == (right.ProxyServer.Trim(), right.ProxyPort.Trim(), right.ProxyUser.Trim());

    /// <summary>«Изменить каталог» of a file base applied to the record as it is now: only the folder changes, every other field
    /// (local settings changed while the folder was chosen included) stays as it is now. Null when the base is no longer in the
    /// list or no longer a file base.</summary>
    public static BaseEntry? WithFolder(IEnumerable<BaseEntry> bases, Guid id, string folder) =>
        bases.FirstOrDefault(x => x.Id == id) is { Kind: ConnectionKind.File } now ? now with { Location = folder } : null;

    /// <summary>The same folder as the file system compares it: case matters on Linux; a trailing separator does not, a root keeps its own.
    /// Spaces are kept: on Linux «/data/erp » is another folder.</summary>
    public static bool SameFolder(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static class LibrarySources
{
    /// <summary>The key of the open list in <see cref="Library.Sources"/>, the history and the saved passwords.</summary>
    public static string Key(Library library, string settingsPath)
    {
        try { return ActivityStore.Source(settingsPath, library.ListPath); }
        catch (Exception ex) when (!string.IsNullOrEmpty(library.ListPath) && ex is ArgumentException or PathTooLongException)
        {
            // Keep recoverable data from a malformed legacy path without attributing it to the local list.
            return "invalid:" + library.ListPath;
        }
    }

    public static Library Remember(Library library, string settingsPath)
    {
        var sources = new Dictionary<string, SourceSettings>(library.Sources);
        var key = Key(library, settingsPath);
        sources.TryGetValue(key, out var previous);
        sources[key] = (previous ?? new SourceSettings()) with
        { Bases = [.. library.Bases], Groups = [.. library.Groups], GroupFolders = [.. library.GroupFolders] };
        return library with { Sources = sources };
    }

    public static Library Select(Library library, string settingsPath, string listPath)
    {
        library = Remember(library, settingsPath);
        var key = ActivityStore.Source(settingsPath, listPath);
        library.Sources.TryGetValue(key, out var saved);
        if (saved is null && string.IsNullOrEmpty(listPath))
            throw new InvalidOperationException("Сохранённый локальный список не найден. Выберите нужный список и создайте ярлык заново.");
        return library with { ListPath = listPath, Bases = saved is null ? [] : [.. saved.Bases], Groups = saved is null ? [] : [.. saved.Groups],
            GroupFolders = saved is null ? [] : [.. saved.GroupFolders] };
    }
}

public sealed class LibraryStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private byte[]? observed;
    private bool hasObserved;
    public string PathName => path;
    /// <summary>The last save of a moved profile could not move the saved passwords: the text for the status line, else null.</summary>
    public string? RelocationWarning { get; private set; }
    // Reads the portable marker; callers must report I/O/access failures instead of silently changing profiles.
    public static string DefaultPath => new SettingsStorage().SettingsPath;

    /// <summary>List opened by a profile that does not exist yet; set by the application, not by tests.</summary>
    public static string? FirstRunListPath { get; set; }

    public Library Load()
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var library = bytes is null ? FirstRun() : Parse(bytes);
        if (ProfileRelocation.Detect(library.ProfilePath, library.Sources.Keys, path) is { } moved) library = moved.Apply(library);
        // An empty local list shows nothing useful: open the standard 1CEStart list instead (in memory until saved).
        if (bytes is not null && library.ListPath.Length == 0 && library.Bases.Count == 0 && library.Groups.Count == 0 &&
            FirstRunListPath is { } standard && File.Exists(standard))
        {
            var sources = new Dictionary<string, SourceSettings>(library.Sources);
            sources.TryAdd(ActivityStore.Source(path, ""), new SourceSettings());
            library = library with { ListPath = standard, Sources = sources };
        }
        observed = bytes; hasObserved = true;
        PlatformCatalog.UseFolders(library);
        return library;
    }

    // The empty local source keeps "--list \"\"" and the local-list setting available for the new profile.
    private Library FirstRun() => FirstRunListPath is { } list && File.Exists(list)
        ? new Library { ListPath = list, Sources = new() { [ActivityStore.Source(path, "")] = new SourceSettings() } }
        : new Library();

    /// <summary>The profile as stored, without first-run defaults or relocation; null when it does not exist.</summary>
    public static Library? ReadStored(string path) => File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;

    /// <summary>As <see cref="ReadStored"/>, but only a profile that truly is not there (the file or its folder missing) is null:
    /// the file is read right away, since File.Exists answers «no» also when access is denied. Any other failure (no access, I/O,
    /// a broken file) is thrown, for a caller that must not take an unreadable profile for an absent one (Codex re-check 6, RC6-2).</summary>
    public static Library? ReadExisting(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        return Parse(bytes);
    }

    private static Library Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes));
        Library library;
        try
        {
            library = JsonSerializer.Deserialize<Library>(reader.ReadToEnd(), Options)
                ?? throw new InvalidDataException("Файл настроек пуст.");
        }
        catch (JsonException ex)
        { throw new InvalidDataException("Файл настроек содержит некорректный JSON или значения неподдерживаемого типа.", ex); }
        Validate(library);
        return library;
    }

    public static void Validate(Library library)
    {
        if (!Enum.IsDefined(library.UpdateMode) || library.UpdateRepository is null ||
            library.UpdateRepository.Length > 0 && !GitHubReleaseSource.IsRepository(library.UpdateRepository) ||
            library.KeepVersions is < 1 or > Updater.MaxKeep)
            throw new InvalidDataException("Неизвестные настройки автообновления.");
        if (!Enum.IsDefined(library.AfterLaunch) || !Enum.IsDefined(library.DoubleClick))
            throw new InvalidDataException("Неизвестное действие в общих настройках.");
        library.PlatformRule?.Validate();
        if (library.Version != 1) throw new InvalidDataException("Неподдерживаемая версия настроек.");
        if (library.Appearance is null) throw new InvalidDataException("Повреждены настройки оформления.");
        library.Appearance.Validate();
        if (library.Bases is null || library.Executable is null || library.Groups is null || library.ListPath is null || library.Sources is null ||
            library.PlatformFolders is null || library.PlatformFolders.Any(x => x is null) || library.MainVersion is null || library.BasesFolder is null || library.BackupFolder is null ||
            library.Branches is null || library.Branches.Any(x => x is null)) throw new InvalidDataException("Повреждён файл настроек.");
        if (library.GroupFolders is null || library.GroupFolders.Any(x => x is null || x.Group is null || x.Folder is null))
            throw new InvalidDataException("Повреждены каталоги групп.");
        if (library.MainVersion.Length > 0 && !PlatformBranches.IsBranch(library.MainVersion)) throw new InvalidDataException("Неизвестная основная версия платформы.");
        if (library.BackupFolder.Any(char.IsControl)) throw new InvalidDataException("Каталог резервных копий по умолчанию содержит недопустимые символы.");
        foreach (var branch in library.Branches) branch.Validate();
        if (library.Branches.GroupBy(x => x.Branch).Any(x => x.Count() > 1)) throw new InvalidDataException("Повторяются настройки одной ветки платформы.");
        foreach (var (key, source) in library.Sources)
        {
            if (string.IsNullOrWhiteSpace(key) || source is null) throw new InvalidDataException("Повреждены настройки источников.");
            Validate(new Library { Bases = source.Bases, Groups = source.Groups, GroupFolders = source.GroupFolders });
        }
        if (library.Groups.Any(x => string.IsNullOrWhiteSpace(x) || x != Groups.Normalize(x)) || library.Groups.Distinct(StringComparer.OrdinalIgnoreCase).Count() != library.Groups.Count)
            throw new InvalidDataException("Повреждён список групп.");
        if (library.Bases.Any(x => x is null)) throw new InvalidDataException("Повреждён список баз.");
        foreach (var entry in library.Bases) entry.Validate();
        if (library.Bases.Select(x => x.Id).Distinct().Count() != library.Bases.Count)
            throw new InvalidDataException("Повторяющиеся идентификаторы баз.");
    }

    public void Save(Library library)
    {
        Validate(library);
        var relocation = library.Moved;
        // The history still needs the old location to follow the move; migrate it before the anchor is replaced.
        if (library.Moved is { } moved && File.Exists(new ActivityStore(path).PathName)) new ActivityStore(path).Adopt(moved);
        // The saved passwords follow the new list keys too. A vault that cannot be changed now does not hold the profile back,
        // but the profile keeps its old anchor until it has: the next load sees the move again, the passwords are still found
        // under the old keys and the vault is tried again (Codex re-check 25.10, R2).
        RelocationWarning = null;
        var anchor = Path.GetFullPath(path);
        if (library.Moved is { } passwordsMove)
            try { new PasswordVault(path).Adopt(passwordsMove); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                anchor = library.ProfilePath ?? anchor;
                RelocationWarning = "Сохранённые пароли не перенесены к новому месту папки настроек: " + LegacyImport.ErrorMessage(ex) +
                    " Перенос повторится при следующем сохранении или запуске.";
            }
        library = LibrarySources.Remember(library, path) with { ProfilePath = anchor, Moved = null };
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        using var guard = FilePersistence.Lock(path);
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (hasObserved && (current is null != (observed is null) ||
            current is not null && !current.AsSpan().SequenceEqual(observed)))
            throw new IOException("Настройки изменены другим экземпляром программы. Обновите список перед сохранением.");
        // Never overwrite a damaged settings file, including when startup Load failed.
        var previous = current is null ? null : Parse(current);
        if (previous is not null && relocation is not null) previous = relocation.Apply(previous);
        ValidateChangedParameters(library, previous);
        var written = JsonSerializer.SerializeToUtf8Bytes(library, Options);
        FilePersistence.Replace(FilePersistence.Target(path), written, current is null ? null : path,
            () => { if (current is not null) FilePersistence.Replace(path + ".bak", current, path); });
        observed = written; hasObserved = true;
        PlatformCatalog.UseFolders(library);
    }

    private void ValidateChangedParameters(Library next, Library? previous)
    {
        // An unchanged invalid string in another source must not prevent repairing the active base or saving settings.
        IEnumerable<(string Source, Guid Id, string? Text)> Parameters(Library value)
        {
            foreach (var (source, settings) in LibrarySources.Remember(value, path).Sources)
                foreach (var entry in settings.Bases) yield return (source, entry.Id, entry.AdditionalParameters);
        }
        var known = previous is null ? [] : Parameters(previous).ToHashSet();
        foreach (var item in Parameters(next))
            if (item.Text is not null && !known.Contains(item)) LaunchParameters.Validate(item.Text);
    }

}

/// <summary>Client for 1С:Предприятие chosen for the current program session, as the original's «Текущая сессия».</summary>
public enum SessionClient { Auto, Ordinary, Thick, Thin }

public static class Launcher
{
    public static string ErrorMessage(Exception error) => error switch
    {
        System.ComponentModel.Win32Exception win32 => Win32Message(win32.NativeErrorCode, OperatingSystem.IsWindows()),
        PlatformNotSupportedException => "Выбранный способ запуска не поддерживается в этой операционной системе.",
        _ => LegacyImport.ErrorMessage(error)
    };
    /// <summary>The reason of a refused start by the operating system code (the codes of Windows and of Linux differ), with the
    /// code itself for a report. Anything else gets the general advice.</summary>
    internal static string Win32Message(int code, bool windows) => (windows, code) switch
    {
        (true, 2 or 3) => $"Файл клиента 1С не найден (код ОС {code}): платформу могли удалить или переместить. Проверьте путь в свойствах базы или в настройках.",
        (true, 5) => "Windows отказала в доступе к файлу клиента 1С (код ОС 5). Проверьте права на файл и каталог платформы и не блокирует ли запуск антивирус.",
        (true, 32) => "Файл клиента 1С занят другим процессом (код ОС 32). Закройте программу, которая его использует, и повторите запуск.",
        (true, 225) => "Защита Windows или антивирус признали файл клиента 1С опасным (код ОС 225). Проверьте уведомление антивируса и сам файл.",
        (true, 1260) => "Запуск этого файла запрещён политикой безопасности Windows (код ОС 1260). Обратитесь к администратору компьютера.",
        (true, 193 or 216) => $"Выбранный файл не подходит этому компьютеру: это не исполняемый файл Windows нужной разрядности (код ОС {code}). " +
            "Выберите 1cv8.exe установленной платформы (x64, x86 или ARM64 — по вашей Windows).",
        (true, 740) => "Для запуска этого клиента нужны права администратора (код ОС 740). Используйте «Запустить от имени администратора».",
        (false, 2) => "Не найден файл клиента 1С или его загрузчик (интерпретатор) (код ОС 2). Проверьте путь в свойствах базы или в настройках; " +
            "для 32-разрядной платформы может не хватать 32-разрядного загрузчика системы.",
        (false, 8) => "Файл клиента 1С — не исполняемый файл этой операционной системы или разрядности (код ОС 8). Выберите клиент платформы для Linux вашей архитектуры.",
        (false, 13) => "Нет права запускать файл клиента 1С (код ОС 13). Проверьте право на выполнение (chmod +x), права на каталог платформы " +
            "и не запрещён ли запуск программ на разделе, где она лежит (параметр монтирования noexec).",
        (false, 12) => "Не хватает памяти для запуска клиента 1С (код ОС 12).",
        (false, 20) => "Путь к клиенту 1С содержит элемент, который не является каталогом (код ОС 20). Проверьте путь в свойствах базы или в настройках.",
        (false, 26) => "Файл клиента 1С сейчас открыт на запись другим процессом (код ОС 26). Дождитесь окончания копирования или установки и повторите запуск.",
        _ => $"Не удалось выполнить файл клиента 1С (код ОС {code}). Проверьте, что выбран исполняемый файл для вашей ОС и разрядности, и у вас есть право на его запуск."
    };

    /// <param name="passwords">The saved passwords of the base's list; null passes the names without passwords.</param>
    public static ProcessStartInfo Build(Library library, BaseEntry entry, bool designer, IReadOnlyList<PlatformInstallation>? installations = null,
        SessionClient session = SessionClient.Auto, LaunchOptions? options = null, ListPasswords? passwords = null)
    {
        // The settings of the base's branch first; the choices of this start («Запустить с параметрами») go over them.
        entry = PlatformBranches.ForStart(library, entry);
        if (options is not null) entry = options.Apply(entry);
        var start = entry.Kind == ConnectionKind.Web ? BuildWeb(library, entry, designer, installations, session)
            : BuildLocal(library, entry, designer, installations, session);
        IReadOnlyDictionary<PasswordRole, string>? saved = null;
        string? Password(PasswordRole role) => (saved ??= passwords?.Read(entry.Id) ?? new Dictionary<PasswordRole, string>()).GetValueOrDefault(role);
        // «Запустить с аутентификацией»: 1C asks for the name and password, the saved ones are not passed; the repository
        // of the page is still connected to the configurator.
        if (options?.AskLogin == true)
        {
            // Also without the page: the web server setting (/WSA-) is not a saved login and stays.
            var auth = entry.Authentication ?? new AuthenticationSettings();
            LaunchAuthentication.Apply(start, entry with { AskCredentials = false,
                Authentication = auth with { EnterpriseLogin = false, DesignerLogin = false, OsAuthentication = false } }, designer, Password);
            LaunchParameters.AskLogin(start);
        }
        else LaunchAuthentication.Apply(start, entry, designer, Password);
        return start;
    }

    private static ProcessStartInfo BuildLocal(Library library, BaseEntry entry, bool designer, IReadOnlyList<PlatformInstallation>? installations,
        SessionClient session)
    {
        var executable = PlatformCatalog.Resolve(library, entry, installations, designer);
        // The configurator always uses 1cv8; the session choice applies to 1С:Предприятие only.
        if (!designer && session != SessionClient.Auto) executable = SessionExecutable(executable, session);
        // Like the original StartManager: a client chosen by version starts through 1cv8s of the same installation,
        // which picks the thin or thick client by the base (App= in .v8i) and the configuration. A file chosen by hand starts as is.
        else if (!designer && Automatic(library, entry))
        {
            // App= of the list decides the client; without it 1cv8s decides by the base, as 1CEStart does.
            if (entry.ClientType.Equals("ThinClient", StringComparison.OrdinalIgnoreCase) && Sibling(executable, "1cv8c") is { } thin) executable = thin;
            else if (entry.ClientType.Equals("ThickClient", StringComparison.OrdinalIgnoreCase) && Sibling(executable, "1cv8") is { } thick) executable = thick;
            else if (Starter(executable) is { } starter) executable = starter;
        }
        var start = Build(executable, entry with { Executable = "" }, designer);
        if (!designer && session == SessionClient.Ordinary) start.ArgumentList.Add("/RunModeOrdinaryApplication");
        else if (!designer && session == SessionClient.Thick) start.ArgumentList.Add("/RunModeManagedApplication");
        // As the original's «Режим» of the base (decision of the user 12.10.2026: the whole original): App=ThickClient is
        // «Толстый клиент (ОП)» or «(УП)» and gives the run mode when the current session is «Авто».
        // A run mode written in the parameters (the base's, the branch's or the list's) wins: no second, contradictory key.
        else if (!designer && session == SessionClient.Auto && entry.ClientType.Equals("ThickClient", StringComparison.OrdinalIgnoreCase) &&
            !LaunchParameters.HasKey(start.ArgumentList, "/RunModeOrdinaryApplication", "/RunModeManagedApplication"))
            start.ArgumentList.Add(entry.ManagedApplication ? "/RunModeManagedApplication" : "/RunModeOrdinaryApplication");
        if (entry.PlatformRule is not null || string.IsNullOrWhiteSpace(entry.Executable) && library.PlatformRule is not null)
            start.ArgumentList.Add("/AppAutoCheckVersion-");
        return start;
    }

    // A base on a web server is opened only by the thin client (/WS): 1cv8 does not connect through a web server and shows
    // its list of bases instead. The configurator does not work through a web server at all.
    private static ProcessStartInfo BuildWeb(Library library, BaseEntry entry, bool designer, IReadOnlyList<PlatformInstallation>? installations,
        SessionClient session)
    {
        if (designer) throw new ArgumentException("Конфигуратор не подключается к базе на веб-сервере. Откройте её в 1С:Предприятии или в браузере.");
        if (session is SessionClient.Ordinary or SessionClient.Thick)
            throw new ArgumentException("База на веб-сервере открывается только тонким клиентом или в браузере. Выберите в «Текущей сессии» «Авто» или «Тонкий клиент».");
        // Resolve already gives the manual file or, when a version rule is set, the client of the selected installation.
        var chosen = PlatformCatalog.Resolve(library, entry, installations, false);
        var thin = chosen;
        if (!PlatformCatalog.IsThinClient(chosen) && !string.IsNullOrWhiteSpace(chosen) && Path.IsPathFullyQualified(chosen))
        {
            var folder = Path.GetDirectoryName(chosen)!;
            thin = Path.Combine(folder, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c");
            if (!File.Exists(thin)) throw new ArgumentException($"Для базы на веб-сервере нужен тонкий клиент 1cv8c в каталоге {folder}, но он не установлен. Установите тонкий клиент или выберите другую версию платформы.");
        }
        var start = Build(thin, entry with { Executable = "" }, false);
        if (entry.PlatformRule is not null || string.IsNullOrWhiteSpace(entry.Executable) && library.PlatformRule is not null)
            start.ArgumentList.Add("/AppAutoCheckVersion-");
        return start;
    }

    /// <summary>As 1CEStart: App=WebClient of the list opens the base in the browser unless the thin client is chosen for this start.
    /// A start as administrator is a start of the thin client with elevation: a browser is not elevated.
    /// A start with authentication (/WA-) is a start of the thin client as well.</summary>
    public static bool OpensInBrowser(BaseEntry entry, bool designer, SessionClient session, bool elevated = false) => !designer && !elevated &&
        entry.Kind == ConnectionKind.Web && session == SessionClient.Auto && !LaunchParameters.HasClientOptions(entry) &&
        entry.ClientType.Equals("WebClient", StringComparison.OrdinalIgnoreCase);

    /// <summary>The original's «Открыть в web-браузере»: the address of a base on a web server in the default browser.</summary>
    public static ProcessStartInfo Browser(BaseEntry entry)
    {
        entry.Validate();
        if (entry.Kind != ConnectionKind.Web) throw new ArgumentException("В браузере открывается только база на веб-сервере.");
        return new ProcessStartInfo(new Uri(entry.Location).AbsoluteUri) { UseShellExecute = true };
    }

    public static void OpenInBrowser(BaseEntry entry)
    {
        using var process = Process.Start(Browser(entry));
    }

    private static bool Automatic(Library library, BaseEntry entry) => entry.PlatformRule is not null ||
        string.IsNullOrWhiteSpace(entry.Executable) && (library.PlatformRule is not null || PlatformCatalog.IsImplicit(library, entry));

    public static string? Starter(string executable) => Sibling(executable, "1cv8s");

    private static string? Sibling(string executable, string client)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return null;
        var result = Path.Combine(Path.GetDirectoryName(executable)!, OperatingSystem.IsWindows() ? client + ".exe" : client);
        return File.Exists(result) ? result : null;
    }

    /// <summary>The client of the same installation: 1cv8c for the thin client, 1cv8 for the ordinary and thick modes.</summary>
    public static string SessionExecutable(string executable, SessionClient session)
    {
        if (session == SessionClient.Auto || string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return executable;
        var name = session == SessionClient.Thin ? "1cv8c" : "1cv8";
        var folder = Path.GetDirectoryName(executable)!;
        var result = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (!File.Exists(result))
            throw new ArgumentException($"Для режима «{SessionText(session)}» нужен клиент {name} в каталоге {folder}, но он не установлен. Выберите в «Текущей сессии» «Авто» или другой режим.");
        return result;
    }

    public static string SessionText(SessionClient session) => session switch
    {
        SessionClient.Ordinary => "Обычный режим", SessionClient.Thick => "Толстый клиент", SessionClient.Thin => "Тонкий клиент", _ => "Авто"
    };

    public static void Start(Library library, BaseEntry entry, bool designer, SessionClient session = SessionClient.Auto, bool elevated = false,
        LaunchOptions? options = null, ListPasswords? passwords = null)
    {
        // With the parameters of its branch: a base that gets launch parameters is opened by the client, not in the browser.
        var branch = PlatformBranches.ForStart(library, entry);
        var effective = options?.Apply(branch) ?? branch;
        elevated = Elevated(entry, elevated || options?.Elevated == true);
        if (options?.Platform is null && options?.AskLogin != true && OpensInBrowser(effective, designer, session, elevated))
        { OpenInBrowser(effective); return; }
        var start = Build(library, entry, designer, session: session, options: options, passwords: passwords);
        if (elevated) Elevate(start);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить 1С.");
        }
        // ERROR_CANCELLED: the user declined the elevation prompt.
        catch (System.ComponentModel.Win32Exception ex) when (elevated && ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Запуск от имени администратора отменён.", ex); }
    }

    /// <summary>Whether a start is elevated: asked for, or the base's «Запуск от имени администратора» (RunAsAdmin) — on every launch
    /// path, desktop shortcuts included. The base's flag works on Windows only: on Linux its kept value asks for nothing, an explicit request still
    /// gets the «only in Windows» error (Codex audit 12.10).</summary>
    public static bool Elevated(BaseEntry entry, bool requested) => requested || entry.RunAsAdmin && OperatingSystem.IsWindows();

    /// <summary>As the original «Запустить от имени администратора»: Windows asks for elevation (UAC). The arguments stay
    /// separate items; the shell start passes them with the same quoting.</summary>
    public static ProcessStartInfo Elevate(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Запуск от имени администратора доступен только в Windows.");
        start.UseShellExecute = true; start.Verb = "runas";
        return start;
    }

    /// <summary>Whether this program already runs elevated: then 1C started from it is elevated too.</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static ProcessStartInfo Build(string executable, BaseEntry entry, bool designer)
    {
        entry.Validate();
        executable = string.IsNullOrWhiteSpace(entry.Executable) ? executable : entry.Executable;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Выберите полный путь к установленному клиенту 1cv8 (1cv8.exe в Windows).");
        // A path that is set but missing is named: the user sees which file to fix.
        if (!File.Exists(executable))
            throw new ArgumentException($"Клиент 1С не найден: {executable}. Выберите установленный клиент в свойствах базы или в настройках.");
        if (designer && PlatformCatalog.IsThinClient(executable))
            throw new ArgumentException("Для конфигуратора нужен толстый клиент 1cv8; выбран тонкий клиент 1cv8c. Выберите 1cv8 в свойствах базы или в настройках.");
        if (entry.Kind == ConnectionKind.File && !Directory.Exists(entry.Location))
            throw new ArgumentException("Каталог базы не найден. Проверьте путь.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (designer && entry.Kind == ConnectionKind.Web)
            throw new ArgumentException("Конфигуратор не подключается к базе на веб-сервере. Откройте её в 1С:Предприятии или в браузере.");
        start.ArgumentList.Add(designer ? "DESIGNER" : "ENTERPRISE");
        start.ArgumentList.Add(entry.Kind switch { ConnectionKind.File => "/F", ConnectionKind.Web => "/WS", _ => "/S" });
        start.ArgumentList.Add(entry.Location);
        LaunchParameters.Append(start, entry, designer);
        return start;
    }

    public static void Start(string executable, BaseEntry entry, bool designer)
    {
        using var process = Process.Start(Build(executable, entry, designer))
            ?? throw new InvalidOperationException("Не удалось запустить 1С.");
    }
}
