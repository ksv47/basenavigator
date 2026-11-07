namespace StartManager.Core;

public enum UpdateCommand { None, Check, Install, Rollback }

public enum DesktopCommand { None, Install, Uninstall }

public sealed record DiagnosticArguments(string[] ApplicationArguments, string SettingsPath, DiagnosticLevel? Level, string? Directory,
    bool? PreventMultipleInstances = null)
{
    public bool Help { get; init; }
    public bool Version { get; init; }
    public bool NoGui { get; init; }
    public string? ListPath { get; init; }
    public Guid? LaunchId { get; init; }
    public bool Designer { get; init; }
    public UpdateCommand Update { get; init; }
    /// <summary>--rollback &lt;версия&gt;: the archived version to restore; null — the most recently saved one.</summary>
    public Version? RollbackVersion { get; init; }
    /// <summary>--install-desktop / --uninstall-desktop: the program's own entry in the Linux application menu.</summary>
    public DesktopCommand Desktop { get; init; }

    public ShortcutRequest LaunchRequest()
    {
        if (LaunchId is not { } id) throw new InvalidOperationException("Не задана база для запуска.");
        var list = ListPath ?? new LibraryStore(SettingsPath).Load().ListPath;
        return new(SettingsPath, list, id, Designer);
    }

    public static DiagnosticArguments Parse(string[] args)
    {
        var remaining = new List<string>();
        var seen = new HashSet<string>();
        DiagnosticLevel? level = null;
        string? directory = null;
        string? settings = null;
        bool? portable = null;
        bool? singleInstance = null;
        var help = false; var version = false; var noGui = false; var update = UpdateCommand.None;
        Version? rollbackVersion = null;
        var desktop = DesktopCommand.None;
        string? list = null;
        Guid? launch = null;
        var designer = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i] == "-h" ? "--help" : args[i];
            if (!seen.Add(key)) throw new ArgumentException("Повторный ключ запуска: " + key);
            if (key == "--help") { help = true; continue; }
            if (key == "--version") { version = true; continue; }
            if (key == "--no-gui") { noGui = true; continue; }
            if (key is "--check-update" or "--update" or "--rollback")
            {
                if (update != UpdateCommand.None) throw new ArgumentException("Укажите только одну команду обновления.");
                update = key switch { "--check-update" => UpdateCommand.Check, "--update" => UpdateCommand.Install, _ => UpdateCommand.Rollback };
                // An optional version after --rollback; another key after it is not a version.
                if (key == "--rollback" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1] != "-h")
                {
                    if (!System.Version.TryParse(args[++i], out var chosen))
                        throw new ArgumentException("После --rollback укажите номер сохранённой версии, например 0.3.0.7, или не указывайте ничего.");
                    // Archived versions have four parts: «0.3.0» means 0.3.0.0 (Grok audit 15.10).
                    rollbackVersion = new System.Version(chosen.Major, chosen.Minor, Math.Max(0, chosen.Build), Math.Max(0, chosen.Revision));
                }
                continue;
            }
            if (key is "--install-desktop" or "--uninstall-desktop")
            {
                if (desktop != DesktopCommand.None) throw new ArgumentException("Укажите только одну команду: --install-desktop или --uninstall-desktop.");
                desktop = key == "--install-desktop" ? DesktopCommand.Install : DesktopCommand.Uninstall;
                continue;
            }
            if (key is "--portable" or "--normal")
            {
                if (portable is not null) throw new ArgumentException("Укажите только один режим: --portable или --normal.");
                portable = key == "--portable";
                continue;
            }
            if (key == "--single-instance") { singleInstance = true; continue; }
            if (key is not ("--log-level" or "--log-dir" or "--settings" or "--list" or "--launch" or "--mode") ||
                i + 1 >= args.Length || args[i + 1] == "-h" || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Неизвестный или повторный ключ, либо отсутствует значение параметра запуска.");
            var value = args[++i];
            if (key == "--log-level")
            {
                if (!DiagnosticPreferences.TryLevel(value, out var parsed))
                    throw new ArgumentException("Уровень --log-level: off, error, warning, info или debug.");
                level = parsed;
            }
            else if (key == "--log-dir")
            {
                if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("После --log-dir нужен полный путь к каталогу журнала.");
                directory = value;
            }
            else
            {
                if (key == "--settings")
                {
                    if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("После --settings нужен путь к файлу настроек.");
                    settings = Path.GetFullPath(value);
                    if (Path.GetDirectoryName(settings) is null || Path.EndsInDirectorySeparator(settings) || System.IO.Directory.Exists(settings))
                        throw new ArgumentException("После --settings укажите путь к файлу настроек, а не к каталогу.");
                }
                else if (key == "--list")
                {
                    if (value.Length > 0 && !Path.IsPathFullyQualified(value))
                        throw new ArgumentException("После --list нужен полный путь к .v8i или пустая строка для локального списка.");
                    list = value.Length == 0 ? "" : Path.GetFullPath(value);
                }
                else if (key == "--launch")
                {
                    if (!Guid.TryParse(value, out var id)) throw new ArgumentException("После --launch нужен GUID базы.");
                    launch = id;
                }
                else if (key == "--mode")
                {
                    if (value is not ("enterprise" or "designer")) throw new ArgumentException("После --mode укажите enterprise или designer.");
                    designer = value == "designer";
                }
                remaining.Add(key); remaining.Add(value);
            }
        }
        var application = remaining.ToArray();
        if (help || version || update != UpdateCommand.None || desktop != DesktopCommand.None)
        {
            if ((help ? 1 : 0) + (version ? 1 : 0) + (update != UpdateCommand.None ? 1 : 0) + (desktop != DesktopCommand.None ? 1 : 0) > 1 ||
                seen.Any(key => key is not ("--help" or "--version" or "--no-gui" or "--check-update" or "--update" or "--rollback"
                    or "--install-desktop" or "--uninstall-desktop")))
                throw new ArgumentException("--help, --version, команды обновления и ярлыка меню приложений используются отдельно " +
                    "от других команд (допустим --no-gui).");
            // Informational commands must work even when the portable marker/profile is inaccessible.
            return new([], "", null, null)
            { Help = help, Version = version, NoGui = noGui, Update = update, RollbackVersion = rollbackVersion, Desktop = desktop };
        }
        if (launch is null && seen.Contains("--mode"))
            throw new ArgumentException("--mode используется только вместе с --launch.");
        if (noGui && launch is null)
            throw new ArgumentException("Для --no-gui укажите --launch <GUID>, --help или --version. Открытие списка требует окна.");
        settings ??= portable is { } mode
            ? Path.Combine(new SettingsStorage().DirectoryFor(mode), "library.json")
            : LibraryStore.DefaultPath;
        return new(application, settings, level, directory, singleInstance)
        { Help = help, Version = version, NoGui = noGui, ListPath = list, LaunchId = launch, Designer = designer };
    }
}
