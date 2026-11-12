using System.Collections.ObjectModel;
using System.Globalization;
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
    // The bottom message: only what just happened (an error, a result, unapplied changes); empty and hidden otherwise.
    private readonly TextBlock message = Label("");
    // The original's lbCaption: the name of the node in capitals, bold grey, above the frame of the page.
    // One line with «…» (the whole name in its tooltip): a wrapped title made the header band higher and moved the tree (Linux check 3).
    private readonly TextBlock title = new() { FontWeight = FontWeight.Bold, Foreground = UiColors.Muted, TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis };
    // The original's «StartManager 1.4» above the tree.
    private readonly TextBlock brand = new() { Text = "БазоНавигатор", FontWeight = FontWeight.Bold, Foreground = UiColors.Grey("#A6A6A6"),
        TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ContentControl page = new() { Margin = new Thickness(10, 8, 12, 10) };
    private UiTypography dialogType;
    /// <summary>F1 and Alt+F1: «Краткая справка» at its section «Настройки программы» (the owner opens it).</summary>
    public Action? HelpRequested { get; set; }
    private readonly List<Button> footerButtons = [];
    private readonly List<(TextBlock Label, Border Mark)> navigationLabels = [];
    private readonly Border frame;
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
    private double navigationText; // the widest whole caption at its place in the tree; 0 until measured
    private (FontFamily, double)? navigationFont;
    // Right of a caption: the item's padding and the tree's border (the vertical scroll bar of the theme overlays the items).
    private const double NavigationTrailing = 16;

    public SettingsWindow(Library library, string settingsPath, string section,
        Func<string, string, AppearanceSettings, Task<(Library Library, V8iStore? Source)>> prepare,
        Func<Library, V8iStore?, LegacyImportPreview?, Task<(Library Library, string? Warning)>> save)
    {
        saved = library; this.settingsPath = settingsPath; this.prepare = prepare; this.save = save; selectedSection = section;
        // As the original's caption; «БазоНавигатор» stands above the tree.
        Title = "Настройки программы";
        // About the original's 704×471 at the default dialog font; a larger font gets a larger window, so the style page keeps its
        // preview in view. As the original (bsDialog, the system menu only) it is not maximized; the size still changes.
        Width = 720; Height = 520; MinWidth = 380; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanMaximize = false; CanMinimize = false;
        var type = UiTypography.From(library.Appearance); dialogType = type;
        Width *= WindowScale(type.DialogSize); Height *= WindowScale(type.DialogSize);
        Background = UiColors.Surface; Foreground = UiColors.Text;
        var root = new Grid { Margin = new Thickness(6), RowDefinitions = new("*,Auto"), RowSpacing = 6 };
        // Wide: the header row (brand, title) and the tree beside the framed page; narrow: everything in one column.
        body = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,*,0,0"), ColumnSpacing = 4, RowSpacing = 4 };
        navigation = new TreeView { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), AutoScrollToSelectedItem = true };
        AutomationProperties.SetName(navigation, "Разделы настроек");
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        // As the original's tree: no expanders, every node stays open, a square before each caption.
        // A class makes the selector a style trigger: a plain style lost to the template part once the window's styles were replaced
        // after «Применить» (the expanders came back and moved every caption).
        navigation.Styles.Add(new Style(s => s.OfType<TreeViewItem>().Class("options").Template().OfType<Panel>().Name("PART_ExpandCollapseChevronContainer"))
        { Setters = { new Setter(IsVisibleProperty, false), new Setter(WidthProperty, 0d), new Setter(MarginProperty, new Thickness(0)) } });
        DialogLayout.YellowSelection(navigation);
        TreeViewItem Item(string text, string key, bool top = false)
        {
            var label = new TextBlock { Text = text, FontWeight = top ? FontWeight.Bold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            // Blue before a top node, grey before a nested one, as the original's #0098D8 and #B5B5B5.
            var mark = new Border { Background = top ? UiColors.SectionText : UiColors.Grey("#B5B5B5"), VerticalAlignment = VerticalAlignment.Center };
            var header = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(6, 0, 4, 0) };
            header.Children.Add(mark); Grid.SetColumn(label, 1); header.Children.Add(label);
            var item = new TreeViewItem { Header = header, Tag = key, IsExpanded = true, Classes = { "options" } };
            item.PropertyChanged += (_, e) => { if (e.Property == TreeViewItem.IsExpandedProperty && !item.IsExpanded) item.IsExpanded = true; };
            AutomationProperties.SetName(item, text); navigationItems.Add(item); navigationLabels.Add((label, mark)); return item;
        }
        var platform = Item("Платформа 1С", "platform");
        var versions = Item("Версии платформы", "versions");
        var branchesItem = Item("Настройки платформы", "branches");
        var lists = Item("Списки баз", "lists");
        var common = Item("Общие списки баз", "common");
        var import = Item("Импорт из StartManager", "import");
        var style = Item("Шрифты и тема", "appearance");
        var bases = Item("Информационные базы", "lists", true); bases.ItemsSource = new[] { versions, lists, common, import };
        var launching = Item("Параметры запуска", "platform", true); launching.ItemsSource = new[] { platform, branchesItem };
        var design = Item("Оформление", "appearance", true); design.ItemsSource = new[] { style };
        var general = Item("Общее", "general", true);
        var update = Item("Автообновление", "update", true);
        // The original's «Сценарии резервирования» with «Параметры» under it, before «Автообновление» (PROXY is not made).
        var backupItem = Item("Сценарии резервирования", "backup", true);
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
        body.Children.Add(brand);
        body.Children.Add(navigation); Grid.SetRow(navigation, 1);
        navigation.LayoutUpdated += (_, _) => FitNavigation();
        title.VerticalAlignment = VerticalAlignment.Bottom; title.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(title, 1); body.Children.Add(title);
        // The vertical bar always keeps its place: a page that only just fits wrapped its texts narrower once the bar appeared, then
        // needed it, and flipped between the two layouts (Linux check 2, font 12, «Общее»).
        pageScroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        // The original's page control with hidden tabs: a 1 px frame round the page.
        frame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Child = pageScroll };
        Grid.SetRow(frame, 1); Grid.SetColumn(frame, 1); body.Children.Add(frame); root.Children.Add(body);
        body.SizeChanged += (_, _) => ArrangeBody();
        // The message stands in the bottom bar between «Сохранить» and «Закрыть» (where the original has its empty status bar), so a
        // message after «Применить» does not make the page lower and scroll (Linux check 11.11); a narrow bar puts it above the buttons.
        // One line: a message longer than the bar is shortened with «…», and «Подробнее…» (a button, so the keyboard reaches it) shows
        // the whole text in a window where it can be selected; the bar keeps its height (Codex final recheck 11.11).
        commands = new WrapPanel { Orientation = Orientation.Horizontal };
        // The original's buttons and hints: «Применить» and «Сохранить» on the left, «Закрыть» on the right.
        var apply = Command("Применить", () => Commit(false)); ToolTip.SetTip(apply, "Сохранить изменения");
        var commit = Command("Сохранить", () => Commit(true)); ToolTip.SetTip(commit, "Сохранить и закрыть");
        commands.Children.Add(apply); commands.Children.Add(commit);
        var close = Command("Закрыть", Close); close.IsCancel = true; close.Margin = default; ToolTip.SetTip(close, "Отменить изменения");
        // The summary of unapplied changes always offers the whole warning.
        var footer = DialogLayout.Footer(this, message, commands, close, () => dialogType, text => text == UnappliedSummary ? UnappliedMessage : null);
        footerButtons.AddRange([apply, commit, close]);
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        ApplyFont(type);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.F1 || (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Alt) != 0 || HelpRequested is not { } help) return;
            e.Handled = true; help();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

    // The window grows with the dialog font by part of its growth, as UiTypography.DialogScale from the default size of 12.
    private static double WindowScale(double size) => 1 + Math.Max(0, size / AppearanceSettings.DefaultDialogSize - 1) * .6;

    /// <summary>The dialog font of the window and everything sized by it: the compact controls, the rows of the tree, its squares,
    /// the header and the bottom buttons (at first and after «Применить» changed the font).</summary>
    private void ApplyFont(UiTypography type)
    {
        dialogType = type;
        FontFamily = type.DialogFamily; FontSize = type.DialogSize;
        DialogLayout.Compact(this, type);
        // The original's 18 px rows at 8 pt: the line and a little room; the levels about 16 apart.
        DialogLayout.CompactTree(navigation, type);
        var mark = Math.Max(7, Math.Round(type.DialogSize * .65));
        foreach (var (_, square) in navigationLabels) square.Width = square.Height = mark;
        brand.FontSize = type.DialogSize + 2; title.FontSize = type.DialogSize + 1;
        // As the original's 87…119 px buttons at 8 pt.
        foreach (var button in footerButtons) button.MinWidth = Math.Round(type.DialogSize * 8);
        // The least size follows the font: the three buttons side by side and, in the narrow layout, a few rows of the tree, the title
        // and a page of several fields above the bottom bar. At font 22 the fixed 380×350 left no page and «Закрыть» past the right
        // edge (Codex recheck 12.11, P2). FitToScreen still caps it by the screen.
        MinWidth = Math.Max(380, 3 * Math.Round(type.DialogSize * 8) + 60);
        MinHeight = Math.Max(350, Math.Round(type.ControlHeight * 14));
        navigationFont = null;
        if (body.ColumnDefinitions[0].Width.IsAbsolute) body.ColumnDefinitions[0].Width = new GridLength(NavigationWidth());
    }

    private void ArrangeBody()
    {
        var narrow = body.Bounds.Width < 620;
        body.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(NavigationWidth());
        body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        body.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        body.RowDefinitions[2].Height = narrow ? GridLength.Auto : new GridLength(0);
        body.RowDefinitions[3].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        navigation.MaxHeight = narrow ? Math.Max(130, FontSize * 8) : double.PositiveInfinity;
        Grid.SetColumn(title, narrow ? 0 : 1); Grid.SetRow(title, narrow ? 2 : 0);
        Grid.SetColumn(frame, narrow ? 0 : 1); Grid.SetRow(frame, narrow ? 3 : 1);
        if (navigation.SelectedItem is TreeViewItem selected)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => selected.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // The tree is as wide as its longest caption on one line (about the original's 200); a large font takes at most 45 % of the
    // window and wraps the captions between words, never inside one.
    private double NavigationWidth()
    {
        var wanted = TextFit.Fit(Math.Clamp(FontSize * 16, 200, 260), navigationText, NavigationTrailing);
        var limit = body.Bounds.Width > 0 ? body.Bounds.Width * .45 : double.PositiveInfinity;
        return Math.Max(TextFit.Fit(0, navigationWords, NavigationTrailing), Math.Min(wanted, limit));
    }

    // Measured once per font, after the tree is laid out: the left place of each caption (level, square) comes from the layout,
    // the widths of the caption and of its widest word from the font and the weight of its node.
    private void FitNavigation()
    {
        if (navigationFont == (FontFamily, FontSize)) return;
        double words = 0, text = 0;
        foreach (var (label, _) in navigationLabels)
        {
            if (label.Bounds.Width <= 0 || label.TranslatePoint(default, navigation) is not { } at) return;
            double Width(string value) => new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, FontStyle.Normal, label.FontWeight), label.FontSize, Brushes.Black).Width;
            words = Math.Max(words, at.X + TextFit.WidestWord([label.Text], Width));
            text = Math.Max(text, at.X + Width(label.Text ?? ""));
        }
        navigationFont = (FontFamily, FontSize); navigationWords = words; navigationText = text;
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
        updateMode.ItemTemplate = DialogLayout.OneLine; DialogLayout.TipOnFocus(updateMode);
        AutomationProperties.SetName(updateMode, "Способ обновлений БазоНавигатора");
        updateMode.SelectionChanged += (_, _) => Invalidate();
        // As the original's Tab13: the caption above the list.
        ToolTip.SetTip(updateMode, "Проверка выполняется при запуске программы в фоне. Загруженная версия начинает работать после перезапуска.");
        page.Children.Add(DialogLayout.Above("Способ обновлений БазоНавигатора:", updateMode));
        // The original keeps every backup; here the archive next to the program keeps the newest of them (the user's choice 15.10).
        keepVersions = new NumericUpDown { Minimum = 1, Maximum = Updater.MaxKeep, Increment = 1, FormatString = "0", Value = saved.KeepVersions,
            ClipValueToMinMax = true, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = Math.Round(FontSize * 5) };
        AutomationProperties.SetName(keepVersions, "Хранить прежних версий");
        keepVersions.ValueChanged += (_, _) => Invalidate();
        ToolTip.SetTip(keepVersions, "Прежние версии сохраняются в папке .versions рядом с программой при установке обновлений и при восстановлении; " +
            "лишние, самые старые, удаляются при следующей установке или восстановлении. Копия установленной сейчас версии в это число не входит.");
        page.Children.Add(DialogLayout.Field("Хранить прежних версий:", keepVersions));
        // Copies beyond the number are deleted: that stays on the page (Codex audit 11.11).
        page.Children.Add(Note("Лишние, самые старые копии в папке .versions удаляются при следующей установке или восстановлении."));

        var source = Panel();
        // The original's grey lblUpdNote: one line; the conditions in its tooltip and the help.
        var sourceNote = Note($"Выпуски берутся из публичного репозитория {GitHubReleaseSource.DefaultRepository} по HTTPS.");
        ToolTip.SetTip(sourceNote, "Системный прокси или переменная окружения HTTPS_PROXY; раздел PROXY — в планах. " +
            "Для закрытого репозитория нужен GitHub CLI (gh) с выполненным «gh auth login»; токен хранит gh, программа его не читает.");
        source.Children.Add(sourceNote);
        otherRepository = new CheckBox { Content = Label("Использовать другой репозиторий:"), IsChecked = saved.UpdateRepository.Length > 0 };
        updateRepository = new TextBox { Text = saved.UpdateRepository, PlaceholderText = "владелец/репозиторий", IsEnabled = otherRepository.IsChecked == true };
        AutomationProperties.SetName(updateRepository, "Репозиторий обновлений");
        otherRepository.IsCheckedChanged += (_, _) => { updateRepository.IsEnabled = otherRepository.IsChecked == true; Invalidate(); };
        updateRepository.TextChanged += (_, _) => Invalidate();
        // As the original's «Использовать хост: [ ]»: the check box and its field on one line.
        source.Children.Add(DialogLayout.Inline(otherRepository, updateRepository));

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

        return Page(Group("Автообновление", page), Group("Источник получения обновлений", source), Group("Текущая версия", manual));
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
    private static StackPanel Panel() => DialogLayout.Rows();
    private StackPanel Group(string? caption, Control content) => DialogLayout.Group(caption, content, dialogType.DialogSize);
    private TextBlock Note(string text) => DialogLayout.Note(text, dialogType.DialogSize);
    /// <summary>A page: its groups 14 apart, the captions of its fields in one column as wide as the longest of them.</summary>
    private static Control Page(params Control[] groups)
    {
        var page = DialogLayout.Groups(groups);
        DialogLayout.AlignCaptions(page);
        return page;
    }
    private TextBox PathField(TextBox box, string icon, string tip, Func<Task> pick) =>
        DialogLayout.PathBox(box, icon, dialogType.DialogIconSize, tip, async () => { try { await pick(); } catch (Exception ex) { Error(ex); } });
    private async Task PickFolder(string caption, TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = caption, AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
    }
    /// <summary>A caption and a value, as «Файл:» of the original's scenarios: the paths of the settings file and the log.</summary>
    private static Control Value(string caption, string value)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(text, value);
        return DialogLayout.Field(caption, text);
    }
    private const string UnappliedMessage = "Есть неприменённые изменения настроек. «Закрыть» отменяет их; сценарии резервирования записываются сразу и не отменяются.";
    // The bar shows the first sentence; «Подробнее…» shows the whole warning (Codex final recheck 11.11: a concise summary in one line).
    private const string UnappliedSummary = "Есть неприменённые изменения настроек.";
    private bool Dirty => !loading && DraftToken() != cleanToken;
    /// <summary>The result of a scenario command; while other settings are unapplied their warning stays after it (Grok audit, part 3).</summary>
    private void ScenarioReport(string text, bool error)
    {
        message.Text = Dirty ? text + " " + UnappliedMessage : text;
        message.Foreground = error ? UiColors.Error : UiColors.Text;
    }
    private const string ScenarioNote = "Команды этого раздела записывают сценарии сразу, как в оригинале: «Применить», «Сохранить» и «Закрыть» к ним не относятся.";
    private void ShowSection(string key)
    {
        // Another section opens from its top (e2e Themes 24.10); rebuilt pages of the same section keep their position.
        if (key != selectedSection) pageScroll.Offset = default;
        selectedSection = key; page.Content = pages[key];
        title.Text = key switch { "general" => "ОБЩЕЕ", "platform" => "ПЛАТФОРМА 1С", "lists" => "СПИСКИ БАЗ", "common" => "ОБЩИЕ СПИСКИ БАЗ", "versions" => "ВЕРСИИ ПЛАТФОРМЫ", "branches" => "НАСТРОЙКИ ПЛАТФОРМЫ", "import" => "ИМПОРТ ИЗ STARTMANAGER", "update" => "АВТООБНОВЛЕНИЕ",
            "backup" => "СЦЕНАРИИ РЕЗЕРВИРОВАНИЯ", "backupOptions" => "ПАРАМЕТРЫ", _ => "ШРИФТЫ И ТЕМА" };
        ToolTip.SetTip(title, title.Text);
    }
    private void BuildPages()
    {
        loading = true;
        preview = null;
        platformEditor = new PlatformEditor(this, saved.Executable, saved.PlatformRule, typography: dialogType);
        client = platformEditor.Client;
        list = Field("Путь к общему списку", saved.ListPath);
        list.PlaceholderText = "Пусто — локальный список БазоНавигатора";
        PathField(list, "file", "Выбрать общий список…", async () => { if (await PickFile("Выберите общий список", "*.v8i") is { } path) list.Text = path; });
        appearance = new AppearanceEditor(saved.Appearance);
        var general = Panel(); var diagnostics = Panel();
        singleInstance = new CheckBox { Content = Label("Запрещать запуск второго экземпляра"), IsChecked = saved.PreventMultipleInstances };
        ToolTip.SetTip(singleInstance, "Для одного файла настроек. Применяется после перезапуска всех открытых экземпляров. Ярлыки запуска баз продолжают работать.");
        singleInstance.IsCheckedChanged += (_, _) => Invalidate();
        ComboBox ActionChoice(string name, string[] choices, int selected)
        {
            var choice = new ComboBox { ItemsSource = choices, SelectedIndex = selected,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            choice.ItemTemplate = DialogLayout.OneLine; DialogLayout.FitChoices(choice, dialogType);
            AutomationProperties.SetName(choice, name);
            choice.SelectionChanged += (_, _) => Invalidate();
            (name.StartsWith("Диагност", StringComparison.Ordinal) ? diagnostics : general).Children.Add(DialogLayout.Field(name + ":", choice)); return choice;
        }
        // The original's order and words; «Свернуть в трэй» is not offered: the program has no tray icon.
        afterLaunch = ActionChoice("Действие после запуска 1С", ["Свернуть в панель задач", "Закрыть БазоНавигатор", "Ничего не делать"],
            Array.IndexOf(AfterLaunchOrder, saved.AfterLaunch));
        ToolTip.SetTip(afterLaunch, "Окно сворачивается или закрывается после успешного создания процесса 1С и сохранения истории. При ошибке или несохранённых настройках окно остаётся открытым.");
        doubleClick = ActionChoice("Действие при двойном щелчке", ["1С:Предприятие", "Конфигуратор"], (int)saved.DoubleClick);
        general.Children.Add(singleInstance);
        // The original's «Кнопку "1С:Стартер"» (ShowStarterBtn).
        starterButton = new CheckBox { Content = Label("Кнопку «1С:Стартер»"), IsChecked = !saved.HideStarterButton };
        AutomationProperties.SetName(starterButton, "Отображать кнопку «1С:Стартер»");
        ToolTip.SetTip(starterButton, "Стартер открывает штатное окно 1С со своим списком баз: для платформы выбранной базы, а «▾» — по версии.");
        starterButton.IsCheckedChanged += (_, _) => Invalidate();
        // The original's group «Отображать:».
        var show = Panel(); show.Children.Add(starterButton);
        general.Children.Add(Value("Файл настроек:", Path.GetFullPath(settingsPath)));
        var storage = Command("Обычный / портативный режим…", async () =>
        {
            try { await new StorageModeWindow(settingsPath, saved.Appearance).ShowDialog(this); }
            catch (Exception ex) { Error(ex); }
        });
        storage.HorizontalAlignment = HorizontalAlignment.Left;
        general.Children.Add(storage);
        logLevel = ActionChoice("Диагностический журнал", ["Выключен", "Только ошибки", "Ошибки и предупреждения", "Ошибки, предупреждения и информация", "Все события (подробно)"], (int)DiagnosticPreferences.Read(saved.Diagnostics));
        var logFolder = Value("Журнал:", AppLog.DirectoryPath);
        ToolTip.SetTip(logFolder, "История запусков ведётся отдельно.");
        diagnostics.Children.Add(logFolder);
        // What this run changed or lost stays on the page.
        var logNote = (AppLog.Overridden ? "На время этого запуска уровень задан командной строкой. " : "") +
            (AppLog.Failure is { } failure ? "В этом сеансе были потери записей или ошибки журнала: " + failure : "");
        if (logNote.Length > 0) diagnostics.Children.Add(Note(logNote.Trim()));
        // Our addition without a counterpart in the original, below its groups; Linux only.
        pages["general"] = OperatingSystem.IsLinux()
            ? Page(Group("Основные параметры", general), Group("Отображать:", show), Group("Диагностика", diagnostics), MenuEntrySection())
            : Page(Group("Основные параметры", general), Group("Отображать:", show), Group("Диагностика", diagnostics));
        pages["update"] = UpdatePage();
        var platform = Panel(); platform.Children.Add(platformEditor);
        // The original's page «Исполняемые файлы»: «Пути поиска платформы», the column «Папка размещения платформы», add and remove.
        platformFolders = new ObservableCollection<string>(saved.PlatformFolders);
        var folderList = new ListBox { ItemsSource = platformFolders, Height = Math.Max(90, FontSize * 7), Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(folderList, "Папка размещения платформы");
        DialogLayout.YellowSelection(folderList);
        var folderButtons = new StackPanel { Spacing = 4 };
        var addFolder = Command("Добавить…", async () =>
        {
            try
            {
                var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка размещения платформы", AllowMultiple = true });
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                foreach (var folder in chosen)
                    if (folder.TryGetLocalPath() is { } path && PlatformCatalog.NormalizeFolders([path]) is [var normal] && !platformFolders.Contains(normal, comparer)) platformFolders.Add(normal);
            }
            catch (Exception ex) { Error(ex); }
        });
        var removeFolder = Command("Удалить", () => { if (folderList.SelectedItem is string selected) platformFolders.Remove(selected); });
        // As the original: a green «+» and a red «×» right of the list.
        folderButtons.Children.Add(DialogLayout.Icon(addFolder, "add", "Добавить папку размещения платформы…", dialogType.DialogIconSize));
        folderButtons.Children.Add(DialogLayout.Icon(removeFolder, "delete", "Удалить выбранную папку из списка", dialogType.DialogIconSize));
        var folderTable = new StackPanel();
        folderTable.Children.Add(ColumnHeader("Папка размещения платформы")); folderTable.Children.Add(folderList);
        var folderRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        folderRow.Children.Add(folderTable); Grid.SetColumn(folderButtons, 1); folderRow.Children.Add(folderButtons);
        var folders = Panel();
        // The original's two lines above the list; the standard folders (searched always) in the tooltip and the help.
        var foldersNote = Note("Если имеются версии платформы, установленные нестандартным способом, здесь можно указать каталоги их размещения.");
        ToolTip.SetTip(foldersNote, "Поиск идёт в них и в подкаталогах до четырёх уровней; стандартные каталоги ищутся всегда: " +
            string.Join("; ", PlatformCatalog.StandardRoots().Distinct()) + ".");
        folders.Children.Add(foldersNote);
        folders.Children.Add(folderRow);
        platformFolders.CollectionChanged += (_, _) => Invalidate();
        var lists = Panel();
        lists.Children.Add(DialogLayout.Field("Общий список .v8i:", list));
        var local = Command("Локальный список", () => list.Text = "");
        ToolTip.SetTip(local, "Очистить поле: открывать локальный список БазоНавигатора");
        local.HorizontalAlignment = HorizontalAlignment.Left;
        lists.Children.Add(local);
        // The shared file is written directly afterwards: that stays on the page.
        var listsNote = Note("Переключение произойдёт после сохранения. При дальнейшей работе общий .v8i редактируется напрямую.");
        ToolTip.SetTip(listsNote, "Подключения прежнего источника сохраняются.");
        lists.Children.Add(listsNote);
        pages["platform"] = Page(Group("Используемая платформа", platform), Group("Пути поиска платформы «1С:Предприятие 8»", folders));
        pages["lists"] = Page(Group("Открываемый список информационных баз", lists)); pages["appearance"] = appearance;
        pages["common"] = CommonListsPage();
        pages["versions"] = VersionsPage(); pages["branches"] = BranchesPage();
        pages["import"] = ImportPage();
        pages["backup"] = BackupScenariosPage(); pages["backupOptions"] = BackupOptionsPage();
        client.TextChanged += (_, _) => Invalidate(); list.TextChanged += (_, _) => Invalidate(); appearance.Changed += Invalidate;
        platformEditor.Changed += Invalidate;
        draftToken = cleanToken = DraftToken(); loading = false; ShowSection(selectedSection);
    }

    /// <summary>The caption of a list column, as the original's list view headers.</summary>
    private static Border ColumnHeader(string text) => new()
    {
        BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0), Background = UiColors.Surface,
        Child = new TextBlock { Text = text, Margin = new Thickness(5, 2), Foreground = UiColors.Muted, TextTrimming = TextTrimming.CharacterEllipsis }
    };
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
        panel.Children.Add(Note("Кнопки действуют сразу: «Применить», «Сохранить» и «Закрыть» к ярлыку не относятся."));
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
                message.Text = Dirty ? UnappliedSummary : "";
                message.Foreground = UiColors.Muted;
                report = null;
            }
            Show(current);
        };
        return Group("Меню приложений", panel);
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
        // Linux 5, Н2: the notice follows the saved state (Dirty), not the previous draft: a value changed back clears it. The
        // message line keeps its height when empty (DialogLayout.Footer), so the page does not move.
        message.Text = token != cleanToken ? UnappliedSummary : ""; message.Foreground = UiColors.Muted;
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
            ApplyFont(UiTypography.From(saved.Appearance));
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
        PathField(basesFolder, "folder", "Выбрать каталог информационных баз", () => PickFolder("Каталог информационных баз", basesFolder));
        ToolTip.SetTip(basesFolder, "Единый каталог: в нём предлагается размещение новой файловой базы. Пусто — не предлагать.");
        panel.Children.Add(DialogLayout.Field("Каталог информационных баз:", basesFolder));
        // A short list as the original's W68: as wide as its longest choice.
        mainVersion = new ComboBox { ItemsSource = MainVersionChoices, HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = Math.Ceiling(UiTypography.Measure(MainVersionChoices[0], dialogType.DialogFamily, dialogType.DialogSize).Width) + 48,
            SelectedIndex = Math.Max(0, Array.IndexOf(PlatformBranches.Shown, saved.MainVersion) + 1) };
        AutomationProperties.SetName(mainVersion, "Основная версия платформы");
        ToolTip.SetTip(mainVersion, "Версия, по которой запускаются базы без версии в списке, и ветка, чьи «Настройки платформы» к ним применяются. " +
            "«Как в настройках 1CEStart» — версия по умолчанию стартера 1С.");
        mainVersion.SelectionChanged += (_, _) => Invalidate();
        panel.Children.Add(DialogLayout.Field("Основная версия платформы:", mainVersion));
        return Page(Group("Дополнительные сведения", panel));
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
        const string BranchNote = "Для баз ветки, у которых в свойствах не задан свой клиент или своя версия. Версия ветки не заменяет полный номер " +
            "сборки из списка (например, 8.3.27.1989); исполняемый файл ветки применяется ко всем её базам. Пусто — общая настройка «Платформа 1С».";
        foreach (var branch in PlatformBranches.Shown)
        {
            var current = saved.Branches.FirstOrDefault(x => x.Branch == branch);
            // As the original's block of a branch: its bold grey caption on the row of the way of choosing, then the file and the parameters.
            var editor = new PlatformEditor(this, current?.Executable ?? "", current?.PlatformRule, individual: true, typography: dialogType,
                caption: $"Платформа 1С {branch}:", modeTip: BranchNote);
            editor.Changed += Invalidate; editor.Client.TextChanged += (_, _) => Invalidate();
            var parameters = Field($"Параметры запуска для {branch}", current?.Parameters ?? "");
            parameters.PlaceholderText = "Например, /DisableStartupMessages";
            parameters.TextChanged += (_, _) => Invalidate();
            // The original's TBevel between the blocks.
            if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 1, Background = UiColors.Line, Margin = new Thickness(0, 4) });
            panel.Children.Add(editor); panel.Children.Add(DialogLayout.Field("Параметры:", parameters));
            branchEditors.Add((branch, editor, parameters));
        }
        return Page(Group("Использование релизов платформы 1С:Предприятие 8", panel));
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
        ToolTip.SetTip(useCommonLists, "Использовать общие списки информационных баз 1С:Предприятие 8");
        skipReadOnlyCommonLists = new CheckBox { Content = Label("Игнорировать файлы списка баз, имеющие атрибут «Только чтение»"), IsChecked = saved.SkipReadOnlyCommonLists };
        useCommonLists.IsCheckedChanged += (_, _) => Invalidate(); skipReadOnlyCommonLists.IsCheckedChanged += (_, _) => Invalidate();
        var files = new ObservableCollection<string>();
        var paths = new List<string>();
        var view = new ListBox { ItemsSource = files, Height = Math.Max(110, FontSize * 10), Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(view, "Общие списки баз");
        DialogLayout.YellowSelection(view);
        ToolTip.SetTip(view, "Общие списки задаются строками CommonInfoBases= в настройках 1CEStart (1CEStart.cfg), как и для самой 1С. " +
            "Их базы показываются в общем дереве после баз открытого списка с отметкой рамкой; база с тем же ID, что уже есть в дереве, " +
            "не повторяется. Такие базы можно запускать и смотреть их свойства, а изменить — только в самом списке.");
        var found = Note("Поиск общих списков в настройках 1CEStart…");
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
        var buttons = new StackPanel { Spacing = 4 };
        // As the original: the folder first, then the file, as flat icons right of the list with its hints.
        buttons.Children.Add(DialogLayout.Icon(Command("Открыть каталог", OpenFolder), "folder", "Открыть каталог размещения файла", dialogType.DialogIconSize, flat: true));
        buttons.Children.Add(DialogLayout.Icon(Command("Открыть файл", OpenFile), "file", "Открыть файл", dialogType.DialogIconSize, flat: true));
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(view); Grid.SetColumn(buttons, 1); row.Children.Add(buttons);
        panel.Children.Add(useCommonLists);
        panel.Children.Add(row);
        panel.Children.Add(found);
        panel.Children.Add(skipReadOnlyCommonLists);
        return Page(Group("Использование общих списков баз платформы 1С:Предприятие 8", panel));
    }
    private Control ImportPage()
    {
        var panel = Panel();
        // What the import writes and keeps stays on the page.
        panel.Children.Add(Note("Импорт дополняет локальный список. Исходники и общий .v8i не меняются. Перед записью существующих настроек создаётся .bak."));
        importFile = Field("Источник импорта");
        PathField(importFile, "file", "Выбрать список или снимок…", async () => { if (await PickFile("Источник импорта", "*.v8i", "*.v8sx") is { } path) importFile.Text = path; });
        panel.Children.Add(DialogLayout.Field("Список .v8i или снимок .v8sx:", importFile));
        importFolder = Field("Папка настроек оригинала");
        importFolder.PlaceholderText = "Необязательно";
        if (OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartManager14", "SMSettings");
            if (Directory.Exists(folder)) importFolder.Text = folder;
        }
        PathField(importFolder, "folder", "Выбрать папку настроек…", () => PickFolder("Папка SMSettings", importFolder));
        panel.Children.Add(DialogLayout.Field("Папка SMSettings оригинала:", importFolder));
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
        // Two columns, as the original's groups of check boxes.
        var choices = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var box in new[] { importClients, importFavorites, importAuthentication, importProperties, importBackup, importAppearance })
        { box.Margin = new Thickness(0, 0, 8, 0); choices.Children.Add(box); }
        panel.Children.Add(choices);
        var previewButton = Command("Предпросмотр импорта", async () =>
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
        });
        previewButton.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(previewButton);
        importPreview = Label("Выберите источник и подготовьте предпросмотр. Запись выполняется общими кнопками внизу окна.");
        panel.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(6, 4), Background = UiColors.Field, Child = importPreview });
        importFile.TextChanged += (_, _) => Invalidate(); importFolder.TextChanged += (_, _) => Invalidate();
        // The page's title names it: no group caption.
        return Page(Group(null, panel));
    }

    // The original's «Параметры резервного копирования» (smFormOptions.dfm Tab15): «Каталог по-умолчанию:». Its RAR group is not
    // made (RAR is not supported). Saved with «Применить» / «Сохранить», as the other settings.
    private Control BackupOptionsPage()
    {
        var panel = Panel();
        backupFolder = Field("Каталог по-умолчанию", saved.BackupFolder);
        backupFolder.TextChanged += (_, _) => Invalidate();
        PathField(backupFolder, "folder", "Выбрать каталог по-умолчанию", () => PickFolder("Каталог по-умолчанию", backupFolder));
        ToolTip.SetTip(backupFolder, "Общий каталог резервных копий. Каталог выгрузки выбирается так: «Каталог:» из свойств базы (относительный " +
            "отсчитывается от этого каталога), «1. Каталог:» сценария, папка Backup рядом с файловой базой, затем папка базы внутри этого каталога. " +
            "Пусто — не задан: для серверной базы без своего каталога выгрузка попросит указать его.");
        // As the original's Tab15: the caption above the field.
        panel.Children.Add(DialogLayout.Above("Каталог по-умолчанию:", backupFolder));
        return Page(Group("Параметры резервного копирования", panel));
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
        var state = Note("");
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
        var list = new ListBox { Height = Math.Max(130, typography.DialogSize * 11), Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        DialogLayout.YellowSelection(list);
        ToolTip.SetTip(list, "Сценарий базы выбирается в её свойствах на странице «Резервное копирование».");
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
        ToolTip.SetTip(create, "Добавить сценарий · Ins"); ToolTip.SetTip(edit, "Изменить сценарий · Enter"); ToolTip.SetTip(remove, "Удалить сценарий · Del");
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
        // The original's tool bar: «+», pencil, «×», a separator, «назначить основным»; «Обновить» at the right edge.
        foreach (var (button, icon) in new[] { (create, "add"), (edit, "pencil"), (remove, "delete"), (setMain, "default"), (refresh, "refresh") })
        {
            DialogLayout.Icon(button, icon, ToolTip.GetTip(button) as string ?? "", typography.DialogIconSize, flat: true);
            button.Margin = new Thickness(0, 0, 2, 0);
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        tools.Children.Add(create); tools.Children.Add(edit); tools.Children.Add(remove);
        tools.Children.Add(new Border { Width = 1, Background = UiColors.Line, Margin = new Thickness(4, 3) });
        tools.Children.Add(setMain);
        var toolbar = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh); toolbar.Children.Add(tools);
        panel.Children.Add(toolbar);
        // Next to the commands, not below the list: they write at once (Codex audit, part 3).
        panel.Children.Add(Note(ScenarioNote));
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
        _ = Load(null);
        // The page's title names it, as the original's Tab7 without a caption.
        return Page(Group(null, panel));
    }
}
