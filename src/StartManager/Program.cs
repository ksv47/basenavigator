using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Controls;
using StartManager.Core;

namespace StartManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        FileStream? instance = null;
        // Helper started by the program itself: waits for it to exit and applies a prepared update (see Updater.LaunchHelper).
        if (args.Length == 5 && args[0] == "--apply-update" && int.TryParse(args[1], out var waitFor) && Version.TryParse(args[4], out var from))
        {
            try { Environment.ExitCode = Updater.ApplyPending(waitFor, args[2], args[3], from); }
            catch (Exception) { Environment.ExitCode = 7; }
            return;
        }
        var consoleMode = args.Any(x => x is "--help" or "-h" or "--version" or "--no-gui" or "--check-update" or "--update" or "--rollback"
            or "--install-desktop" or "--uninstall-desktop");
        var guiStarted = false;
        try
        {
            if (consoleMode) CliConsole.Initialize();
            // Update commands take the exclusive hold themselves; --help/--version only print (also the trial run of a
            // downloaded version, which must not leave files in the package). The menu entry commands neither read the
            // profile nor start the program: they only write to the user's data folder and finish at once.
            var noRegistration = args.Any(x => x is "--check-update" or "--update" or "--rollback" or "--help" or "-h" or "--version"
                or "--install-desktop" or "--uninstall-desktop");
            // Registration comes before anything else that may show a window, including the argument error window.
            if (!noRegistration)
            {
                Registration = Updater.Register(AppContext.BaseDirectory, TimeSpan.FromMinutes(5));
                if (Registration.TimedOut)
                {
                    if (consoleMode) Console.Error.WriteLine("Идёт установка обновления БазоНавигатора. Повторите запуск через несколько минут.");
                    Environment.ExitCode = 7; return;
                }
            }
            DiagnosticArguments options;
            try { options = DiagnosticArguments.Parse(args); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Environment.ExitCode = ex is ArgumentException ? 2 : 3;
                StartupError = LegacyImport.ErrorMessage(ex);
                if (consoleMode) { Console.Error.WriteLine(StartupError); return; }
                guiStarted = true; RunGui([]); return;
            }
            Options = options;
            if (options.Help) { Console.Out.WriteLine(CommandLineHelp.Text); return; }
            if (options.Version) { Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version); return; }
            if (options.Update != UpdateCommand.None) { Environment.ExitCode = RunUpdate(options.Update, options.RollbackVersion); return; }
            if (options.Desktop != DesktopCommand.None)
            {
                Environment.ExitCode = DesktopEntryCommand.Run(options.Desktop, Environment.ProcessPath, OperatingSystem.IsLinux(),
                    Environment.GetEnvironmentVariable, Console.Out, Console.Error);
                return;
            }
            // Every copy holds .run.lock shared (registered above): the update helper replaces files only with an exclusive
            // hold, which also keeps new copies waiting until it has finished.
            if (Environment.ProcessPath is { } self)
            {
                // Started while the files were replaced: continue from the new file.
                if (Registration?.Waited == true) { StartAgain(self, args); return; }
                if (options.LaunchId is null && StartRecovery(self)) return;
                Updater.Cleanup(AppContext.BaseDirectory, self);
                if (options.LaunchId is null) PrepareUpdateAtStart(options);
            }
            ActiveSettingsPath = options.SettingsPath;
            LibraryStore.FirstRunListPath = V8iStore.StandardPath;
            var storage = new SettingsStorage();
            if (string.Equals(Path.GetFullPath(options.SettingsPath), Path.Combine(storage.PortableDirectory, "library.json"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                ProfileRelocation.PortableRoot = storage.ApplicationDirectory;
            AppLog.Initialize(options);
            LogStartFailures();
            RetireLegacyPasswords(options.SettingsPath);
            if (options.LaunchId is not null)
            {
                var result = Launch(options);
                Environment.ExitCode = result.Code;
                if (options.NoGui)
                {
                    // The notice stays in the vault until the main window shows it.
                    ConsoleLaunch.Report(result.Error, PasswordsNotice, Console.Out, Console.Error);
                    return;
                }
                if (result.Error is null) return;
                StartupError = result.Error;
            }
            else
            {
                bool exclusive = false;
                try { exclusive = new LibraryStore(options.SettingsPath).Load().PreventMultipleInstances; }
                catch (Exception ex) { AppLog.Warning("startup.profile.read_failed", ex); }
                exclusive = options.PreventMultipleInstances ?? exclusive;
                try { instance = InstanceGuard.Acquire(options.SettingsPath, exclusive); }
                catch (Exception ex) when (InstanceGuard.IsConflict(ex))
                {
                    AppLog.Warning("startup.instance.blocked", ex);
                    StartupError = "БазоНавигатор с этим файлом настроек уже открыт. Запрет второго экземпляра включён. " +
                        "Перейдите в прежнее окно или закройте его и повторите запуск.";
                    Environment.ExitCode = 6;
                }
                catch (Exception ex)
                {
                    AppLog.Warning("startup.instance.unavailable", ex);
                    if (exclusive)
                    {
                        StartupError = "Не удалось установить запрет второго экземпляра. Проверьте доступ к папке настроек. " + LegacyImport.ErrorMessage(ex);
                        Environment.ExitCode = 6;
                    }
                }
            }
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) AppLog.Error("fatal.unhandled", ex);
                AppLog.Flush();
            };
            TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("task.unobserved", e.Exception);
            guiStarted = true;
            RunGui(options.ApplicationArguments);
        }
        catch (Exception ex)
        {
            AppLog.Error("fatal.startup", ex); AppLog.Flush();
            Environment.ExitCode = 1;
            if (consoleMode) Console.Error.WriteLine("Ошибка БазоНавигатора: " + LegacyImport.ErrorMessage(ex));
            else if (!guiStarted)
            {
                StartupError = "Не удалось запустить БазоНавигатор: " + LegacyImport.ErrorMessage(ex);
                try { RunGui([]); }
                catch (Exception uiError) { AppLog.Error("fatal.error_window", uiError); }
            }
        }
        finally { AppLog.Close(); instance?.Dispose(); }
    }

    public static Version CurrentVersion => typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Held for the whole run; tells the update helper that this copy of the program is running.</summary>
    private static Registration? Registration { get; set; }

    private static void StartAgain(string self, string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        Registration?.Dispose();
        try { using var process = System.Diagnostics.Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>
    /// An interrupted update must not be repaired by the running program (it would replace its own file):
    /// the helper repairs it after this process exits and starts the program again.
    /// </summary>
    private static bool StartRecovery(string self)
    {
        try
        {
            var (started, notice, failure) = CreateUpdater().RecoverAtStart(CurrentVersion, RestartArguments);
            if (notice is not null) UpdateNotice = notice;
            if (failure is not null) startFailures.Add(("update.recovery.helper_failed", failure));
            return started;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Said, not dropped (Grok full audit 24.10, P2-2); the journal is not open yet, the record is written once it is.
            UpdateNotice = Updater.HelperNotStarted(ex);
            startFailures.Add(("update.recovery.failed", ex));
            return false;
        }
    }

    /// <summary>The caption of a message of the program itself (not of one of its windows): the product name.</summary>
    public const string MessageTitle = "БазоНавигатор";

    /// <summary>Bases whose passwords of an earlier build were removed and not yet shown (kept in the vault until the main window
    /// shows them; a shortcut start or --no-gui keeps them for it); 0 when none.</summary>
    public static int PasswordsPending { get; private set; }
    // The notice of this start (its generation and total): only that is acknowledged (Codex re-checks 4 and 5, N1, RC5-1).
    private static RetiredNotice? passwordsSeen;
    // The vault the notice was read from: the profile of this start (the main window may switch the profile later).
    private static string passwordsSettings = "";
    public static string? PasswordsNotice => PasswordsPending > 0 ? PasswordVault.RetiredMessage(PasswordsPending) : null;

    // Saved passwords of 0.3.0.x («<base ID>/<role>»): removed at every start that finds them, never moved or given to a base
    // (decision of the user 25.10.2026). A vault that cannot be changed keeps them, still given to no base; the next start tries again.
    private static void RetireLegacyPasswords(string settingsPath)
    {
        passwordsSettings = settingsPath;
        var vault = new PasswordVault(settingsPath);
        try { if (vault.RetireLegacy() is var removed && removed > 0) AppLog.PasswordsRetired(removed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { AppLog.Warning("passwords.retire.failed", ex); }
        try { passwordsSeen = vault.PendingRetired(); PasswordsPending = passwordsSeen.Bases; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { AppLog.Warning("passwords.notice.read_failed", ex); }
    }

    /// <summary>The main window has shown the notice and it was closed: it leaves the vault (a failure keeps it for the next start).
    /// Called on the UI thread; the vault is written off it.</summary>
    public static Task PasswordsNoticeShown()
    {
        if (PasswordsPending <= 0 || passwordsSeen is not { } seen) return Task.CompletedTask;
        PasswordsPending = 0;
        var settingsPath = passwordsSettings;
        return Task.Run(() => AcknowledgePasswords(settingsPath, seen));
    }

    private static void AcknowledgePasswords(string settingsPath, RetiredNotice seen)
    {
        try { new PasswordVault(settingsPath).Acknowledge(seen); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { AppLog.Warning("passwords.notice.ack_failed", ex); }
    }
    // Failures of the update steps that run before the journal is open: written to it right after AppLog.Initialize.
    private static readonly List<(string Code, Exception Error)> startFailures = [];

    private static void LogStartFailures()
    {
        foreach (var (code, error) in startFailures) AppLog.Warning(code, error);
        startFailures.Clear();
    }

    /// <summary>Outcome of an update applied after the previous run, or a prepared update that waits for exit.</summary>
    public static string? UpdateNotice { get; private set; }

    /// <summary>Arguments to start the program again after an update (without the helper arguments).</summary>
    public static string[] RestartArguments => Environment.GetCommandLineArgs().Skip(1).ToArray();

    private static void PrepareUpdateAtStart(DiagnosticArguments options)
    {
        try
        {
            var result = Updater.TakeResult(AppContext.BaseDirectory);
            var updater = CreateUpdater();
            if (result is not null && result.StartsWith("ok ", StringComparison.Ordinal))
                UpdateNotice = $"Установлена версия {result[3..]}";
            else if (result is not null && result.StartsWith("recovered", StringComparison.Ordinal))
                UpdateNotice = "Прерванное обновление отменено, программа восстановлена" + result["recovered".Length..];
            else if (result is not null)
            {
                // Do not retry a failed update at every start: the prepared files are dropped, the reason is shown.
                updater.DiscardPending();
                UpdateNotice = "Обновление не установлено: " + result.Replace("error ", "");
            }
            // A helper from the previous run did not apply it (for example after a reboot): wait for this run instead.
            if (updater.ResumePending(CurrentVersion, RestartArguments) is { } pending)
            {
                UpdateNotice = pending;
                if (Updater.HelperFailure is { }) startFailures.Add(("update.pending.helper_failed", new IOException(Updater.HelperFailure)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { startFailures.Add(("update.start.failed", ex)); }
    }
    public static bool LightBuild => Updater.IsLight(typeof(Program).Assembly);

    /// <param name="keep">«Хранить прежних версий» of the profile: the archive is trimmed to it; null — not trimmed.</param>
    public static Updater CreateUpdater(string repository = "", int? keep = null)
    {
        var name = repository.Length > 0 ? repository : GitHubReleaseSource.DefaultRepository;
        // HTTPS from the public repository; gh only when that fails (a private repository, the API limit).
        return new(AppContext.BaseDirectory, Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить файл программы."),
            new FallbackReleaseSource(new GitHubHttpReleaseSource(name), new GitHubReleaseSource(name))) { Keep = keep, Light = LightBuild };
    }

    private static int RunUpdate(UpdateCommand command, Version? rollbackVersion)
    {
        try
        {
            var repository = "";
            int? keep = null;
            try { var profile = new LibraryStore(LibraryStore.DefaultPath).Load(); repository = profile.UpdateRepository; keep = profile.KeepVersions; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
            var updater = CreateUpdater(repository, keep);
            // Direct replacement needs the exclusive hold: no other copy runs and none can start meanwhile.
            var impossible = false;
            using var exclusive = command == UpdateCommand.Check ? null : updater.TryExclusive(out impossible);
            if (command != UpdateCommand.Check && exclusive is null)
            {
                Console.Error.WriteLine(impossible ? "Нет доступа к папке программы: обновление невозможно." : "Закройте все окна БазоНавигатора и повторите команду.");
                return 7;
            }
            // The window removes leftovers at its start; a console-only user gets them removed here (old program files renamed
            // aside by earlier direct replacements on Windows: no copy runs from them under the exclusive hold).
            if (OperatingSystem.IsWindows() && exclusive is not null && Environment.ProcessPath is { } self)
                Updater.Cleanup(AppContext.BaseDirectory, self);
            // Console output is set up before files change: a replaced single-file program cannot load anything new (Linux).
            Console.Out.WriteLine(command == UpdateCommand.Rollback ? "Возврат сохранённой версии…" : "Проверка выпусков…");
            if (command == UpdateCommand.Rollback)
            {
                Console.Out.WriteLine($"Возвращена версия {updater.Rollback(CurrentVersion, rollbackVersion)}{updater.ParkedNote}. Перезапустите программу.");
                return 0;
            }
            var latest = updater.Latest();
            Console.Out.WriteLine($"Установлена {CurrentVersion} ({(LightBuild ? "лёгкий вариант" : "автономный вариант")}), последний выпуск {latest.Version}.");
            if (latest.Version <= CurrentVersion) { Console.Out.WriteLine("Обновление не требуется."); return 0; }
            if (command == UpdateCommand.Check) return 8;
            updater.Install(latest, CurrentVersion, Updater.CurrentRuntime, LightBuild);
            Console.Out.WriteLine($"Установлена версия {latest.Version}. Прежняя сохранена в архиве {Updater.ArchiveFolder}; вернуть — --rollback.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Обновление не выполнено: " + LegacyImport.ErrorMessage(ex));
            return 7;
        }
    }

    private static (int Code, string? Error) Launch(DiagnosticArguments options)
    {
        ShortcutRequest request;
        Library library;
        BaseEntry entry;
        try
        {
            AppLog.Stage("shortcut.resolve.begin");
            request = options.LaunchRequest();
            (library, entry) = request.Resolve();
        }
        catch (Exception ex) { AppLog.Error("shortcut.resolve.failed", ex); return (3, "Не удалось прочитать профиль/список или найти базу: " + LegacyImport.ErrorMessage(ex)); }
        try
        {
            AppLog.Stage("shortcut.launch.begin");
            Launcher.Start(library, entry, request.Designer, passwords: new PasswordVault(request.Settings).For(library));
            AppLog.Info("shortcut.launch.started");
        }
        catch (Exception ex) { AppLog.Error("shortcut.launch.failed", ex); return (4, "Не удалось запустить клиент 1С: " + Launcher.ErrorMessage(ex)); }
        try { new ActivityStore(request.Settings).Record(ActivityStore.Source(request.Settings, request.List), entry, request.Designer); }
        catch (Exception ex) { AppLog.Warning("shortcut.history.failed", ex); return (5, "База уже запущена, но история не сохранена. Не повторяйте запуск автоматически. " + LegacyImport.ErrorMessage(ex)); }
        return (0, null);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunGui(string[] args)
    {
        AppLog.Stage("startup.avalonia.begin");
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => { AppLog.Error("fatal.ui", e.Exception); AppLog.Flush(); };
        var builder = BuildAvaloniaApp();
        AppLog.Stage("startup.desktop_lifetime.begin");
        // Avalonia assigns its lifetime exit code even when showing a startup error.
        var startupExitCode = Environment.ExitCode;
        try { builder.StartWithClassicDesktopLifetime(args); }
        finally { if (startupExitCode != 0) Environment.ExitCode = startupExitCode; }
    }
    internal static string? StartupError { get; private set; }
    internal static string? ActiveSettingsPath { get; private set; }
    internal static DiagnosticArguments? Options { get; private set; }
    public static AppBuilder BuildAvaloniaApp()
    {
        AppLog.Stage("startup.builder.configure");
        var builder = AppBuilder.Configure<App>();
        AppLog.Stage("startup.platform.detect.begin");
        builder = builder.UsePlatformDetect();
        AppLog.Stage("startup.platform.detect.ready");
        builder = builder.WithInterFont().LogToTrace();
        AppLog.Stage("startup.builder.ready");
        return builder;
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        AppLog.Stage("startup.theme.begin");
        Styles.Add(new FluentTheme());
        using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://BaseNavigator/Assets/basenavigator.ico"));
        var windowStyle = new Style(s => s.Is<Window>());
        windowStyle.Setters.Add(new Setter(Window.IconProperty, new WindowIcon(iconStream)));
        Styles.Add(windowStyle);
        var scrolling = new Style(s => s.Is<ScrollViewer>());
        scrolling.Setters.Add(new Setter(ScrollViewer.AllowAutoHideProperty, false));
        Styles.Add(scrolling);
        // Linux 4, Н1: the wheel over a closed list, number or date scrolls the page instead of changing the value, in every window.
        DialogLayout.WheelScrollsPage();
        // Linux 5, Н1: Escape in an open calendar, list or panel closes only it, in every window; nothing open, as before.
        DialogLayout.EscapeClosesPopupFirst();
        UiColors.Initialize(this);
        UiStrings.Initialize(this);
        AppLog.Stage("startup.theme.ready");
    }
    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Stage("startup.framework.ready");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.StartupError is { } error)
                desktop.MainWindow = ErrorWindow(error);
            else
            {
                try
                {
                    var options = Program.Options ?? DiagnosticArguments.Parse(desktop.Args ?? []);
                    desktop.MainWindow = new MainWindow(options.SettingsPath, options.ListPath);
                    AppLog.Stage("startup.window.constructed");
                    desktop.MainWindow.Opened += (_, _) => AppLog.Stage("startup.window.opened");
                }
                catch (Exception ex) { AppLog.Error("startup.window.failed", ex); desktop.MainWindow = ErrorWindow("Не удалось открыть настройки. Проверьте путь к файлу, указанный после --settings."); }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static Window ErrorWindow(string error)
    {
        var window = new Window { Title = "БазоНавигатор · сообщение", Width = 540, Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var buttons = new WrapPanel();
        var close = SettingsWindow.Command("Закрыть", window.Close); close.IsCancel = true;
        buttons.Children.Add(close);
        window.Content = DialogLayout.Frame(DialogLayout.Label(error), buttons);
        UiTypography.Default.ApplyDialog(window);
        return window;
    }
}
