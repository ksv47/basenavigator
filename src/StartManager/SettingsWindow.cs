using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using StartManager.Core;

namespace StartManager;

internal sealed class SettingsWindow : Window
{
    private Library saved;
    private readonly string settingsPath;
    private readonly Func<string, string, AppearanceSettings, Task<(Library Library, V8iStore? Source)>> prepare;
    private readonly Func<Library, V8iStore?, LegacyImportPreview?, Task<(Library Library, string? Warning)>> save;
    private readonly TextBlock message = Label(GeneralMessage);
    private readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl page = new() { Margin = new Thickness(0, 0, 10, 0) };
    private readonly Dictionary<string, Control> pages = [];
    private TextBox client = null!, list = null!, importFile = null!, importFolder = null!;
    private ObservableCollection<string> platformFolders = null!;
    private AppearanceEditor appearance = null!;
    private PlatformEditor platformEditor = null!;
    private ComboBox afterLaunch = null!, doubleClick = null!, logLevel = null!, updateMode = null!;
    private CheckBox otherRepository = null!;
    private TextBox updateRepository = null!;
    private NumericUpDown keepVersions = null!;
    // Order of the original: notify, download, off.
    private static readonly UpdateMode[] UpdateModes = [UpdateMode.Notify, UpdateMode.Download, UpdateMode.Off];
    // Order of the original's «Действие после запуска 1С»: minimize, close, nothing.
    private static readonly AfterLaunchAction[] AfterLaunchOrder = [AfterLaunchAction.Minimize, AfterLaunchAction.Close, AfterLaunchAction.KeepWindow];
    private TextBlock importPreview = null!;
    private CheckBox importClients = null!, importFavorites = null!, importAppearance = null!, importAuthentication = null!, importProperties = null!, importBackup = null!;
    // «Параметры резервного копирования»: the general «Каталог по-умолчанию».
    private TextBox backupFolder = null!;
    // «Версии платформы» and «Настройки платформы» of the original (PlatformBranches).
    private ComboBox mainVersion = null!;
    private TextBox basesFolder = null!;
    private readonly List<(string Branch, PlatformEditor Editor, TextBox Parameters)> branchEditors = [];
    private static readonly string[] MainVersionChoices = ["Как в настройках 1CEStart", .. PlatformBranches.Shown];
    private CheckBox singleInstance = null!, starterButton = null!, useCommonLists = null!, skipReadOnlyCommonLists = null!;
    private LegacyImportPreview? preview;
    private string selectedSection;
    private bool loading;
    private string draftToken = "";
    // The draft as last built from the saved settings: any difference is an unapplied change.
    private string cleanToken = "";
    private readonly Grid body;
    private readonly ScrollViewer pageScroll;
    private readonly WrapPanel commands;
    private bool busy, saving, closed;
    // «Установить обновление» running: closing the window cancels it (gh is stopped, its folder removed), then the window closes.
    private CancellationTokenSource? updateDownload;
    private bool closeAfterUpdate;
    // The tree of sections: its captions wrap only between words (Д2 of the Linux run, DejaVu Sans 22 broke «Информационны / е»).
    private readonly TreeView navigation;
    private readonly List<TreeViewItem> navigationItems = [];
    private double navigationWords; // the widest caption word at its place in the tree; 0 until measured
    private (FontFamily, double)? navigationFont;
    // Right of a caption: the item's padding and the tree's border (the vertical scroll bar of the theme overlays the items).
    private const double NavigationTrailing = 16;

    public SettingsWindow(Library library, string settingsPath, string section,
        Func<string, string, AppearanceSettings, Task<(Library Library, V8iStore? Source)>> prepare,
        Func<Library, V8iStore?, LegacyImportPreview?, Task<(Library Library, string? Warning)>> save)
    {
        saved = library; this.settingsPath = settingsPath; this.prepare = prepare; this.save = save; selectedSection = section;
        Title = "Настройки программы · БазоНавигатор";
        Width = 820; Height = 600; MinWidth = 380; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var type = UiTypography.From(library.Appearance);
        FontFamily = type.DialogFamily; FontSize = type.DialogSize;
        // As the other dialogs: a large dialog font gets a larger window, so the style page keeps its preview in view.
        Width *= UiTypography.DialogScale(type.DialogSize); Height *= UiTypography.DialogScale(type.DialogSize);
        Background = UiColors.Surface; Foreground = UiColors.Text;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("*,Auto,Auto"), RowSpacing = 10 };
        body = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,*"), ColumnSpacing = 14, RowSpacing = 10 };
        body.ColumnDefinitions[0].Width = new GridLength(NavigationWidth());
        navigation = new TreeView { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), AutoScrollToSelectedItem = true };
        AutomationProperties.SetName(navigation, "Разделы настроек");
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        TreeViewItem Item(string text, string key)
        {
            var item = new TreeViewItem { Header = Label(text), Tag = key, IsExpanded = true };
            AutomationProperties.SetName(item, text); navigationItems.Add(item); return item;
        }
        var platform = Item("Платформа 1С", "platform");
        var versions = Item("Версии платформы", "versions");
        var branchesItem = Item("Настройки платформы", "branches");
        var lists = Item("Списки баз", "lists");
        var common = Item("Общие списки баз", "common");
        var import = Item("Импорт из StartManager", "import");
        var style = Item("Шрифты и тема", "appearance");
        var bases = Item("Информационные базы", "lists"); bases.ItemsSource = new[] { versions, lists, common, import };
        var launching = Item("Параметры запуска", "platform"); launching.ItemsSource = new[] { platform, branchesItem };
        var design = Item("Оформление", "appearance"); design.ItemsSource = new[] { style };
        var general = Item("Общее", "general");
        var update = Item("Автообновление", "update");
        // The original's «Сценарии резервирования» with «Параметры» under it, before «Автообновление» (PROXY is not made).
        var backupItem = Item("Сценарии резервирования", "backup");
        var backupOptions = Item("Параметры", "backupOptions"); backupItem.ItemsSource = new[] { backupOptions };
        navigation.ItemsSource = new[] { general, bases, launching, design, backupItem, update };
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is TreeViewItem { Tag: string key } selected)
            {
                ShowSection(key);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => selected.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
        body.Children.Add(navigation); Grid.SetRowSpan(navigation, 2);
        navigation.LayoutUpdated += (_, _) => FitNavigation();
        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        right.Children.Add(title);
        pageScroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(pageScroll, 1); right.Children.Add(pageScroll); Grid.SetColumn(right, 1); Grid.SetRowSpan(right, 2); body.Children.Add(right); root.Children.Add(body);
        body.SizeChanged += (_, _) =>
        {
            var narrow = body.Bounds.Width < 620;
            body.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(NavigationWidth());
            body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRowSpan(navigation, narrow ? 1 : 2); navigation.MaxHeight = narrow ? Math.Max(130, FontSize * 6) : double.PositiveInfinity;
            Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0); Grid.SetRowSpan(right, narrow ? 1 : 2);
            if (navigation.SelectedItem is TreeViewItem selected)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => selected.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        };
        var messages = new ScrollViewer { MaxHeight = 90, Content = message, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(messages, 1); root.Children.Add(messages);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        commands = new WrapPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Command("Применить", () => Commit(false)));
        commands.Children.Add(Command("Сохранить", () => Commit(true)));
        footer.Children.Add(commands);
        var close = Command("Закрыть", Close); close.IsCancel = true; Grid.SetColumn(close, 1); footer.Children.Add(close);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        BuildPages();
        navigation.SelectedItem = section switch { "general" => general, "appearance" => style, "lists" => lists, "common" => common, "versions" => versions, "branches" => branchesItem, "import" => import, "update" => update, "backup" => backupItem, "backupOptions" => backupOptions, _ => platform };
        UiTypography.FitOnScreenChanges(this);
        Closed += (_, _) => closed = true;
        Closing += (_, e) =>
        {
            if (saving) { e.Cancel = true; message.Text = "Дождитесь завершения сохранения."; }
            else if (updateDownload is { } download)
            {
                e.Cancel = true; closeAfterUpdate = true;
                if (!download.IsCancellationRequested) download.Cancel();
                message.Text = "Загрузка обновления отменяется; окно закроется после её остановки.";
            }
        };
    }

    private double NavigationWidth() => TextFit.Fit(Math.Clamp(FontSize * 11, 200, 250), navigationWords, NavigationTrailing);

    // Measured once per font, after the tree is laid out: the left place of each caption (level, expander) comes from the layout,
    // the width of its widest word from the font. The column then is at least that wide, so a word is never broken.
    private void FitNavigation()
    {
        if (navigationFont == (FontFamily, FontSize)) return;
        double needed = 0;
        foreach (var item in navigationItems)
        {
            if (item.Header is not TextBlock label || label.Bounds.Width <= 0 || label.TranslatePoint(default, navigation) is not { } at) return;
            needed = Math.Max(needed, at.X + TextFit.WidestWord([label.Text], word => UiTypography.Measure(word, label.FontFamily, label.FontSize).Width));
        }
        navigationFont = (FontFamily, FontSize); navigationWords = needed;
        var width = NavigationWidth();
        if (body.ColumnDefinitions[0].Width is { IsAbsolute: true } current && Math.Abs(current.Value - width) > 0.5)
            body.ColumnDefinitions[0].Width = new GridLength(width);
    }

    internal static Button Command(string text, Action action)
    {
        var button = new Button { Content = Label(text), Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 6, 0),
            Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, text); button.Click += (_, _) => action(); return button;
    }

    /// <summary>An asynchronous command (an async lambda binds here): an error it does not handle itself is logged and shown in a
    /// message over its window, the program goes on (Codex full audit 24.10, P2-7).</summary>
    internal static Button Command(string text, Func<Task> action)
    {
        Button button = null!;
        button = Command(text, () => Guarded(button, text, action));
        return button;
    }

    /// <summary>Runs an asynchronous command of a button; see <see cref="CommandGuard"/>.</summary>
    internal static async void Guarded(Control anchor, string text, Func<Task> action) => await CommandGuard.Run(action, async error =>
    {
        AppLog.Error("ui.command.failed", error);
        if (TopLevel.GetTopLevel(anchor) is Window { IsVisible: true } owner)
            await BackupDialogs.Inform(owner, UiTypography.Default, text.TrimEnd('…'), "Команда не выполнена: " + LegacyImport.ErrorMessage(error));
    }, shown => AppLog.Error("ui.command.message_failed", shown), () => AppLog.Info("ui.command.cancelled"));
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    // Layout follows the original «Автообновление»: way of updating, then the source; version and manual
    // commands are our addition. Installing replaces program files only, so it does not wait for «Применить».
    private Control UpdatePage()
    {
        var page = Panel();
        updateMode = new ComboBox { ItemsSource = new[] { "Проверять обновления и уведомлять пользователя", "Загружать обновления автоматически",
            "Не проверять обновления автоматически" }, SelectedIndex = Array.IndexOf(UpdateModes, saved.UpdateMode), HorizontalAlignment = HorizontalAlignment.Stretch };
        updateMode.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((value, _) => Label(value?.ToString() ?? ""));
        AutomationProperties.SetName(updateMode, "Способ обновлений БазоНавигатора");
        updateMode.SelectionChanged += (_, _) => Invalidate();
        page.Children.Add(DialogLayout.Field("Способ обновлений БазоНавигатора:", updateMode));
        page.Children.Add(Label("Проверка выполняется при запуске программы в фоне. Загруженная версия начинает работать после перезапуска."));
        // The original keeps every backup; here the archive next to the program keeps the newest of them (the user's choice 15.10).
        keepVersions = new NumericUpDown { Minimum = 1, Maximum = Updater.MaxKeep, Increment = 1, FormatString = "0", Value = saved.KeepVersions,
            ClipValueToMinMax = true, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = Math.Max(110, FontSize * 4 + 40) };
        AutomationProperties.SetName(keepVersions, "Хранить прежних версий");
        keepVersions.ValueChanged += (_, _) => Invalidate();
        page.Children.Add(DialogLayout.Field("Хранить прежних версий:", keepVersions));
        page.Children.Add(Label("Прежние версии сохраняются в папке .versions рядом с программой при установке обновлений и при восстановлении; " +
            "лишние, самые старые, удаляются при следующей установке или восстановлении. Копия установленной сейчас версии в это число не входит."));

        var source = Panel();
        source.Children.Add(Label($"По умолчанию выпуски берутся из приватного репозитория {GitHubReleaseSource.DefaultRepository} через GitHub CLI (gh). " +
            "Нужен выполненный вход «gh auth login»; токен хранит gh, программа его не читает. " +
            "Прокси для gh задаётся переменной окружения HTTPS_PROXY (раздел PROXY — в планах)."));
        otherRepository = new CheckBox { Content = Label("Использовать другой репозиторий:"), IsChecked = saved.UpdateRepository.Length > 0 };
        updateRepository = new TextBox { Text = saved.UpdateRepository, PlaceholderText = "владелец/репозиторий", IsEnabled = otherRepository.IsChecked == true };
        AutomationProperties.SetName(updateRepository, "Репозиторий обновлений");
        otherRepository.IsCheckedChanged += (_, _) => { updateRepository.IsEnabled = otherRepository.IsChecked == true; Invalidate(); };
        updateRepository.TextChanged += (_, _) => Invalidate();
        source.Children.Add(otherRepository); source.Children.Add(updateRepository);

        var manual = Panel();
        var runtime = Updater.CurrentRuntime;
        var current = Program.CurrentVersion;
        Updater Create() => Program.CreateUpdater(saved.UpdateRepository, saved.KeepVersions);
        // Restoring from this page trims with the number shown, applied or not (Grok audit 15.10).
        int Shown() => keepVersions.Value is { } shown && shown >= 1 && shown <= Updater.MaxKeep ? (int)decimal.Truncate(shown) : saved.KeepVersions;
        // Information about the archive is optional: a locked or unreadable file must not stop the settings window.
        IReadOnlyList<SavedVersion> Saved()
        {
            try { return Create().SavedVersions(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
        }
        string Installed() => $"Установлена {current} ({(Program.LightBuild ? "лёгкий вариант, нужен .NET 10" : "автономный вариант")}, {runtime})." +
            (Saved() is { Count: > 0 } versions ? $" Сохранено прежних версий: {versions.Count} ({string.Join(", ", versions.Select(x => x.Version))})." : "");
        var status = Label(Installed());
        var bar = UpdateBar();
        ReleaseInfo? latest = null;
        Button? check = null, install = null, rollback = null, restart = null;
        // After an install or rollback (here, in another window or in the background) the files on disk differ from
        // the running build: further changes wait for a restart.
        void Busy(bool busy)
        {
            var locked = busy || Updater.ChangedInThisProcess;
            string? pending = null;
            try { pending = Create().PendingVersion; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            restart!.IsVisible = !busy && pending is not null && Updater.ChangedInThisProcess &&
                // The files are prepared but the helper did not start (here, in the offer or the background): a restart would only exit.
                Updater.HelperFailure is null;
            check!.IsEnabled = !locked;
            install!.IsEnabled = !locked && latest is not null && latest.Version > current;
            // The window explains an empty archive and a session that already changed the files.
            rollback!.IsEnabled = !busy;
        }
        if (Updater.ChangedInThisProcess) status.Text += Updater.HelperFailure is { } failure
            ? $" Обновление подготовлено в этом сеансе, но установщик не запустился ({failure.TrimEnd('.')}). Оно будет установлено после следующего запуска и закрытия программы."
            : " Обновление подготовлено в этом сеансе — перезапустите программу.";
        async Task Run(string progress, Func<string> action, bool changesFiles)
        {
            try
            {
                Busy(true); status.Text = progress;
                status.Text = await Task.Run(action);
                AppLog.Info(changesFiles ? "update.files_changed" : "update.checked");
            }
            catch (OperationCanceledException) { AppLog.Info("update.cancelled"); status.Text = "Загрузка обновления отменена."; }
            catch (Exception ex) { AppLog.Warning("update.failed", ex); status.Text = "Не выполнено: " + LegacyImport.ErrorMessage(ex); }
            finally { Busy(false); }
        }
        check = Command("Проверить сейчас", async () => await Run("Проверка выпусков на GitHub…", () =>
        {
            latest = Create().Latest();
            return Installed() + (latest.Version > current ? $" Доступна версия {latest.Version}." : $" Последний выпуск {latest.Version} — обновление не требуется.");
        }, false));
        // The running program is never replaced: files are prepared and applied by a helper after the program exits.
        // The stages and the bar under the state (as the window of a backup); the buttons wait, closing the window cancels.
        install = Command("Установить обновление", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var finished = false;
            var progress = new Progress<UpdateProgress>(p => { if (!finished) { status.Text = p.Text; ShowUpdateProgress(bar, p); } });
            updateDownload = cancel; bar.IsIndeterminate = true; bar.IsVisible = true;
            try
            {
                await Run(new UpdateProgress(UpdateStage.Downloading).Text, () =>
                {
                    try
                    {
                        var release = latest ?? throw new InvalidOperationException("Сначала проверьте обновления.");
                        var staged = Create().StageAndLaunch(release, current, runtime, Program.LightBuild, Program.RestartArguments, progress, cancel.Token);
                        latest = null;
                        if (staged.HelperError is not null) return staged.Notice + ".";
                        return $"Версия {release.Version} готова и будет установлена после закрытия программы. Прежняя версия {current} сохранится для возврата.";
                    }
                    finally { finished = true; }
                }, true);
            }
            finally { updateDownload = null; bar.IsVisible = false; }
            if (closeAfterUpdate && !closed) Close();
        });
        rollback = Command("Восстановить версию…", async () =>
        {
            SavedVersion? chosen;
            // Read on the UI thread: the archive is read and the files are prepared in the background.
            var keep = Shown();
            Updater CreateShown() => Program.CreateUpdater(saved.UpdateRepository, keep);
            try { chosen = await RestoreVersionWindow.Choose(this, UiTypography.From(saved.Appearance), CreateShown); }
            catch (Exception ex) { AppLog.Warning("update.failed", ex); status.Text = "Не выполнено: " + LegacyImport.ErrorMessage(ex); return; }
            if (chosen is null || closed) return;
            var updater = CreateShown();
            await Run("Подготовка возврата…", () => RestoreVersionWindow.Restore(updater, chosen, current), true);
        });
        restart = Command("Перезапустить сейчас", () =>
        {
            try
            {
                Create().RequestRestart();
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Не выполнено: " + LegacyImport.ErrorMessage(ex); }
        });
        restart.IsVisible = false;
        manual.Children.Add(status);
        manual.Children.Add(bar);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { check, install, rollback, restart }) { button.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(button); }
        manual.Children.Add(buttons);
        Busy(false);

        var all = new StackPanel { Spacing = 18 };
        all.Children.Add(DialogLayout.Section("Автообновление", page));
        all.Children.Add(DialogLayout.Section("Источник получения обновлений", source));
        all.Children.Add(DialogLayout.Section("Текущая версия", manual));
        return all;
    }
    /// <summary>The bar of the preparation of a release: hidden until it starts, running while the size is not known.</summary>
    internal static ProgressBar UpdateBar()
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 10, MinHeight = 10, IsVisible = false };
        AutomationProperties.SetName(bar, "Ход загрузки обновления");
        return bar;
    }

    internal static void ShowUpdateProgress(ProgressBar bar, UpdateProgress progress)
    {
        if (progress.Fraction is { } part) { bar.IsIndeterminate = false; bar.Value = part; }
        else bar.IsIndeterminate = true;
    }

    private static TextBox Field(string name, string value = "")
    { var field = new TextBox { Text = value }; AutomationProperties.SetName(field, name); return field; }
    private static StackPanel Panel() => new() { Spacing = 10 };
    private const string UnappliedMessage = "Есть неприменённые изменения настроек. «Закрыть» отменяет их; сценарии резервирования записываются сразу и не отменяются.";
    private bool Dirty => !loading && DraftToken() != cleanToken;
    /// <summary>The result of a scenario command; while other settings are unapplied their warning stays after it (Grok audit, part 3).</summary>
    private void ScenarioReport(string text, bool error)
    {
        message.Text = Dirty ? text + " " + UnappliedMessage : text;
        message.Foreground = error ? UiColors.Error : UiColors.Text;
    }
    private const string GeneralMessage = "Изменения сохраняются кнопками «Применить» и «Сохранить». «Закрыть» отменяет неприменённое.";
    private const string ScenarioNote = "Команды этого раздела записывают сценарии сразу, как в оригинале: «Применить», «Сохранить» и «Закрыть» к ним не относятся.";
    private const string ScenarioMessage = "Сценарии резервирования записываются сразу каждой командой раздела. «Применить» и «Сохранить» сохраняют остальные настройки, «Закрыть» отменяет только их.";
    private void ShowSection(string key)
    {
        // The bottom message tells the scenarios apart while their section is shown; it does not replace an error or a report.
        if (key == "backup" && (message.Text == GeneralMessage || message.Text == ScenarioMessage)) { message.Text = ScenarioMessage; message.Foreground = UiColors.Text; }
        else if (key != "backup" && message.Text == ScenarioMessage) message.Text = Dirty ? UnappliedMessage : GeneralMessage;
        // Another section opens from its top (e2e Themes 24.10); rebuilt pages of the same section keep their position.
        if (key != selectedSection) pageScroll.Offset = default;
        selectedSection = key; page.Content = pages[key];
        title.Text = key switch { "general" => "ОБЩЕЕ", "platform" => "ПЛАТФОРМА 1С", "lists" => "СПИСКИ БАЗ", "common" => "ОБЩИЕ СПИСКИ БАЗ", "versions" => "ВЕРСИИ ПЛАТФОРМЫ", "branches" => "НАСТРОЙКИ ПЛАТФОРМЫ", "import" => "ИМПОРТ ИЗ STARTMANAGER", "update" => "АВТООБНОВЛЕНИЕ",
            "backup" => "СЦЕНАРИИ РЕЗЕРВИРОВАНИЯ", "backupOptions" => "ПАРАМЕТРЫ РЕЗЕРВНОГО КОПИРОВАНИЯ", _ => "ШРИФТЫ И ТЕМА" };
    }
    private void BuildPages()
    {
        loading = true;
        preview = null;
        platformEditor = new PlatformEditor(this, saved.Executable, saved.PlatformRule, typography: UiTypography.From(saved.Appearance));
        client = platformEditor.Client;
        list = Field("Путь к общему списку", saved.ListPath);
        appearance = new AppearanceEditor(saved.Appearance);
        var general = Panel(); var diagnostics = Panel();
        singleInstance = new CheckBox { Content = Label("Запрещать запуск второго экземпляра"), IsChecked = saved.PreventMultipleInstances };
        singleInstance.IsCheckedChanged += (_, _) => Invalidate();
        ComboBox ActionChoice(string name, string[] choices, int selected)
        {
            var choice = new ComboBox { ItemsSource = choices, SelectedIndex = selected,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            choice.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((value, _) => Label(value?.ToString() ?? ""));
            AutomationProperties.SetName(choice, name);
            choice.SelectionChanged += (_, _) => Invalidate();
            (name.StartsWith("Диагност", StringComparison.Ordinal) ? diagnostics : general).Children.Add(DialogLayout.Field(name + ":", choice)); return choice;
        }
        // The original's order and words; «Свернуть в трэй» is not offered: the program has no tray icon.
        afterLaunch = ActionChoice("Действие после запуска 1С", ["Свернуть в панель задач", "Закрыть БазоНавигатор", "Ничего не делать"],
            Array.IndexOf(AfterLaunchOrder, saved.AfterLaunch));
        doubleClick = ActionChoice("Действие при двойном щелчке", ["1С:Предприятие", "Конфигуратор"], (int)saved.DoubleClick);
        general.Children.Add(Label("Окно сворачивается или закрывается после успешного создания процесса 1С и сохранения истории. При ошибке или несохранённых настройках окно остаётся открытым."));
        general.Children.Add(singleInstance);
        // The original's «Кнопку "1С:Стартер"» (ShowStarterBtn).
        starterButton = new CheckBox { Content = Label("Кнопку «1С:Стартер»"), IsChecked = !saved.HideStarterButton };
        AutomationProperties.SetName(starterButton, "Отображать кнопку «1С:Стартер»");
        starterButton.IsCheckedChanged += (_, _) => Invalidate();
        general.Children.Add(Label("Для одного файла настроек. Применяется после перезапуска всех открытых экземпляров. Ярлыки запуска баз продолжают работать."));
        // The original's group «Отображать:».
        var show = Panel(); show.Children.Add(starterButton);
        show.Children.Add(Label("Стартер открывает штатное окно 1С со своим списком баз: для платформы выбранной базы, а «▾» — по версии."));
        general.Children.Add(Label("Текущий файл настроек: " + Path.GetFullPath(settingsPath)));
        general.Children.Add(Command("Обычный / портативный режим…", async () =>
        {
            try { await new StorageModeWindow(settingsPath, saved.Appearance).ShowDialog(this); }
            catch (Exception ex) { Error(ex); }
        }));
        logLevel = ActionChoice("Диагностический журнал", ["Выключен", "Только ошибки", "Ошибки и предупреждения", "Ошибки, предупреждения и информация", "Все события (подробно)"], (int)DiagnosticPreferences.Read(saved.Diagnostics));
        diagnostics.Children.Add(Label("Журнал: " + AppLog.DirectoryPath + "\nИстория запусков ведётся отдельно." +
            (AppLog.Overridden ? "\nНа время этого запуска уровень задан командной строкой." : "") +
            (AppLog.Failure is { } failure ? "\nВ этом сеансе были потери записей или ошибки журнала: " + failure : "")));
        var generalPage = Panel();
        generalPage.Children.Add(DialogLayout.Section("Основные параметры", general));
        generalPage.Children.Add(DialogLayout.Section("Отображать:", show));
        generalPage.Children.Add(DialogLayout.Section("Диагностика", diagnostics));
        // Our addition without a counterpart in the original, below its groups; Linux only.
        if (OperatingSystem.IsLinux()) generalPage.Children.Add(MenuEntrySection());
        pages["general"] = generalPage;
        pages["update"] = UpdatePage();
        var platform = Panel(); platform.Children.Add(platformEditor);
        platform.Children.Add(Label("Этот клиент используется по умолчанию. Индивидуальный клиент базы задаётся в её свойствах."));
        // The original's page «Исполняемые файлы»: «Пути поиска платформы», the column «Папка размещения платформы», add and remove.
        platformFolders = new ObservableCollection<string>(saved.PlatformFolders);
        var folderList = new ListBox { ItemsSource = platformFolders, Height = 110, Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(folderList, "Папка размещения платформы");
        var folderButtons = new StackPanel { Spacing = 6 };
        folderButtons.Children.Add(Command("Добавить…", async () =>
        {
            try
            {
                var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка размещения платформы", AllowMultiple = true });
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                foreach (var folder in chosen)
                    if (folder.TryGetLocalPath() is { } path && PlatformCatalog.NormalizeFolders([path]) is [var normal] && !platformFolders.Contains(normal, comparer)) platformFolders.Add(normal);
            }
            catch (Exception ex) { Error(ex); }
        }));
        folderButtons.Children.Add(Command("Удалить", () => { if (folderList.SelectedItem is string selected) platformFolders.Remove(selected); }));
        var folderRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        folderRow.Children.Add(folderList); Grid.SetColumn(folderButtons, 1); folderRow.Children.Add(folderButtons);
        var folders = Panel();
        folders.Children.Add(Label("Если имеются версии платформы установленные нестандартным способом, то здесь можно указать каталоги их размещения. Поиск идёт в них и " +
            "в подкаталогах до четырёх уровней; стандартные каталоги ищутся всегда: " + string.Join("; ", PlatformCatalog.StandardRoots().Distinct()) + "."));
        folders.Children.Add(Label("Папка размещения платформы"));
        folders.Children.Add(folderRow);
        platform.Children.Add(DialogLayout.Section("Пути поиска платформы «1С:Предприятие 8»", folders));
        platformFolders.CollectionChanged += (_, _) => Invalidate();
        var lists = Panel(); lists.Children.Add(Label("Общий список .v8i. Пустое поле — локальный список БазоНавигатора.")); lists.Children.Add(list);
        lists.Children.Add(Command("Выбрать общий список…", async () =>
        {
            try { if (await PickFile("Выберите общий список", "*.v8i") is { } path) list.Text = path; }
            catch (Exception ex) { Error(ex); }
        }));
        lists.Children.Add(Command("Локальный список", () => list.Text = ""));
        lists.Children.Add(Label("Переключение произойдёт после сохранения. Подключения прежнего источника сохраняются. При дальнейшей работе общий .v8i редактируется напрямую."));
        pages["platform"] = DialogLayout.Section("Используемая платформа", platform);
        pages["lists"] = DialogLayout.Section("Открываемый список информационных баз", lists); pages["appearance"] = appearance;
        pages["common"] = CommonListsPage();
        pages["versions"] = VersionsPage(); pages["branches"] = BranchesPage();
        pages["import"] = ImportPage();
        pages["backup"] = BackupScenariosPage(); pages["backupOptions"] = BackupOptionsPage();
        client.TextChanged += (_, _) => Invalidate(); list.TextChanged += (_, _) => Invalidate(); appearance.Changed += Invalidate;
        platformEditor.Changed += Invalidate;
        draftToken = cleanToken = DraftToken(); loading = false; ShowSection(selectedSection);
    }
    /// <summary>
    /// The program's own entry in the Linux application menu (<see cref="DesktopEntry"/>), as --install-desktop and
    /// --uninstall-desktop: the commands act at once and are not part of «Применить» / «Сохранить» / «Закрыть».
    /// </summary>
    private Control MenuEntrySection()
    {
        var panel = Panel();
        var state = Label("");
        Button? create = null, remove = null;
        var working = false;
        (bool Installed, string? Problem) shown = default;
        string? report = null; // the bottom message this section wrote last
        void Show((bool Installed, string? Problem) current)
        {
            shown = current;
            state.Text = current.Problem ?? (current.Installed ? "Ярлык программы в меню приложений создан." : "Ярлыка программы в меню приложений нет.");
            state.Foreground = current.Problem is null ? UiColors.Text : UiColors.Error;
            create!.Content = Label(current.Installed ? "Обновить ярлык" : "Создать ярлык");
            create.IsEnabled = !working && current.Problem is null;
            remove!.IsEnabled = !working && current.Installed;
        }
        async Task Run(DesktopCommand command)
        {
            if (working || closed) return;
            working = true; create!.IsEnabled = remove!.IsEnabled = false;
            var executable = Environment.ProcessPath;
            try
            {
                // File IO and the cache tools (up to 2 × 5 s) run off the UI thread.
                var (result, current) = await Task.Run(() => (
                    DesktopEntryCommand.Apply(command, executable, Environment.GetEnvironmentVariable, DesktopEntryCommand.RunQuietly),
                    DesktopEntryCommand.State(Environment.GetEnvironmentVariable)));
                working = false;
                if (closed) return;
                Show(current);
                if (result.Error is { } error) { AppLog.Warning("desktop_entry.failed"); ScenarioReport(error, true); report = message.Text; return; }
                AppLog.Info(command == DesktopCommand.Install ? "desktop_entry.installed" : "desktop_entry.removed");
                var done = command == DesktopCommand.Install ? "Ярлык программы в меню приложений создан."
                    : result.Lines is [DesktopEntryCommand.NotFound] ? DesktopEntryCommand.NotFound : "Ярлык программы в меню приложений удалён.";
                ScenarioReport(result.Note is null ? done : done + " " + result.Note, false);
                report = message.Text;
            }
            catch (Exception ex)
            {
                working = false;
                if (closed) return;
                Show(DesktopEntryCommand.State(Environment.GetEnvironmentVariable));
                Error(ex);
                report = message.Text;
            }
        }
        create = Command("Создать ярлык", async () => await Run(DesktopCommand.Install));
        remove = Command("Удалить ярлык", async () => await Run(DesktopCommand.Uninstall));
        AutomationProperties.SetName(create, "Создать или обновить ярлык программы в меню приложений");
        AutomationProperties.SetName(remove, "Удалить ярлык программы в меню приложений");
        panel.Children.Add(state);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { create, remove }) { button.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(button); }
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Кнопки действуют сразу: «Применить», «Сохранить» и «Закрыть» к ярлыку не относятся.",
            TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted });
        Show(DesktopEntryCommand.State(Environment.GetEnvironmentVariable));
        // A file added or removed while the window is open shows when the user returns to it. State is cheap: one bounded
        // read of a regular file, a link or a FIFO is not opened.
        // When the state changed, this section's last result message at the bottom is stale and is cleared; messages
        // from other parts of the window stay.
        Activated += (_, _) =>
        {
            if (working || closed) return;
            var current = DesktopEntryCommand.State(Environment.GetEnvironmentVariable);
            if (current != shown && report is not null && message.Text == report)
            {
                message.Text = Dirty ? UnappliedMessage : GeneralMessage;
                message.Foreground = Dirty ? UiColors.Muted : UiColors.Text;
                report = null;
            }
            Show(current);
        };
        return DialogLayout.Section("Меню приложений", panel);
    }
    private async Task<(Library Library, V8iStore? Source)> Draft()
    {
        var path = list.Text?.Trim() ?? ""; var executable = client.Text?.Trim() ?? "";
        if (path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Укажите полный путь к общему списку .v8i.");
        var rule = platformEditor.ReadRule(); var style = appearance.ReadSettings();
        var folders = PlatformCatalog.NormalizeFolders(platformFolders);
        var after = AfterLaunchOrder[Math.Max(0, afterLaunch.SelectedIndex)]; var doubleAction = (DoubleClickAction)doubleClick.SelectedIndex;
        var repository = otherRepository.IsChecked == true ? updateRepository.Text?.Trim() ?? "" : "";
        if (keepVersions.Value is not { } kept || kept < 1 || kept > Updater.MaxKeep || kept != decimal.Truncate(kept))
            throw new ArgumentException($"Хранить прежних версий: укажите число от 1 до {Updater.MaxKeep}.");
        var keep = (int)kept;
        if (otherRepository.IsChecked == true && !GitHubReleaseSource.IsRepository(repository))
            throw new ArgumentException("Репозиторий обновлений: укажите владельца и имя, например owner/basenavigator.");
        if (rule is null && executable != saved.Executable && executable.Length > 0 &&
            (!Path.IsPathFullyQualified(executable) || !await Task.Run(() => File.Exists(executable))))
            throw new ArgumentException("Укажите полный путь к существующему клиенту 1С.");
        var draft = await prepare(path, executable, style);
        return (draft.Library with { PlatformRule = rule, PlatformFolders = folders, AfterLaunch = after, DoubleClick = doubleAction,
            UpdateMode = UpdateModes[Math.Max(0, updateMode.SelectedIndex)], UpdateRepository = repository, KeepVersions = keep,
            PreventMultipleInstances = singleInstance.IsChecked == true, HideStarterButton = starterButton.IsChecked != true,
            MainVersion = mainVersion.SelectedIndex > 0 ? PlatformBranches.Shown[mainVersion.SelectedIndex - 1] : "",
            BasesFolder = ReadBasesFolder(), Branches = ReadBranches(), BackupFolder = ReadBackupFolder(),
            UseCommonLists = useCommonLists.IsChecked == true, SkipReadOnlyCommonLists = skipReadOnlyCommonLists.IsChecked == true,
            Diagnostics = logLevel.SelectedIndex == (int)DiagnosticPreferences.Read(saved.Diagnostics) ? saved.Diagnostics
                : DiagnosticPreferences.WithLevel(saved.Diagnostics, (DiagnosticLevel)logLevel.SelectedIndex) }, draft.Source);
    }
    private void Invalidate()
    {
        if (loading) return;
        var token = DraftToken(); if (token == draftToken) return; draftToken = token;
        preview = null;
        importPreview.Text = "Для переноса баз подготовьте предпросмотр. Изменение любого раздела сбрасывает прежний предпросмотр.";
        message.Text = UnappliedMessage; message.Foreground = UiColors.Muted;
    }
    private string DraftToken()
    {
        string platform;
        try { platform = System.Text.Json.JsonSerializer.Serialize(platformEditor.ReadRule()); }
        catch (Exception ex) { platform = "invalid:" + ex.Message; }
        string style;
        try { style = System.Text.Json.JsonSerializer.Serialize(appearance.ReadSettings()); }
        catch (Exception ex) { style = "invalid:" + ex.Message; }
        return System.Text.Json.JsonSerializer.Serialize(new object?[] { client.Text, list.Text, importFile.Text, importFolder.Text,
            importClients.IsChecked, importFavorites.IsChecked, importAppearance.IsChecked, importAuthentication.IsChecked, importProperties.IsChecked, style, platform,
            afterLaunch.SelectedIndex, doubleClick.SelectedIndex, logLevel.SelectedIndex, singleInstance.IsChecked,
            updateMode.SelectedIndex, otherRepository.IsChecked, updateRepository.Text, keepVersions.Value, platformFolders,
            useCommonLists.IsChecked, skipReadOnlyCommonLists.IsChecked, starterButton.IsChecked, mainVersion.SelectedIndex, basesFolder.Text,
            BranchesToken(), backupFolder.Text, importBackup.IsChecked });
    }
    private async void Commit(bool close)
    {
        if (busy || closed) return;
        busy = true; body.IsEnabled = commands.IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(importFile.Text) && preview is null)
                throw new InvalidOperationException("Сначала выполните предпросмотр импорта или очистите поле источника импорта.");
            message.Text = "Подготовка настроек…";
            var draft = await Draft();
            if (closed) return;
            saving = true; message.Text = "Сохранение настроек…";
            var result = await save(draft.Library, draft.Source, preview);
            saving = false;
            if (closed) return;
            var imported = preview is not null;
            saved = result.Library;
            PlatformCatalog.UseFolders(saved);
            AppLog.Configure(saved); AppLog.Info("settings.saved");
            var type = UiTypography.From(saved.Appearance);
            FontFamily = type.DialogFamily; FontSize = type.DialogSize;
            if (body.ColumnDefinitions[0].Width.IsAbsolute) body.ColumnDefinitions[0].Width = new GridLength(NavigationWidth());
            BuildPages(); UiTypography.FitToScreen(this);
            message.Text = result.Warning ?? (imported ? "Импорт завершён. Настройки сохранены; открыт локальный список." : "Настройки сохранены.");
            message.Foreground = result.Warning is null ? UiColors.Text : UiColors.Error;
            if (close && result.Warning is null) Close();
        }
        catch (Exception ex) { if (!closed) Error(ex); }
        finally { saving = false; busy = false; if (!closed) body.IsEnabled = commands.IsEnabled = true; }
    }
    private void Error(Exception ex) { AppLog.Error("settings.operation.failed", ex); message.Text = LegacyImport.ErrorMessage(ex); message.Foreground = UiColors.Error; }
    private async Task<string?> PickFile(string title, params string[] patterns)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (patterns.Length > 0) options.FileTypeFilter = [new FilePickerFileType("Список баз") { Patterns = patterns }];
        var files = await StorageProvider.OpenFilePickerAsync(options); return files.FirstOrDefault()?.TryGetLocalPath();
    }
    // The original's «Версии платформы»: «Основная версия платформы» (the branch of a record without Version=) and «Каталог
    // информационных баз» (the folder proposed for a new file base). The branch switches and «Альтернативный список» are not
    // carried over (decision of the user 11.10.2026).
    private Control VersionsPage()
    {
        var panel = Panel();
        basesFolder = Field("Каталог информационных баз", saved.BasesFolder);
        basesFolder.PlaceholderText = "Единый каталог информационных баз";
        basesFolder.TextChanged += (_, _) => Invalidate();
        basesFolder.InnerRightContent = Command("…", async () =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог информационных баз", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) basesFolder.Text = path;
            }
            catch (Exception ex) { Error(ex); }
        });
        panel.Children.Add(DialogLayout.Field("Каталог информационных баз:", basesFolder));
        panel.Children.Add(Label("Единый каталог: в нём предлагается размещение новой файловой базы. Пусто — не предлагать."));
        mainVersion = new ComboBox { ItemsSource = MainVersionChoices, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, Array.IndexOf(PlatformBranches.Shown, saved.MainVersion) + 1) };
        AutomationProperties.SetName(mainVersion, "Основная версия платформы");
        mainVersion.SelectionChanged += (_, _) => Invalidate();
        panel.Children.Add(DialogLayout.Field("Основная версия платформы:", mainVersion));
        panel.Children.Add(Label("Версия, по которой запускаются базы без версии в списке, и ветка, чьи «Настройки платформы» к ним применяются. " +
            "«Как в настройках 1CEStart» — версия по умолчанию стартера 1С."));
        return DialogLayout.Section("Дополнительные сведения", panel);
    }

    private string ReadBasesFolder()
    {
        var folder = basesFolder.Text?.Trim() ?? "";
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder)) throw new ArgumentException("Каталог информационных баз: укажите полный путь или оставьте поле пустым.");
        return folder.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    // The original's «Настройки платформы»: for each branch the client of its bases and the parameters put before their own.
    private Control BranchesPage()
    {
        branchEditors.Clear();
        var panel = Panel();
        panel.Children.Add(new TextBlock { Text = "Использование релизов платформы 1С:Предприятие 8", FontWeight = FontWeight.SemiBold, Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Label("Для баз ветки, у которых в свойствах не задан свой клиент или своя версия. Версия ветки не заменяет полный номер " +
            "сборки из списка (например, 8.3.27.1989); исполняемый файл ветки применяется ко всем её базам. Пусто — общая настройка «Платформа 1С»."));
        foreach (var branch in PlatformBranches.Shown)
        {
            var current = saved.Branches.FirstOrDefault(x => x.Branch == branch);
            var editor = new PlatformEditor(this, current?.Executable ?? "", current?.PlatformRule, individual: true, typography: UiTypography.From(saved.Appearance));
            editor.Changed += Invalidate; editor.Client.TextChanged += (_, _) => Invalidate();
            var parameters = Field($"Параметры запуска для {branch}", current?.Parameters ?? "");
            parameters.PlaceholderText = "Например, /DisableStartupMessages";
            parameters.TextChanged += (_, _) => Invalidate();
            var section = Panel(); section.Children.Add(editor); section.Children.Add(DialogLayout.Field("Параметры:", parameters));
            panel.Children.Add(DialogLayout.Section($"Платформа 1С {branch}:", section));
            branchEditors.Add((branch, editor, parameters));
        }
        return panel;
    }

    private List<BranchSettings> ReadBranches()
    {
        var result = new List<BranchSettings>();
        foreach (var (branch, editor, parameters) in branchEditors)
        {
            var rule = editor.ReadRule();
            var client = rule is null ? editor.Client.Text?.Trim() ?? "" : "";
            if (client.Length > 0 && !Path.IsPathFullyQualified(client)) throw new ArgumentException($"Платформа {branch}: укажите полный путь к клиенту 1С.");
            // The fields the program does not know stay with the branch.
            var next = (saved.Branches.FirstOrDefault(x => x.Branch == branch) ?? new BranchSettings { Branch = branch }) with
            { Executable = client, PlatformRule = rule, Parameters = parameters.Text?.Trim() ?? "" };
            next.Validate();
            if (!next.IsDefault || next.Extra is { Count: > 0 }) result.Add(next);
        }
        return result;
    }

    private string BranchesToken() => string.Join("|", branchEditors.Select(x =>
    {
        string rule;
        try { rule = System.Text.Json.JsonSerializer.Serialize(x.Editor.ReadRule()); }
        catch (Exception ex) { rule = "invalid:" + ex.Message; }
        return $"{x.Branch}:{x.Editor.Client.Text}:{rule}:{x.Parameters.Text}";
    }));

    // The original's «Общие списки баз»: the lists are those of 1CEStart (CommonInfoBases= of 1CEStart.cfg), shown here as they are and
    // opened in an editor or a file manager; they are added and removed in 1CEStart.cfg, not here, as in the original.
    private Control CommonListsPage()
    {
        var panel = Panel();
        useCommonLists = new CheckBox { Content = Label("Использовать общие списки информационных баз"), IsChecked = saved.UseCommonLists };
        skipReadOnlyCommonLists = new CheckBox { Content = Label("Игнорировать файлы списка баз, имеющие атрибут «Только чтение»"), IsChecked = saved.SkipReadOnlyCommonLists };
        useCommonLists.IsCheckedChanged += (_, _) => Invalidate(); skipReadOnlyCommonLists.IsCheckedChanged += (_, _) => Invalidate();
        var files = new ObservableCollection<string>();
        var paths = new List<string>();
        var view = new ListBox { ItemsSource = files, Height = 150, Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(view, "Общие списки баз");
        var found = Label("Поиск общих списков в настройках 1CEStart…");
        // On a slow share File.Exists may take long: the page opens at once and fills in.
        _ = Task.Run(() => CommonLists.Discover().Select(x => (Path: x, Exists: File.Exists(x))).ToList()).ContinueWith(task =>
        {
            if (closed) return;
            if (task.Exception is { } error) { found.Text = "Настройки 1CEStart не прочитаны: " + LegacyImport.ErrorMessage(error.GetBaseException()); return; }
            foreach (var (path, exists) in task.Result) { paths.Add(path); files.Add(exists ? path : path + "  (файл не найден)"); }
            found.Text = task.Result.Count == 0 ? "В настройках 1CEStart общие списки не указаны." : $"Общих списков в настройках 1CEStart: {task.Result.Count}.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
        string? Chosen() => view.SelectedIndex >= 0 && view.SelectedIndex < paths.Count ? paths[view.SelectedIndex] : null;
        async void OpenFile()
        {
            if (Chosen() is not { } path) { message.Text = "Выберите список."; return; }
            try { if (!await ListEditor.Open(Launcher, path)) message.Text = $"Не удалось открыть {path}."; }
            catch (Exception ex) { Error(ex); }
        }
        async void OpenFolder()
        {
            if (Chosen() is not { } path) { message.Text = "Выберите список."; return; }
            var folder = Path.GetDirectoryName(path) ?? path;
            try { if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) message.Text = $"Не удалось открыть каталог {folder}."; }
            catch (Exception ex) { Error(ex); }
        }
        view.DoubleTapped += (_, _) => OpenFile();
        var buttons = new StackPanel { Spacing = 6 };
        // As the original: the folder first, then the file.
        buttons.Children.Add(Command("Открыть каталог", OpenFolder));
        buttons.Children.Add(Command("Открыть файл", OpenFile));
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(view); Grid.SetColumn(buttons, 1); row.Children.Add(buttons);
        panel.Children.Add(useCommonLists);
        panel.Children.Add(row);
        panel.Children.Add(found);
        panel.Children.Add(skipReadOnlyCommonLists);
        panel.Children.Add(Label("Общие списки задаются строками CommonInfoBases= в настройках 1CEStart (1CEStart.cfg), как и для самой 1С. " +
            "Их базы показываются в общем дереве после баз открытого списка с отметкой рамкой; база с тем же ID, что уже есть в дереве, " +
            "не повторяется. Такие базы можно запускать и смотреть их свойства, а изменить — только в самом списке."));
        return DialogLayout.Section("Использование общих списков баз платформы 1С:Предприятие 8", panel);
    }
    private Control ImportPage()
    {
        var panel = Panel();
        panel.Children.Add(Label("Импорт дополняет локальный список. Исходники и общий .v8i не меняются. Перед записью существующих настроек создаётся .bak."));
        panel.Children.Add(Label("Список .v8i или снимок .v8sx")); importFile = Field("Источник импорта"); panel.Children.Add(importFile);
        panel.Children.Add(Command("Выбрать список или снимок…", async () =>
        {
            try { if (await PickFile("Источник импорта", "*.v8i", "*.v8sx") is { } path) importFile.Text = path; }
            catch (Exception ex) { Error(ex); }
        }));
        panel.Children.Add(Label("Папка SMSettings оригинала (необязательно)")); importFolder = Field("Папка настроек оригинала"); panel.Children.Add(importFolder);
        if (OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartManager14", "SMSettings");
            if (Directory.Exists(folder)) importFolder.Text = folder;
        }
        panel.Children.Add(Command("Выбрать папку настроек…", async () =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка SMSettings", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) importFolder.Text = path;
            }
            catch (Exception ex) { Error(ex); }
        }));
        CheckBox Choice(string label, bool enabled)
        {
            var box = new CheckBox { Content = Label(label), IsChecked = enabled };
            AutomationProperties.SetName(box, label); box.IsCheckedChanged += (_, _) => Invalidate(); return box;
        }
        importClients = Choice("Клиенты 1С", true); importFavorites = Choice("Избранное", true); importAppearance = Choice("Шрифт и высота строк списка", false);
        importAuthentication = Choice("Аутентификация и пароли баз", true);
        importProperties = Choice("Свойства баз и каталоги групп", true);
        importBackup = Choice("Настройки резервного копирования", true);
        ToolTip.SetTip(importBackup, "Сценарии резервирования (BackupScripts), сценарий, каталог и префикс каждой базы, общий «Каталог по-умолчанию»; " +
            "сценарии RAR переносятся как ZIP");
        // Everything the option takes from v8config.smc (LegacyImport.OtherProperties and the groups' Directory; Grok audit 14.10).
        ToolTip.SetTip(importProperties, "Комментарий, конфигурация и релиз, режим запуска, запуск от имени администратора, " +
            "проверка версии агента сервера, запрет удаления, двойной щелчок; каталоги групп");
        panel.Children.Add(importClients); panel.Children.Add(importFavorites); panel.Children.Add(importAuthentication); panel.Children.Add(importProperties); panel.Children.Add(importBackup); panel.Children.Add(importAppearance);
        panel.Children.Add(Command("Предпросмотр импорта", async () =>
        {
            if (busy || closed) return;
            busy = true; body.IsEnabled = commands.IsEnabled = false;
            preview = null;
            try
            {
                var requestedList = list.Text?.Trim() ?? "";
                if (requestedList.Length > 0 && requestedList != saved.ListPath)
                    throw new InvalidOperationException("Импорт открывает локальный список. Сначала примените выбранный общий список отдельно или выберите «Локальный список» перед предпросмотром.");
                var draft = (await Draft()).Library;
                if (closed) return;
                var local = LibrarySources.Remember(draft, settingsPath);
                if (draft.ListPath.Length > 0)
                {
                    local.Sources.TryGetValue(ActivityStore.Source(settingsPath, ""), out var previous);
                    // The folders of the groups go with the local list too: the import adds the original's ones only to groups without their own.
                    local = local with { ListPath = "", Bases = previous is null ? [] : [.. previous.Bases], Groups = previous is null ? [] : [.. previous.Groups],
                        GroupFolders = previous is null ? [] : [.. previous.GroupFolders] };
                }
                var file = importFile.Text?.Trim() ?? ""; var folder = importFolder.Text?.Trim();
                var clients = importClients.IsChecked == true; var favorites = importFavorites.IsChecked == true; var style = importAppearance.IsChecked == true;
                var authentication = importAuthentication.IsChecked == true; var properties = importProperties.IsChecked == true;
                var backup = importBackup.IsChecked == true;
                var scenarios = new BackupScenarioStore(settingsPath);
                var result = await Task.Run(() => LegacyImport.Preview(local, file, folder, new(clients, favorites, style, authentication, properties, backup), scenarios));
                if (closed) return;
                preview = result;
                importPreview.Text = $"Добавится баз: {preview.Added.Count}. Дубликатов: {preview.Duplicates}. Отметок избранного: {preview.Favorites.Count}.\n\n" +
                    string.Join("\n", preview.Added.Select(x => "+ " + x.Name)) + "\n\n" + string.Join("\n\n", preview.Notices);
                message.Text = "Предпросмотр готов. «Применить» или «Сохранить» выполнит импорт и откроет локальный список; настройки клиента и оформления также сохранятся.";
                message.Foreground = UiColors.Text;
            }
            catch (Exception ex) { if (!closed) { importPreview.Text = "Предпросмотр не подготовлен."; Error(ex); } }
            finally { busy = false; if (!closed) body.IsEnabled = commands.IsEnabled = true; }
        }));
        importPreview = Label("Выберите источник и подготовьте предпросмотр. Запись выполняется общими кнопками внизу окна.");
        panel.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = importPreview });
        importFile.TextChanged += (_, _) => Invalidate(); importFolder.TextChanged += (_, _) => Invalidate();
        return panel;
    }

    // The original's «Параметры резервного копирования» (smFormOptions.dfm Tab15): «Каталог по-умолчанию:». Its RAR group is not
    // made (RAR is not supported). Saved with «Применить» / «Сохранить», as the other settings.
    private Control BackupOptionsPage()
    {
        var panel = Panel();
        backupFolder = Field("Каталог по-умолчанию", saved.BackupFolder);
        backupFolder.TextChanged += (_, _) => Invalidate();
        var glyph = UiIcons.Create("folder", UiTypography.From(saved.Appearance).IconSize);
        glyph.Margin = new Thickness(6, 0, 0, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        backupFolder.InnerLeftContent = glyph;
        var browse = new Button { Content = "…", Padding = new Thickness(8, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(browse, "Выбрать каталог"); AutomationProperties.SetName(browse, "Выбрать каталог по-умолчанию");
        browse.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог по-умолчанию", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) backupFolder.Text = path;
            }
            catch (Exception ex) { Error(ex); }
        };
        backupFolder.InnerRightContent = browse;
        panel.Children.Add(DialogLayout.Field("Каталог по-умолчанию:", backupFolder));
        panel.Children.Add(Label("Общий каталог резервных копий. Каталог выгрузки выбирается так: «Каталог:» из свойств базы (относительный " +
            "отсчитывается от этого каталога), «1. Каталог:» сценария, папка Backup рядом с файловой базой, затем папка базы внутри этого каталога. " +
            "Пусто — не задан: для серверной базы без своего каталога выгрузка попросит указать его."));
        return DialogLayout.Section("Параметры резервного копирования", panel);
    }

    private string ReadBackupFolder()
    {
        var folder = backupFolder.Text?.Trim() ?? "";
        // A path of the other system stays as it is: a profile may be shared by Windows and Linux (BackupTarget.IsAbsolute).
        if (folder.Length > 0 && !BackupTarget.IsAbsolute(folder)) throw new ArgumentException("Каталог по-умолчанию: укажите полный путь или оставьте поле пустым.");
        if (folder.Any(char.IsControl)) throw new ArgumentException("Каталог по-умолчанию не должен содержать управляющие символы.");
        return folder;
    }

    // The original's «Сценарии резервирования» (smFormOptions.dfm Tab7, _Unit205.pas:10613-10960): «Создать», «Изменить», «Удалить»,
    // «Назначить основным», «Обновить»; the columns «Сценарий (наименование)» and «Шаблон имени файла»; under the list «Формат:», «ID:»,
    // «Описание:», «Файл:». As in the original each command writes the scenarios at once (the buttons at the bottom of the window do
    // not apply to them). The default scenario is bold and marked «(основной)».
    private Control BackupScenariosPage()
    {
        var typography = UiTypography.From(saved.Appearance);
        var store = new BackupScenarioStore(settingsPath);
        BackupScenarioSet? set = null;
        var working = false;
        var panel = Panel();
        var formatText = Label(""); var idText = Label(""); var descriptionText = Label(""); var fileText = Label(store.PathName);
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted };
        const string Columns = "3*,2*";
        TextBlock Cell(string text, bool header, bool bold = false) => new()
        {
            Text = text, Margin = new Thickness(5, 3), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = header || bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = header ? UiColors.Muted : UiColors.Text
        };
        Grid Row(TextBlock first, TextBlock second)
        {
            var row = new Grid { ColumnDefinitions = new(Columns) };
            var left = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = first };
            row.Children.Add(left); Grid.SetColumn(second, 1); row.Children.Add(second);
            return row;
        }
        var header = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0), Background = UiColors.Surface,
            Child = Row(Cell("Сценарий (наименование)", true), Cell("Шаблон имени файла", true)), HorizontalAlignment = HorizontalAlignment.Left };
        var list = new ListBox { Height = Math.Max(170, typography.DialogSize * 12), Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        // The rows follow the font, as the main list does (typography.RowHeight), and are never lower than a line of the dialog font
        // with the cell margins and the row line (Grok audit, part 3).
        var rowHeight = Math.Max(typography.RowHeight, Math.Ceiling(UiTypography.Measure("Йру Ágj", typography.DialogFamily, typography.DialogSize).Height) + 7);
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
            new Setter(ListBoxItem.MinHeightProperty, rowHeight) } });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Сценарии резервирования");
        // The header keeps the width of the rows when the list shows its scroll bar.
        list.LayoutUpdated += (_, _) =>
        {
            if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
            var width = scroll.Viewport.Width + 2;
            if (width > 2 && (double.IsNaN(header.Width) || Math.Abs(header.Width - width) > 0.5)) header.Width = width;
        };
        BackupScenario? Chosen() => set is not null && list.SelectedIndex >= 0 && list.SelectedIndex < set.Scenarios.Count ? set.Scenarios[list.SelectedIndex] : null;
        Button create = null!, edit = null!, remove = null!, setMain = null!, refresh = null!;
        void Buttons()
        {
            create.IsEnabled = set is not null && !working; refresh.IsEnabled = !working;
            edit.IsEnabled = remove.IsEnabled = setMain.IsEnabled = Chosen() is not null && !working;
            // The last scenario cannot be removed: the button says so instead of asking first and refusing after (Grok audit, part 3).
            if (set is { Scenarios.Count: <= 1 }) remove.IsEnabled = false;
            if (Chosen() is { } chosen && set?.Default?.Id == chosen.Id) setMain.IsEnabled = false;
        }
        void ShowChosen()
        {
            var chosen = Chosen();
            formatText.Text = chosen is null ? "" : BackupScenarioEdit.FormatText(chosen.Format);
            idText.Text = chosen?.Id.ToString("D") ?? "";
            descriptionText.Text = chosen?.ShownDescription ?? "";
            Buttons();
        }
        void Fill(Guid? select)
        {
            var rows = new List<Control>();
            foreach (var scenario in set?.Scenarios ?? [])
            {
                var main = set!.Default?.Id == scenario.Id;
                // The mark goes first, so a narrow column never cuts it off (decision of the orchestrator, part 3).
                var shown = main ? "(основной) " + scenario.Name : scenario.Name;
                var name = Cell(shown, false, main); ToolTip.SetTip(name, shown);
                var template = Cell(scenario.Template, false); ToolTip.SetTip(template, scenario.Template);
                var row = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = Row(name, template), MinHeight = rowHeight };
                name.VerticalAlignment = template.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(row, shown);
                rows.Add(row);
            }
            list.ItemsSource = rows;
            var index = set is null ? -1 : set.Scenarios.FindIndex(x => x.Id == select);
            list.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(0, index);
            ShowChosen();
        }
        async Task Load(Guid? select)
        {
            working = true; Buttons();
            try
            {
                set = await Task.Run(store.Load);
                state.Foreground = UiColors.Muted;
                state.Text = store.Saved ? "" : "Сценарии ещё не сохранялись: показаны готовые сценарии оригинала. Файл появится после первого изменения.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warning("backup.scenarios.read_failed", ex);
                set = null; state.Foreground = UiColors.Error;
                state.Text = "Сценарии резервирования не прочитаны: " + LegacyImport.ErrorMessage(ex) + " Файл не будет перезаписан; исправьте или удалите его и нажмите «Обновить».";
            }
            finally { working = false; }
            if (closed) return;
            Fill(select);
        }
        // Writes the whole set; a file changed by another window or a failed write is said and nothing is changed here.
        async Task<string?> Write(BackupScenarioSet next)
        {
            try
            {
                await Task.Run(() => store.Save(next));
                set = next; AppLog.Info("backup.scenarios.saved");
                state.Foreground = UiColors.Muted; state.Text = "";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                AppLog.Warning("backup.scenarios.save_failed", ex);
                return "Сценарии не сохранены: " + LegacyImport.ErrorMessage(ex);
            }
        }
        async Task Run(Func<Task> action)
        {
            if (working || closed) return;
            working = true; Buttons();
            try { await action(); }
            catch (Exception ex) { if (!closed) { AppLog.Error("settings.operation.failed", ex); ScenarioReport(LegacyImport.ErrorMessage(ex), true); } }
            finally { working = false; if (!closed) Buttons(); }
        }
        async Task Open(BackupScenario scenario, bool isNew)
        {
            var window = new BackupScenarioWindow(scenario, isNew, async next =>
            {
                if (set is null) return "Сценарии не прочитаны. Нажмите «Обновить».";
                return await Write(BackupScenarioEdit.Put(set, next));
            }, typography);
            await window.ShowDialog(this);
            if (closed) return;
            if (window.Saved) { Fill(scenario.Id); ScenarioReport($"Сценарий «{set?.Scenarios.FirstOrDefault(x => x.Id == scenario.Id)?.Name}» сохранён.", false); }
        }
        create = Command("Создать", async () => await Run(() => Open(BackupScenarioEdit.New(), true)));
        edit = Command("Изменить", async () => await Run(async () => { if (Chosen() is { } chosen) await Open(chosen, false); }));
        remove = Command("Удалить", async () => await Run(async () =>
        {
            if (set is null || Chosen() is not { } chosen) return;
            if (!await BackupScenarioWindow.Ask(this, typography, "Удаление сценария", "Вы уверены, что хотите удалить сценарий резервирования?\n" + chosen.Name, safeDefault: true)) return;
            var next = BackupScenarioEdit.Remove(set, chosen.Id);
            if (await Write(next) is { } failure) { ScenarioReport(failure, true); return; }
            Fill(next.Default?.Id);
            ScenarioReport($"Сценарий «{chosen.Name}» удалён. Базы, для которых он был выбран, используют сценарий по умолчанию.", false);
        }));
        setMain = Command("Назначить основным", async () => await Run(async () =>
        {
            if (set is null || Chosen() is not { } chosen) return;
            if (await Write(BackupScenarioEdit.WithDefault(set, chosen.Id)) is { } failure) { ScenarioReport(failure, true); return; }
            Fill(chosen.Id);
            ScenarioReport($"Сценарий «{chosen.Name}» назначен основным.", false);
        }));
        refresh = Command("Обновить", async () => { if (!working) await Load(Chosen()?.Id); });
        ToolTip.SetTip(create, "Добавить сценарий · Insert"); ToolTip.SetTip(edit, "Изменить сценарий · Enter"); ToolTip.SetTip(remove, "Удалить сценарий · Delete");
        ToolTip.SetTip(setMain, "Установить использование по умолчанию"); ToolTip.SetTip(refresh, "Перечитать");
        list.SelectionChanged += (_, _) => ShowChosen();
        list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is null || !edit.IsEnabled) return;
            e.Handled = true; edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            var button = e.KeyModifiers != Avalonia.Input.KeyModifiers.None ? null
                : e.Key switch { Avalonia.Input.Key.Enter => edit, Avalonia.Input.Key.Delete => remove, Avalonia.Input.Key.Insert => create, _ => null };
            if (button is null || !button.IsEnabled) return;
            e.Handled = true; button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { create, edit, remove, setMain, refresh }) { button.Margin = new Thickness(0, 0, 8, 6); toolbar.Children.Add(button); }
        setMain.Margin = new Thickness(12, 0, 8, 6);
        panel.Children.Add(toolbar);
        // Next to the commands, not below the list: they write at once (Codex audit, part 3).
        panel.Children.Add(new TextBlock { Text = ScenarioNote, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.SectionText });
        var table = new StackPanel(); table.Children.Add(header); table.Children.Add(list);
        panel.Children.Add(table);
        var details = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 4 };
        void Detail(int row, string caption, Control value)
        {
            var label = new TextBlock { Text = caption, Foreground = UiColors.Muted };
            Grid.SetRow(label, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            details.Children.Add(label); details.Children.Add(value);
        }
        // As Tab7: «Формат:» and «ID:» on one row; on a narrow page the ID goes under the format.
        var formatRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10 };
        formatText.MinWidth = 70; formatText.TextWrapping = TextWrapping.NoWrap;
        formatRow.Children.Add(formatText);
        var idPair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        idPair.Children.Add(new TextBlock { Text = "ID:", Foreground = UiColors.Muted }); idPair.Children.Add(idText);
        idText.TextWrapping = TextWrapping.NoWrap;
        formatRow.Children.Add(idPair);
        Detail(0, "Формат:", formatRow); Detail(1, "Описание:", descriptionText); Detail(2, "Файл:", fileText);
        panel.Children.Add(details);
        panel.Children.Add(state);
        panel.Children.Add(Label("Сценарий базы выбирается в её свойствах на странице «Резервное копирование»."));
        _ = Load(null);
        return DialogLayout.Section("Сценарии резервирования", panel);
    }
}
