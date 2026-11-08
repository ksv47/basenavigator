using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow : Window
{
    private UiTypography typography = UiTypography.Default;
    private LibraryStore store;
    private Library library = new();
    private V8iStore? sharedList;
    private ListBox bases = new();
    private Button? addButton;
    private bool adding;
    private TextBox search = new() { PlaceholderText = "Поиск базы…" };
    private SearchMode searchMode = SearchMode.Name;
    private ListColumns columns = new();
    private Grid listHeader = new();
    private TextBox executable = new() { PlaceholderText = "Полный путь к 1cv8.exe или 1cv8" };
    private TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private TextBlock count = new();
    // The original's status bar panels: the platform, size and client of the selected base, the list load time.
    private TextBlock statusVersion = new(), statusSize = new(), statusClient = new(), statusTime = new();
    private double? loadSeconds;
    private TextBlock connection = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    // The buttons of the table of commands (MainCommands): RefreshCommands turns each on or off by its rule.
    private readonly Dictionary<Button, MainCommand> commandButtons = [];
    private Button? serviceButton;
    private readonly List<Button> launchButtons = [];
    private readonly List<Button> designerButtons = [];
    // «Текущая сессия»: the client for the next start of 1С:Предприятие, not saved in the settings.
    private SessionClient session = SessionClient.Auto;
    private readonly List<RadioButton> sessionOptions = [];
    private ComboBox? sessionChoice;
    private StackPanel? sessionItems;
    private double sessionHeight;
    private int sessionRevision;
    private readonly HashSet<LibrarySection> collapsedSections = [];
    private bool loadFailed;
    private bool reading;
    private bool closed;
    private bool writing;
    private bool profilePending;
    private readonly string? initialListPath;
    private bool listOverrideActive;
    private bool initialListLoaded;
    private string? savedDefaultList;
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, BaseInfo> information = [];
    private readonly SemaphoreSlim infoGate = new(1);
    private int infoGeneration;
    private enum LibrarySection { None, Favorites, All, Orphans }
    // Orphan: a row of «Отсутствующие конфигурации» — cache folders of a configuration no known base uses; not a group.
    private sealed record LibraryRow(BaseEntry? Entry, string Group, int Depth, string Name,
        LibrarySection Section = LibrarySection.None, bool FavoriteShortcut = false, OrphanCacheEntry? Orphan = null)
    {
        public string Key => Section != LibrarySection.None ? "section:" + Section : Orphan is not null ? "orphan:" + Orphan.Id
            : Entry is not null ? (FavoriteShortcut ? "favorite:" : "") + Entry.Id : "group:" + Group;
        public override string ToString() => Name;
    }

    public MainWindow(string? settingsPath = null, string? listPath = null)
    {
        initialListPath = listPath;
        listOverrideActive = listPath is not null;
        store = new(settingsPath ?? LibraryStore.DefaultPath);
        recoveryDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.PathName))
            ?? throw new ArgumentException("Укажите файл настроек, а не корень диска."), "Recovery");
        activityStore = new(store.PathName);
        Width = 717; Height = 590; MinWidth = 664; MinHeight = 460;
        UiTypography.FitOnScreenChanges(this);
        // The window's keys come from the table of Shortcuts (the same table «Краткая справка» lists); a key whose command
        // is not available now (MainCommands: the list is being read, nothing suitable is selected) stays unhandled.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (ShortcutKeys.Find(ShortcutScope.Window, e) is not { } command) return;
            if (!MainCommands.CanExecute(MainCommands.Of(command), CurrentState())) { e.Handled = Refuse(MainCommands.Of(command), list: false); return; }
            System.Action? action = command switch
            {
                ShortcutCommand.ShortHelp => ShowShortHelp,
                // As the original: Ctrl+Shift+F and Ctrl+Alt+F choose the search by the name or by the connection string.
                ShortcutCommand.Search => () => FocusSearch(null),
                ShortcutCommand.SearchByName => () => FocusSearch(SearchMode.Name),
                ShortcutCommand.SearchByConnection => () => FocusSearch(SearchMode.Connection),
                // The original's Alt+F10 «Сервисное меню».
                ShortcutCommand.ServiceMenu => () => { if (serviceButton is { } anchor) ShowServiceMenu(anchor); },
                ShortcutCommand.Properties => EditSelected,
                ShortcutCommand.AssignVersion => AssignVersion,
                ShortcutCommand.ToggleFavorite => ToggleFavorite,
                ShortcutCommand.Snapshots => ShowSnapshots,
                // The original's keys: F5 «Перечитать список баз», F12/F11 «Развернуть/Свернуть все», Ctrl+F3/Ctrl+F4 «Запустить с выбором параметров».
                ShortcutCommand.Reload => Reload,
                ShortcutCommand.CollapseAll => () => SetGroupsCollapsed(true),
                ShortcutCommand.ExpandAll => () => SetGroupsCollapsed(false),
                // The connection link's «Открыть каталог» (Shift+F12) and «Изменить каталог» (Shift+F11).
                ShortcutCommand.OpenFolder => OpenSelectedBaseFolder,
                ShortcutCommand.ChangeFolder => ChangeSelectedBaseFolder,
                ShortcutCommand.LaunchWithParameters => () => _ = LaunchWithParameters(false),
                ShortcutCommand.DesignerWithParameters => () => _ = LaunchWithParameters(true),
                ShortcutCommand.GoToFavorites => GoToFavorites,
                ShortcutCommand.PlatformFilter => ShowPlatformFilter,
                // The original's «Установить блокировку» (Ctrl+Alt+L) and «Разблокировать» (Ctrl+Alt+U) of «Свойства ▾».
                ShortcutCommand.SetLock => SetSelectedLock,
                ShortcutCommand.RemoveLock => RemoveSelectedLock,
                // The original's «Выгрузить резервную копию данных» (Shift+Ctrl+F5) and «Загрузить данные из резервной копии» (Shift+Ctrl+F7).
                ShortcutCommand.Backup => BackupSelected,
                ShortcutCommand.RestoreBackup => ShowBackupList,
                ShortcutCommand.BaseInfo => ShowBaseInfo,
                _ => null
            };
            if (action is not null) { e.Handled = true; action(); }
        }, RoutingStrategies.Tunnel);
        loadFailed = true;
        RebuildInterface();
        status.Text = "Загрузка списка…";
        AppLog.Info("library.load.begin");
        Opened += async (_, _) => { await ReloadAsync(initial: true); await ShowPasswordsNotice(); };
        Closed += (_, _) => { closed = true; infoGeneration++; };
        Closing += async (_, e) =>
        {
            if (writing) { e.Cancel = true; status.Text = "Дождитесь завершения записи."; return; }
            if (cacheBusy) { e.Cancel = true; status.Text = "Дождитесь окончания очистки временных файлов."; return; }
            if (profilePending)
            {
                e.Cancel = true;
                if (await ResolvePendingProfile(closing: true)) Close();
                return;
            }
            SaveView();
        };
    }

    private void RebuildInterface()
    {
        var query = search.Text; var selected = (bases.SelectedItem as LibraryRow)?.Key;
        var message = status.Text;
        typography = UiTypography.From(library.Appearance);
        UiColors.Apply(library.Appearance.Theme, library.Appearance.SelectionIntensity);
        commandButtons.Clear();
        launchButtons.Clear(); designerButtons.Clear();
        bases = new(); search = new(); executable = new() { Text = library.Executable };
        details = new(); status = new(); count = new(); connection = new();
        statusVersion = new(); statusSize = new(); statusClient = new(); statusTime = new();
        Title = $"БазоНавигатор · сборка {typeof(MainWindow).Assembly.GetName().Version}";
        FontSize = typography.InterfaceSize;
        FontFamily = typography.Family; Foreground = UiColors.Text;
        Background = UiColors.Surface;
        var root = new Grid { Margin = new Thickness(6, 0, 6, 0), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto") };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4 };
        // The original's «Сервис» arrow before the heading opens the service menu.
        var service = new Button { Content = UiIcons.Create("chevron-down", typography.IconSize), Padding = new Thickness(0), MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(service, MainCommands.Tip(MainCommand.ServiceMenu)); ToolTip.SetTip(service, MainCommands.Tip(MainCommand.ServiceMenu));
        service.Click += (_, _) => ShowServiceMenu(service);
        serviceButton = service;
        heading.Children.Add(service);
        var headingText = new TextBlock { Text = "Информационные базы: 1С:Предприятие 8", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(headingText, 1); heading.Children.Add(headingText);
        var settingsButton = ToolAction("settings", "Настройки программы", OpenSettings);
        Grid.SetColumn(settingsButton, 2); heading.Children.Add(settingsButton);
        root.Children.Add(heading);
        // Composition and order of the original's toolbar (smMainForm.dfm): search | add, group, delete | properties …
        // on the right: the list source | reload | expand, collapse. Favorites and history moved where the original has them.
        Border Separator() => new() { Width = 1, Height = typography.IconSize + 2, Background = UiColors.Line, Margin = new Thickness(5, 2) };
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 4) }; toolbarPanel = toolbar;
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        var sourceTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        search.Width = Math.Max(100, typography.InterfaceSize * 9); search.MinHeight = 26; search.Padding = new Thickness(4, 1);
        search.FontSize = typography.InterfaceSize;
        // As in the original: a magnifier inside the field; it opens the choice of the search type.
        var searchType = new Button { Padding = new Thickness(4, 0, 2, 0), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        AutomationProperties.SetName(searchType, "Вид поиска"); ToolTip.SetTip(searchType, "Вид поиска");
        searchType.Click += (_, _) => ShowSearchTypeMenu(searchType);
        search.InnerLeftContent = searchType;
        ApplySearchMode();
        var clearSearch = new Button { Content = UiIcons.Create("close", Math.Max(10, typography.IconSize - 6)), Padding = new Thickness(3, 0),
            MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        AutomationProperties.SetName(clearSearch, "Сбросить поиск"); ToolTip.SetTip(clearSearch, "Сбросить поиск");
        clearSearch.Click += (_, _) => { search.Text = ""; search.Focus(); };
        search.InnerRightContent = clearSearch;
        search.TextChanged += (_, _) => clearSearch.IsVisible = !string.IsNullOrEmpty(search.Text);
        tools.Children.Add(search);
        tools.Children.Add(Separator());
        addButton = CommandButton(MainCommand.AddBase, "add", async () => await AddBase());
        tools.Children.Add(addButton);
        tools.Children.Add(CommandButton(MainCommand.AddGroup, "folder-add", () => EditGroup(null)));
        tools.Children.Add(CommandButton(MainCommand.Delete, "delete", Delete));
        tools.Children.Add(Separator());
        var propertiesButton = CommandButton(MainCommand.Properties, "edit", EditSelected);
        // The original's «Свойства ▾» (pbActionIB): the arrow offers the actions on the base.
        var propertiesMore = new Button { Content = UiIcons.Create("chevron-down", Math.Max(10, typography.IconSize - 6)), Padding = new Thickness(0), MinHeight = 0,
            Width = Math.Max(10, typography.IconSize - 4), Height = propertiesButton.Height, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(propertiesMore, MainCommands.Tip(MainCommand.PropertiesMenu)); ToolTip.SetTip(propertiesMore, MainCommands.Tip(MainCommand.PropertiesMenu));
        propertiesMore.Click += (_, _) => ShowPropertiesMenu(propertiesMore);
        DimWhenDisabled(propertiesMore);
        commandButtons[propertiesMore] = MainCommand.PropertiesMenu;
        tools.Children.Add(propertiesButton); tools.Children.Add(propertiesMore);
        tools.Children.Add(Separator());
        cacheButton = CommandButton(MainCommand.ClearCache, "clear", ClearBaseCache);
        tools.Children.Add(cacheButton);
        tools.Children.Add(Separator());
        // The original's «8.x» text button: the filter by the platform branch (the installed versions are in «Сервис»).
        var versions = Action(platformFilter ?? "8.x", ShowPlatformFilter); platformFilterButton = versions;
        versions.Background = Brushes.Transparent; versions.BorderThickness = new Thickness(0); versions.Padding = new Thickness(4, 0);
        versions.MinHeight = 0; versions.Height = Math.Max(26, typography.IconSize + 10); versions.FontWeight = FontWeight.Bold;
        versions.Foreground = UiColors.SectionText; versions.FontSize = typography.InterfaceSize + 2; versions.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(versions, MainCommands.Caption(MainCommand.PlatformFilter)); ShowPlatformFilterState();
        commandButtons[versions] = MainCommand.PlatformFilter;
        tools.Children.Add(versions);
        // The original's «Снимки ▾» (tbsDropDown): the snapshots of the list, the arrow offers a new one. The shared list is chosen
        // in the settings, as in the original: the toolbar has no button for it (decision of the user 08.11.2026).
        var snapshotsButton = CommandButton(MainCommand.Snapshots, "snapshots", ShowSnapshots);
        var snapshotsMore = new Button { Content = UiIcons.Create("chevron-down", Math.Max(10, typography.IconSize - 6)), Padding = new Thickness(0), MinHeight = 0,
            Width = Math.Max(10, typography.IconSize - 4), Height = snapshotsButton.Height, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(snapshotsMore, MainCommands.Tip(MainCommand.SnapshotsMenu)); ToolTip.SetTip(snapshotsMore, MainCommands.Tip(MainCommand.SnapshotsMenu));
        snapshotsMore.Click += (_, _) => ShowSnapshotsMenu(snapshotsMore);
        DimWhenDisabled(snapshotsMore);
        commandButtons[snapshotsMore] = MainCommand.SnapshotsMenu;
        sourceTools.Children.Add(snapshotsButton); sourceTools.Children.Add(snapshotsMore);
        sourceTools.Children.Add(Separator());
        sourceTools.Children.Add(CommandButton(MainCommand.Reload, "refresh", Reload));
        sourceTools.Children.Add(Separator());
        sourceTools.Children.Add(CommandButton(MainCommand.ExpandAll, "expand-all", () => SetGroupsCollapsed(false)));
        sourceTools.Children.Add(CommandButton(MainCommand.CollapseAll, "collapse-all", () => SetGroupsCollapsed(true)));
        DockPanel.SetDock(sourceTools, Dock.Right); toolbar.Children.Add(sourceTools); toolbar.Children.Add(tools);
        Put(root, toolbar, 1);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        var list = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var headerShift = new TranslateTransform();
        var header = new Grid { Background = UiColors.Surface, Width = MinimumListWidth(), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = headerShift };
        listHeader = header; BuildHeader(); AttachHeaderMenu();
        var headerViewport = new Border { Child = header, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        list.Children.Add(headerViewport);
        bases.Background = UiColors.Field; bases.BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(bases, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(bases, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetAllowAutoHide(bases, false);
        AutomationProperties.SetName(bases, "Список информационных баз");
        bases.LayoutUpdated += (_, _) =>
        {
            var scroll = bases.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var viewport = scroll?.Viewport.Width ?? 0;
            if (viewport > 0)
            {
                var minimum = MinimumListWidth();
                var width = minimum <= viewport ? viewport : Math.Max(minimum, minimum - typography.NameWidth + typography.ReadableNameWidth);
                if (header.Width != width)
                {
                    header.Width = width;
                    foreach (var container in bases.GetRealizedContainers().OfType<ListBoxItem>())
                        if (container.Presenter?.Child is Grid row) row.Width = width;
                }
                headerViewport.Width = viewport;
                headerShift.X = -scroll!.Offset.X;
            }
        };
        bases.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, typography.RowHeight) }
        });
        bases.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, UiColors.Selection) }
        });
        bases.Styles.Add(new Style(s => s.OfType<TextBlock>()) { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.BaseText) } });
        bases.Styles.Add(new Style(s => s.OfType<TextBlock>().Class("group-label")) { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.Text) } });
        bases.Styles.Add(new Style(s => s.OfType<TextBlock>().Class("section-label")) { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.SectionText) } });
        bases.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<TextBlock>())
        { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.SelectedText) } });
        // «Отображать всплывающие подсказки» of the original's style panel: the rows of the list only, the buttons keep theirs.
        ToolTip.SetServiceEnabled(bases, library.Appearance.ListTooltips);
        bases.ItemTemplate = new FuncDataTemplate<LibraryRow>((item, _) =>
        {
            if (item is null) return new TextBlock();
            var entry = item.Entry;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout()), Height = typography.RowHeight };
            row.Width = header.Width;
            var nameGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*"), Margin = new Thickness(5 + item.Depth * 14, 0, 4, 0), ColumnSpacing = 6 };
            if (item.Orphan is { } orphan) return OrphanRow(row, nameGrid, item, orphan);
            if (entry is null)
            {
                var icon = item.Section == LibrarySection.Favorites ? "favorite-folder" : item.Section == LibrarySection.All ? "folder"
                    : item.Section == LibrarySection.Orphans ? "missing-folder"
                    : collapsed.Contains(item.Group) && string.IsNullOrWhiteSpace(search.Text) ? "chevron-right" : "chevron-down";
                Control glyph = UiIcons.Create(icon);
                // As in the original, sections and groups show a folder next to the expander.
                {
                    var closed = item.Section != LibrarySection.None ? collapsedSections.Contains(item.Section) && string.IsNullOrWhiteSpace(search.Text)
                        : collapsed.Contains(item.Group) && string.IsNullOrWhiteSpace(search.Text);
                    var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                    icons.Children.Add(item.Section == LibrarySection.None
                        ? UiIcons.Create(closed ? "tree-plus" : "tree-minus")
                        : new Viewbox { Width = 8, Height = 12, Child = UiIcons.Create(closed ? "chevron-right" : "chevron-down") });
                    icons.Children.Add(item.Section == LibrarySection.None ? UiIcons.Create("folder") : glyph); glyph = icons;
                }
                var toggle = new Button { Content = glyph, Width = 36, Height = 20, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(toggle, "Свернуть/развернуть " + item.Name);
                toggle.Click += (_, _) => ToggleRow(item);
                nameGrid.Children.Add(toggle);
            }
            else if (!item.FavoriteShortcut && IsFavorite(entry)) nameGrid.Children.Add(UiIcons.Create("star"));
            // As the original: a base that is not found has a question mark, a server base the letters SQL.
            else if (information.GetValueOrDefault(entry.Id) is { BaseMissing: true }) nameGrid.Children.Add(UiIcons.Create("missing-base"));
            else if (entry.Kind == ConnectionKind.Server) nameGrid.Children.Add(UiIcons.Create("server-base"));
            else nameGrid.Children.Add(new Border { Width = 13, Height = 8, CornerRadius = new CornerRadius(1), Background = Brush.Parse("#4C85B9"), VerticalAlignment = VerticalAlignment.Center });
            // As the original, a base of a common list has its own mark: here a hollow frame around the sign of the base.
            var common = entry is null ? null : CommonOf(entry);
            if (common is not null)
                nameGrid.Children.Add(new Border { Width = 22, Height = 16, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
                    BorderBrush = UiColors.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { FontSize = typography.ListSize, FontFamily = typography.ListFamily, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var counter = entry is null && item.Section == LibrarySection.None ? item.Name.LastIndexOf("  (", StringComparison.Ordinal) : -1;
            if (counter > 0)
            {
                // The group's base count is muted, like in the original.
                label.Inlines = [new Run(item.Name[..counter]), new Run(item.Name[counter..]) { Foreground = UiColors.Muted }];
            }
            // As the original (_Unit207.pas:45478): a base with a release shows « [релиз]» after its name, in the same font and colour.
            // Only the shown text: the search, the sort and the copied name use the name itself.
            else label.Text = entry is null ? item.Name : entry.ListName;
            if (entry is null) label.Classes.Add("group-label");
            else if (information.GetValueOrDefault(entry.Id) is { BaseMissing: true }) { label.Foreground = UiColors.Error; label.FontStyle = FontStyle.Italic; }
            if (item.Section != LibrarySection.None) { label.Classes.Add("section-label"); label.FontWeight = FontWeight.Bold; }
            AutomationProperties.SetAutomationId(row, item.Key);
            // The release too (ListName): a large list font may cut it with the ellipsis (Grok audit 14.10).
            ToolTip.SetTip(label, entry is null ? item.Name : $"{entry.ListName}\n{entry.Location}" +
                (common is null ? "" : $"\nОбщий список: {common.ListPath} (только запуск и просмотр)"));
            Grid.SetColumn(label, 1); nameGrid.Children.Add(label);
            row.Children.Add(nameGrid);
            if (entry is null && item.Section == LibrarySection.Orphans)
            {
                // As the original: the section shows the total size of the cache it lists.
                Grid.SetColumnSpan(nameGrid, 3);
                var total = Cell(FormatSize(orphans.Sum(x => x.Size)), 3); total.FontWeight = FontWeight.Bold; total.Classes.Add("section-label");
                total.IsVisible = columns.Visible(ListColumn.Size); row.Children.Add(total); return row;
            }
            if (entry is null) { Grid.SetColumnSpan(nameGrid, 4); return row; }
            information.TryGetValue(entry.Id, out var data);
            var platform = Cell(data?.Platform ?? "…", 1);
            platform.TextAlignment = TextAlignment.Right;
            var platformCell = new Grid { ColumnDefinitions = new("*,Auto") };
            Grid.SetColumn(platformCell, 1); Grid.SetColumn(platform, 0); platformCell.Children.Add(platform);
            var architecture = new TextBlock { Text = data is { ArchitectureLabel.Length: > 0 } ? "(" + data.ArchitectureLabel + ")" : "",
                Width = Math.Ceiling(UiTypography.Measure("(ARM64)", typography.ListFamily, typography.ListSize).Width) + 8,
                FontFamily = typography.ListFamily, FontSize = typography.ListSize, Foreground = UiColors.Muted,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(architecture, 1); platformCell.Children.Add(architecture);
            if (data is not null)
            {
                if (data.PlatformMissing) { platform.Foreground = architecture.Foreground = UiColors.Error; platform.TextDecorations = TextDecorations.Strikethrough; }
            }
            ToolTip.SetTip(platformCell, data?.Note ?? "Определение клиента…");
            ToolTip.SetTip(platform, data?.Note ?? "Определение клиента…");
            platformCell.IsVisible = columns.Visible(ListColumn.Platform); row.Children.Add(platformCell);
            var dateCell = Cell(data?.Modified?.ToString("dd.MM.yy") ?? "—", 2); dateCell.IsVisible = columns.Visible(ListColumn.Date); row.Children.Add(dateCell);
            var sizeCell = Cell(data?.Size is { } size ? FormatSize(size) : "—", 3); sizeCell.IsVisible = columns.Visible(ListColumn.Size); row.Children.Add(sizeCell);
            if (!string.IsNullOrEmpty(data?.Note)) ToolTip.SetTip(row, data.Note);
            return row;
        });
        Put(list, bases, 1);
        var listFrame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Child = list };
        body.Children.Add(listFrame);
        var buttons = new StackPanel { Spacing = 14, MinWidth = UiTypography.Measure("1С:Предприятие", typography.Family, typography.InterfaceSize).Width + 48 };
        buttons.Children.Add(LaunchWithMenu(LaunchAction("1С:Предприятие", "play", () => Launch(false)), designer: false));
        buttons.Children.Add(LaunchWithMenu(LaunchAction("Конфигуратор", "designer", () => Launch(true)), designer: true));
        if (!library.HideStarterButton) buttons.Children.Add(StarterButton());
        // As in the original: launch buttons on top, the session client at the bottom; scrolls on a short window.
        var right = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(buttons, Dock.Top); right.Children.Add(buttons);
        var sessionGroup = SessionGroup(); DockPanel.SetDock(sessionGroup, Dock.Bottom); right.Children.Add(sessionGroup);
        var rightScroll = new ScrollViewer { Content = right, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(rightScroll, 1); body.Children.Add(rightScroll);
        rightScroll.SizeChanged += (_, e) => FitSessionGroup(e.NewSize.Height, buttons.DesiredSize.Height);
        Put(root, body, 2);
        var info = new StackPanel { Spacing = 6, Margin = new Thickness(5, 5, 5, 0) };
        details.Foreground = UiColors.SectionText; details.FontWeight = FontWeight.SemiBold; details.TextWrapping = TextWrapping.NoWrap; details.TextTrimming = TextTrimming.CharacterEllipsis;
        connection.Foreground = UiColors.Muted; connection.FontSize = typography.InterfaceSize;
        info.Children.Add(details); info.Children.Add(connection);
        // As in the original: the favorite star at the right of the selected base's name and connection.
        var infoRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var infoTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        Button? copy = null;
        copy = ToolAction("copy", "Копировать", () => ShowCopyMenu(copy!));
        infoTools.Children.Add(copy);
        // As the original's star under the list: «Перейти к Избранному» (Ctrl+J); adding and removing stay F8, Ctrl+D and the menu.
        infoTools.Children.Add(CommandButton(MainCommand.GoToFavorites, "star", GoToFavorites));
        Grid.SetColumn(infoTools, 1);
        infoRow.Children.Add(info); infoRow.Children.Add(infoTools); Put(root, infoRow, 3);
        AttachConnectionMenu();
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 3, 0, 3) };
        var section = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(7, 0) };
        section.Children.Add(UiIcons.Create("database", typography.IconSize)); section.Children.Add(new TextBlock { Text = "1С:Предприятие 8", FontSize = typography.InterfaceSize, VerticalAlignment = VerticalAlignment.Center });
        // The original's bottom tabs: the list itself and the launch history.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tabs.Children.Add(new Border { Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Child = section });
        var historyTab = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(7, 0), MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        var historyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        historyContent.Children.Add(UiIcons.Create("history", typography.IconSize));
        historyContent.Children.Add(new TextBlock { Text = "История", FontSize = typography.InterfaceSize, VerticalAlignment = VerticalAlignment.Center });
        historyTab.Content = historyContent; historyTab.Click += (_, _) => OpenHistory();
        AutomationProperties.SetName(historyTab, "История запусков"); ToolTip.SetTip(historyTab, "История запусков");
        tabs.Children.Add(historyTab);
        bottom.Children.Add(tabs);
        var close = LaunchAction("Выход", "close", Close); Grid.SetColumn(close, 2); bottom.Children.Add(close); Put(root, bottom, 4);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
        status.VerticalAlignment = VerticalAlignment.Center; status.FontSize = typography.InterfaceSize; status.Foreground = UiColors.Muted;
        status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; status.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(status);
        // As the original StatusBar1: centered panels divided by lines; widths follow the interface font.
        double Fit(string sample) => Math.Ceiling(UiTypography.Measure(sample, typography.Family, typography.InterfaceSize).Width) + 16;
        var panels = new List<Border>();
        void Panel(Control content, int column, double width, string tip)
        {
            var cell = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 0, 0, 0), MinWidth = width, Padding = new Thickness(6, 0), Child = content };
            ToolTip.SetTip(cell, tip); Grid.SetColumn(cell, column); footer.Children.Add(cell); panels.Add(cell);
        }
        foreach (var text in new[] { statusVersion, statusSize, statusClient, statusTime, count })
        { text.FontSize = typography.InterfaceSize; text.Foreground = UiColors.Muted; text.VerticalAlignment = VerticalAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Center; }
        Panel(statusVersion, 1, Fit("8.3.27.2214"), "Платформа выбранной базы");
        Panel(statusSize, 2, Fit("999,99 МБ"), "Размер файла выбранной базы");
        Panel(statusClient, 3, Fit("ThickClient"), "Клиент из списка баз (App=); Auto — выбор по настройке базы");
        var time = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        time.Children.Add(UiIcons.Create("clock", Math.Max(14, typography.IconSize - 2))); time.Children.Add(statusTime);
        Panel(time, 4, Fit("00,000 с") + 18, "Время загрузки списка, с");
        Panel(count, 5, Fit("Базы: 999 / 999"), "Показано баз / всего в списке");
        // A narrow window keeps the message readable: the size, the client and the platform give way in this order.
        var messageWidth = Fit(new string('ш', 16));
        footer.SizeChanged += (_, e) =>
        {
            foreach (var panel in panels) panel.IsVisible = true;
            var free = e.NewSize.Width - panels.Sum(x => x.MinWidth + 12);
            foreach (var panel in new[] { panels[1], panels[2], panels[0] })
                if (free < messageWidth) { panel.IsVisible = false; free += panel.MinWidth + 12; }
        };
        UpdateStatusPanels();
        // The original's StatusBar1 is 21 px high under its line; a larger interface font makes it taller.
        Put(root, new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer,
            MinHeight = Math.Max(22, Math.Ceiling(UiTypography.Measure("Йg", typography.Family, typography.InterfaceSize).Height) + 7) }, 5);
        Content = root;
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) ToolTip.SetTip(status, status.Text); };
        AttachUpdateNotice();
        search.TextChanged += (_, _) => BuildRows();
        bases.SelectionChanged += (_, _) => ShowDetails();
        AttachListContextMenu();
        bases.DoubleTapped += (_, e) =>
        {
            var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (row?.Content is LibraryRow { Entry: { } entry }) { DoubleClickEntry(entry); e.Handled = true; }
        };
        search.Text = query;
        RefreshCommands();
        BuildRows(selected); status.Text = message;
        RefreshShortHelp();
    }

    private Control SessionGroup()
    {
        var items = new StackPanel { Spacing = 0, Margin = new Thickness(8, 2, 8, 6) };
        sessionOptions.Clear();
        var values = Enum.GetValues<SessionClient>();
        var rowHeight = Math.Ceiling(UiTypography.Measure("Йр", typography.Family, typography.InterfaceSize).Height) + 6;
        // A window too low for the four options shows them as a drop-down list instead of cutting off the last ones.
        var choice = new ComboBox { ItemsSource = values.Select(Core.Launcher.SessionText).ToArray(), SelectedIndex = Array.IndexOf(values, session),
            FontSize = typography.InterfaceSize, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 4, 8, 8), IsVisible = false };
        AutomationProperties.SetName(choice, "Текущая сессия");
        choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) ChooseSession(values[choice.SelectedIndex]); };
        sessionChoice = choice; sessionItems = items;
        var titleHeight = Math.Ceiling(UiTypography.Measure("Йр", typography.Family, typography.InterfaceSize).Height);
        // Title with its margin, the options with theirs, the border and the gap above the group.
        sessionHeight = titleHeight + 4 + 8 + values.Length * rowHeight + 2 + 14;
        foreach (var value in values)
        {
            var option = new RadioButton { Content = Core.Launcher.SessionText(value), GroupName = "session", IsChecked = session == value,
                FontSize = typography.InterfaceSize, MinHeight = 0, Padding = new Thickness(Math.Round(Math.Max(4, typography.InterfaceSize / 3)), 0, 0, 0),
                Height = rowHeight };
            option.Classes.Add("compact");
            ToolTip.SetTip(option, value switch
            {
                SessionClient.Ordinary => "Толстый клиент в режиме обычного приложения (/RunModeOrdinaryApplication)",
                SessionClient.Thick => "Толстый клиент в режиме управляемого приложения (/RunModeManagedApplication)",
                SessionClient.Thin => "Тонкий клиент 1cv8c",
                _ => "1cv8s выбранной версии: клиент выбирает 1С по настройке базы. Вручную указанный файл клиента запускается как есть"
            });
            option.IsCheckedChanged += (_, _) => { if (option.IsChecked == true) ChooseSession(value); };
            items.Children.Add(option); sessionOptions.Add(option);
        }
        var title = new TextBlock { Text = "Текущая сессия", FontSize = typography.InterfaceSize, Foreground = UiColors.Muted, Margin = new Thickness(8, 4, 8, 0) };
        var panel = new StackPanel(); panel.Children.Add(title); panel.Children.Add(items); panel.Children.Add(choice);
        // Compact circles, as the original's radio group: Fluent draws 20 px circles in a 32 px row.
        // Fluent sets the template parts with Template priority; a selector with a class becomes a style trigger and wins over it.
        // Not above 20 px: the circle sits in a 20 px column of the template and a larger one is cut on the left.
        var circle = Math.Round(Math.Clamp(typography.InterfaceSize + 2, 12, 20));
        foreach (var (part, size) in new[] { ("OuterEllipse", circle), ("CheckOuterEllipse", circle), ("CheckGlyph", Math.Round(circle / 2.3)) })
            panel.Styles.Add(new Style(s => s.OfType<RadioButton>().Class("compact").Template().Name(part))
            { Setters = { new Setter(WidthProperty, size), new Setter(HeightProperty, size) } });
        panel.Styles.Add(new Style(s => s.OfType<RadioButton>().Class("compact").Template().OfType<Grid>())
        { Setters = { new Setter(HeightProperty, rowHeight) } });
        ToolTip.SetTip(title, "Клиент для ближайшего запуска 1С:Предприятия; после запуска выбор возвращается к «Авто». Конфигуратор всегда запускается толстым клиентом.");
        return new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Child = panel, Margin = new Thickness(0, 14, 0, 0) };
    }

    // The options and the drop-down list show the same choice; a change by either counts once.
    private void ChooseSession(SessionClient value)
    {
        if (session != value) { session = value; sessionRevision++; }
        var index = Array.IndexOf(Enum.GetValues<SessionClient>(), value);
        if (index < sessionOptions.Count) sessionOptions[index].IsChecked = true;
        if (sessionChoice is not null) sessionChoice.SelectedIndex = index;
    }

    private void FitSessionGroup(double available, double buttons)
    {
        if (sessionItems is null || sessionChoice is null) return;
        var compact = buttons + sessionHeight > available;
        sessionItems.IsVisible = !compact; sessionChoice.IsVisible = compact;
    }

    // A toolbar button of the table of commands: the hint with the key, on or off by the command's rule (RefreshCommands).
    private Button CommandButton(MainCommand command, string icon, System.Action action)
    {
        var button = ToolAction(icon, MainCommands.Tip(command), action);
        commandButtons[button] = command;
        return button;
    }
    private Button ToolAction(string icon, string label, System.Action action)
    {
        var button = Action(label, action);
        button.Content = UiIcons.Create(icon, typography.IconSize); button.Width = button.Height = Math.Max(26, typography.IconSize + 10); button.MinHeight = 0;
        button.Padding = new Thickness(3); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        DimWhenDisabled(button);
        ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label); return button;
    }
    // A disabled icon is dimmed rather than boxed: the theme's grey box kept the colours and read as pressed.
    private static void DimWhenDisabled(Button button)
    {
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        button.PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) button.Opacity = button.IsEnabled ? 1 : .35; };
    }

    // As the original: a narrow ▾ button beside each launch button opens its launch variants on both platforms.
    private Control LaunchWithMenu(Button launch, bool designer)
    {
        launchButtons.Add(launch);
        if (designer) designerButtons.Add(launch);
        var panel = new Grid { ColumnDefinitions = new("*,Auto") };
        panel.Children.Add(launch);
        var more = Action("", () => { });
        more.Content = UiIcons.Create("chevron-down", Math.Max(12, typography.IconSize - 4)); more.Padding = new Thickness(2, 0);
        more.MinHeight = launch.MinHeight; more.VerticalAlignment = VerticalAlignment.Stretch; more.BorderThickness = new Thickness(0, 1, 1, 1);
        var label = designer ? "Варианты запуска конфигуратора" : "Варианты запуска 1С:Предприятия";
        AutomationProperties.SetName(more, label); ToolTip.SetTip(more, label);
        more.Click += (_, _) => ShowLaunchMenu(more, designer);
        launchButtons.Add(more); if (designer) designerButtons.Add(more);
        Grid.SetColumn(more, 1); panel.Children.Add(more);
        return panel;
    }

    private void ShowLaunchMenu(Control anchor, bool designer)
    {
        var elevated = Core.Launcher.IsElevated();
        var menu = ClassicMenu("Варианты запуска");
        var admin = new MenuItem { Header = MenuHeader("Запустить от имени администратора"), Classes = { "scaled" }, IsEnabled = !elevated };
        AutomationProperties.SetName(admin, "Запустить от имени администратора");
        if (elevated) ToolTip.SetTip(admin, "БазоНавигатор уже запущен от имени администратора: обычный запуск тоже выполняется с этими правами.");
        admin.Click += (_, _) => { if (Selected() is { } entry) LaunchEntry(entry, designer, elevated: true); };
        var items = new List<Control>();
        // As the original's actV8EnterpriseWithLogin / actV8ConfigWithLogin: /WA- and the 1C login window.
        // The original's order: «Запустить с выбором параметров» (Ctrl+F3/F4), «Запустить с аутентификацией», «Запустить от имени администратора».
        // The caption, key and availability of the table (a configurator has none for a web base); a click checks it again.
        var state = CurrentState();
        items.Add(CommandItem(designer ? MainCommand.DesignerWithParameters : MainCommand.LaunchWithParameters, state,
            () => _ = LaunchWithParameters(designer)));
        var login = CommandItem(designer ? MainCommand.DesignerWithLogin : MainCommand.LaunchWithLogin, state, () => LaunchWithLogin(designer));
        ToolTip.SetTip(login, "1С запросит имя пользователя и пароль в своём окне входа. БазоНавигатор пароль не получает и не хранит.");
        items.Add(login);
        if (OperatingSystem.IsWindows()) items.Add(admin);
        // As the original's mItemWebOpen: shown for a base on a web server only.
        var web = (bases.SelectedItem as LibraryRow)?.Entry is { Kind: ConnectionKind.Web };
        if (!designer && web)
        {
            if (items.Count > 0) items.Add(new Separator());
            var browser = new MenuItem { Header = MenuHeader("Открыть в web-браузере"), Classes = { "scaled" }, IsEnabled = web };
            AutomationProperties.SetName(browser, "Открыть в web-браузере");
            if (!web) ToolTip.SetTip(browser, "Доступно для базы на веб-сервере.");
            browser.Click += (_, _) => OpenInBrowser();
            items.Add(browser);
        }
        menu.ItemsSource = items;
        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    // The general setting or the base's own one (its properties, «Прочие свойства»).
    private async void DoubleClickEntry(BaseEntry entry)
    {
        switch (entry.DoubleClickFor(library.DoubleClick))
        {
            case BaseDoubleClick.Designer: LaunchEntry(entry, true); break;
            case BaseDoubleClick.EnterpriseLogin: LaunchEntry(entry, false, options: LaunchOptions.Login); break;
            case BaseDoubleClick.EnterpriseParameters: await LaunchWithParameters(false, entry); break;
            case BaseDoubleClick.Properties: if (!reading && !loadFailed && !closed) await Edit(entry); break;
            default: LaunchEntry(entry, false); break;
        }
    }

    private void LaunchWithLogin(bool designer)
    {
        if (Selected() is not { } entry) return;
        if (designer && entry.Kind == ConnectionKind.Web) { status.Text = MainCommands.WebDesignerText; return; }
        LaunchEntry(entry, designer, options: LaunchOptions.Login);
    }

    private async Task LaunchWithParameters(bool designer, BaseEntry? clicked = null)
    {
        if (loadFailed || reading || closed || (clicked ?? Selected()) is not { } entry) return;
        var source = ActivitySource;
        // The parameters as the start gets them: the branch's (Настройки платформы) before the base's own; kept once when unchanged.
        var dialog = new LaunchParametersWindow(entry with { AdditionalParameters = PlatformBranches.ForStart(library, entry).EffectiveParameters }, designer);
        typography.ApplyDialog(dialog);
        var options = await dialog.ShowDialog<LaunchOptions?>(this);
        if (options is null) { RestoreListFocus(); return; }
        // A reload while the modal dialog was open must not launch an old address under the new source.
        if (source != ActivitySource || !ShownBases().Any(x => x == entry))
        { status.Text = "Список изменился. Откройте параметры запуска заново."; RestoreListFocus(); return; }
        LaunchEntry(entry, designer, options: options);
    }

    private void OpenInBrowser()
    {
        if (Selected() is { } entry) LaunchEntry(entry, false, browser: true);
    }

    private Button LaunchAction(string label, string icon, System.Action action)
    {
        var button = Action(label, action);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions($"{typography.IconSize + 2},*"), ColumnSpacing = 4 };
        content.Children.Add(UiIcons.Create(icon, typography.IconSize));
        // Centered caption and a taller button, as on the original's launch panel.
        var text = new TextBlock { Text = label, FontSize = typography.InterfaceSize, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(text, 1); content.Children.Add(text); button.Content = content;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.MinHeight = UiTypography.Measure(label, typography.Family, typography.InterfaceSize).Height + 18;
        AutomationProperties.SetName(button, label); return button;
    }

    private string ColumnLayout() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"*,{(columns.Visible(ListColumn.Platform) ? typography.PlatformWidth : 0)},{(columns.Visible(ListColumn.Date) ? typography.DateWidth : 0)},{(columns.Visible(ListColumn.Size) ? typography.SizeWidth : 0)}");
    private double MinimumListWidth() => typography.NameWidth + (columns.Visible(ListColumn.Platform) ? typography.PlatformWidth : 0) +
        (columns.Visible(ListColumn.Date) ? typography.DateWidth : 0) + (columns.Visible(ListColumn.Size) ? typography.SizeWidth : 0);

    private static readonly IBrush SortGlyph = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly (ListColumn Column, string Title)[] ListTitles =
        [(ListColumn.Name, "Наименование"), (ListColumn.Platform, "Платформа"), (ListColumn.Date, "Дата"), (ListColumn.Size, "Размер")];

    // As the original's header: a click sorts by the column, a second click reverses; the arrow shows the order.
    private void BuildHeader()
    {
        listHeader.ColumnDefinitions = new ColumnDefinitions(ColumnLayout());
        listHeader.Children.Clear();
        for (var index = 0; index < ListTitles.Length; index++)
        {
            var (column, title) = ListTitles[index];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new TextBlock { Text = title, FontSize = typography.ColumnHeaderSize, FontFamily = typography.HeaderFamily,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            // As the original's sort glyph (traced from its header): a grey #9A9A9A triangle of rows 1, 3, 5, 7 and 9 px, drawn as
            // whole pixels so it stays sharp, about 10 px after the title.
            if (columns.Sort == column)
                content.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(columns.Descending
                    ? "M0,0 h9 v1 h-1 v1 h-1 v1 h-1 v1 h-1 v1 h-1 v-1 h-1 v-1 h-1 v-1 h-1 v-1 h-1 z"
                    : "M4,0 h1 v1 h1 v1 h1 v1 h1 v1 h1 v1 h-9 v-1 h1 v-1 h1 v-1 h1 v-1 h1 z"),
                    Fill = SortGlyph, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            var cell = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                Padding = new Thickness(5, 0), MinHeight = typography.HeaderHeight, Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = column == ListColumn.Size ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center, IsVisible = columns.Visible(column) };
            var name = $"{title}: сортировать" + (columns.Sort == column ? columns.Descending ? " (по убыванию)" : " (по возрастанию)" : "");
            AutomationProperties.SetName(cell, name); ToolTip.SetTip(cell, title + " · щелчок — сортировка, правая кнопка — колонки");
            cell.Click += (_, _) => ChangeColumns(columns.SortBy(column));
            Grid.SetColumn(cell, index); listHeader.Children.Add(cell);
        }
    }

    private void ChangeColumns(ListColumns next)
    {
        if (next == columns) return;
        columns = next; BuildHeader(); listHeader.Width = MinimumListWidth(); BuildRows();
    }

    private TextBlock Cell(string text, int column, double? fontSize = null)
    {
        var cell = new TextBlock { Text = text, FontSize = fontSize ?? typography.ListSize, FontFamily = fontSize is null ? typography.ListFamily : typography.HeaderFamily, MinHeight = fontSize is null ? 0 : typography.HeaderHeight, Margin = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = column == 3 ? TextAlignment.Right : TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(cell, text); Grid.SetColumn(cell, column); return cell;
    }

    private static void Put(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    private Button Action(string text, System.Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(6, 3), MinHeight = 26, Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => action();
        return button;
    }
    // An asynchronous action: an error it does not handle itself is shown, not left to end the program (Codex full audit 24.10, P2-7).
    private Button Action(string text, Func<Task> action)
    {
        Button button = null!;
        button = Action(text, () => SettingsWindow.Guarded(button, text, action));
        return button;
    }
    private BaseEntry? Selected()
    {
        var selected = (bases.SelectedItem as LibraryRow)?.Entry;
        if (selected is null) status.Text = MainCommands.SelectBaseText;
        return selected;
    }
    private void Refresh(Guid? select = null)
    {
        BuildRows(select?.ToString());
        _ = RefreshInformation(++infoGeneration, library, CommonShown().Select(x => x.Entry).ToList());
        _ = RefreshOrphans(library);
    }
    private static string FormatSize(long size) => SizeFormat.Short(size);
    private async Task RefreshInformation(int generation, Library snapshot, IReadOnlyList<BaseEntry> common)
    {
        await infoGate.WaitAsync();
        try
        {
            if (generation != infoGeneration) return;
            var values = await Task.Run(() =>
            {
                var result = new Dictionary<Guid, BaseInfo>();
                var platforms = PlatformCatalog.Scan(executables: snapshot.Bases.Select(x => x.Executable).Append(snapshot.Executable)).Installations;
                foreach (var entry in snapshot.Bases.Concat(common))
                {
                    if (generation != Volatile.Read(ref infoGeneration)) break;
                    result[entry.Id] = BaseInfo.Read(entry, snapshot, platforms);
                }
                return result;
            });
            if (generation != infoGeneration) return;
            information.Clear(); foreach (var pair in values) information[pair.Key] = pair.Value;
            BuildRows();
        }
        catch (Exception ex) { if (generation == infoGeneration) status.Text = "Сведения о базах недоступны: " + LegacyImport.ErrorMessage(ex); }
        finally { infoGate.Release(); }
    }
    private void BuildRows(string? selected = null)
    {
        var restoreFocus = bases.IsKeyboardFocusWithin;
        selected ??= (bases.SelectedItem as LibraryRow)?.Key;
        var query = search.Text?.Trim() ?? "";
        var shown = ShownBases().ToList();
        var matches = shown.Where(x => BaseSearch.Matches(x, query, searchMode) && PassesPlatformFilter(x)).ToList();
        var groups = Groups.All(library with { Bases = shown });
        if (columns is { Sort: ListColumn.Name, Descending: true }) groups = [.. groups.AsEnumerable().Reverse()];
        IEnumerable<BaseEntry> Ordered(IEnumerable<BaseEntry> entries) => columns.Order(entries, id => information.GetValueOrDefault(id));
        var rows = new List<LibraryRow>();
        // «Отображать группы сверху» off: as in the original, only under the sort by name the groups and bases of a level are
        // mixed by name; another sort keeps the groups on top.
        var mixed = !library.Appearance.GroupsOnTop && columns.Sort == ListColumn.Name;
        void Add(string parent, int depth)
        {
            var levelGroups = groups.Where(x => Groups.Parent(x).Equals(parent, StringComparison.OrdinalIgnoreCase)).ToList();
            var levelBases = Ordered(matches.Where(x => x.Group.Equals(parent, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var item in Groups.Level(levelGroups, levelBases, mixed, columns.Descending))
            {
                if (item.Entry is { } entry) { rows.Add(new(entry, parent, depth, entry.Name)); continue; }
                var group = item.Group!;
                var total = matches.Count(x => Groups.Contains(group, x.Group));
                if ((query.Length > 0 || platformFilter is not null) && total == 0) continue;
                rows.Add(new(null, group, depth, $"{group.Split('/')[^1]}  ({total})"));
                if (query.Length > 0 || !collapsed.Contains(group)) Add(group, depth + 1);
            }
        }
        rows.Add(new(null, "", 0, "ИЗБРАННОЕ", LibrarySection.Favorites));
        if (query.Length > 0 || !collapsedSections.Contains(LibrarySection.Favorites))
            foreach (var entry in Ordered(matches.Where(IsFavorite)))
                rows.Add(new(entry, entry.Group, 1, entry.Name, FavoriteShortcut: true));
        rows.Add(new(null, "", 0, $"ИНФОРМАЦИОННЫЕ БАЗЫ  [{shown.Count}]", LibrarySection.All));
        if (query.Length > 0 || !collapsedSections.Contains(LibrarySection.All)) Add("", 1);
        // As the original: the cache of configurations no known base uses, at the end; not while searching.
        if (orphans.Count > 0 && query.Length == 0)
        {
            rows.Add(new(null, "", 0, $"ОТСУТСТВУЮЩИЕ КОНФИГУРАЦИИ  [{orphans.Count}]", LibrarySection.Orphans));
            if (!collapsedSections.Contains(LibrarySection.Orphans))
                foreach (var orphan in orphans.OrderByDescending(x => x.Modified)) rows.Add(new(null, "", 1, orphan.Id.ToString("D"), Orphan: orphan));
        }
        bases.ItemsSource = rows;
        bases.SelectedItem = rows.FirstOrDefault(x => x.Key == selected) ?? rows.FirstOrDefault(x => x.Entry is not null) ?? rows.FirstOrDefault();
        if (restoreFocus) RestoreListFocus();
        count.Text = $"Базы: {matches.Count} / {shown.Count}";
        UpdateStatusPanels();
        ShowDetails();
    }
    // As the original's toolbar buttons: all groups at once; the sections stay open so the groups remain visible.
    private void SetGroupsCollapsed(bool collapse)
    {
        var selected = bases.SelectedItem as LibraryRow;
        collapsedSections.Remove(LibrarySection.All);
        collapsed.Clear();
        if (collapse) collapsed.UnionWith(Groups.All(library));
        var key = selected?.Key;
        // A base inside a collapsed group gives the selection to its top-level group.
        if (collapse && selected is { Section: LibrarySection.None, FavoriteShortcut: false } && selected.Group.Length > 0)
            key = "group:" + selected.Group.Split('/')[0];
        BuildRows(key);
    }
    private void ToggleRow(LibraryRow row)
    {
        if (row.Section != LibrarySection.None)
        { if (!collapsedSections.Add(row.Section)) collapsedSections.Remove(row.Section); }
        else if (!collapsed.Add(row.Group)) collapsed.Remove(row.Group);
        BuildRows(row.Key);
    }
    private void UpdateStatusPanels()
    {
        var entry = (bases.SelectedItem as LibraryRow)?.Entry;
        var data = entry is null ? null : information.GetValueOrDefault(entry.Id);
        statusVersion.Text = data is null ? "" : data.Release.Length > 0 ? data.Release : data.Platform;
        statusSize.Text = data?.Size is { } size ? FormatSize(size) : "";
        statusClient.Text = entry is null ? "" : entry.ClientType.Length > 0 ? entry.ClientType : "Auto";
        statusTime.Text = loadSeconds is { } seconds ? seconds.ToString("0.000", System.Globalization.CultureInfo.CurrentCulture) + " с" : "";
    }
    private void ShowDetails()
    {
        UpdateStatusPanels();
        RefreshCommands();
        var selected = bases.SelectedItem as LibraryRow;
        var item = selected?.Entry;
        // The link of a file base opens its folder, as the original's lbConnect.
        var folderLink = selected is { Orphan: null } && item is { Kind: ConnectionKind.File };
        connection.Cursor = folderLink ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        foreach (var button in launchButtons) button.IsEnabled = item is not null && !loadFailed && !reading;
        // The configurator does not connect through a web server.
        if (item is { Kind: ConnectionKind.Web }) foreach (var button in designerButtons) button.IsEnabled = false;
        if (selected is { Orphan: { } cache })
        {
            details.Text = "Кэш отсутствующей конфигурации · " + FormatSize(cache.Size);
            connection.Text = "Ни одна известная база не использует этот ID · Del — удалить кэш · " + string.Join(" · ", cache.Folders);
            ToolTip.SetTip(connection, string.Join("\n", cache.Folders)); return;
        }
        if (selected is { Section: LibrarySection.Orphans })
        {
            details.Text = selected.Name;
            connection.Text = "Каталоги кэша 1С с ID, которого нет ни в одном известном списке баз · правая кнопка — удалить весь";
            ToolTip.SetTip(connection, connection.Text); return;
        }
        if (selected is { Section: not LibrarySection.None })
        {
            details.Text = !ShownBases().Any() ? "Пока нет баз. Нажмите «Добавить»."
                : !ShownBases().Any(x => BaseSearch.Matches(x, search.Text, searchMode) && PassesPlatformFilter(x))
                    ? "Нет подходящих баз." : selected.Name;
            connection.Text = selected.Section == LibrarySection.Favorites
                ? "Избранные базы · Ctrl+D — добавить/убрать · Ctrl+J — перейти к разделу"
                : SourceDescription();
            ToolTip.SetTip(connection, connection.Text); return;
        }
        if (selected is { Entry: null })
        {
            // A group with a folder shows it, as a file base its own: a click or Shift+F12 opens it.
            details.Text = selected.Group;
            var groupFolder = Groups.Folder(library, selected.Group);
            connection.Cursor = groupFolder.Length > 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            connection.Text = groupFolder.Length > 0 ? groupFolder : "Группа · Alt+Enter — изменить · стрелка — свернуть/развернуть";
            ToolTip.SetTip(connection, groupFolder.Length > 0 ? groupFolder + "\nОткрыть каталог (Shift+F12)" : connection.Text);
            return;
        }
        details.Text = item is not null
            ? item.Name
            : !ShownBases().Any() ? "Пока нет баз. Нажмите «Добавить»."
            : ShownBases().Any(x => BaseSearch.Matches(x, search.Text, searchMode) && PassesPlatformFilter(x)) ? "Выберите базу." : "Нет подходящих баз.";
        connection.Text = item is null ? "" : BaseClipboard.ConnectionString(item);
        ToolTip.SetTip(connection, folderLink ? connection.Text + "\nОткрыть каталог (Shift+F12)" : connection.Text);
    }
    // The read-only and update notices go first: the status line trims the long path at its end (Codex P3-4, 18.10).
    private string SourceDescription() => (ReadOnlyList is { } reason ? $"Список только для чтения: {reason}; изменение отключено · " : "") +
        (updateProgressNotice is not null ? updateProgressNotice + " · " : "") +
        (updateNotice is not null ? updateNotice + " · " : "") +
        (sharedList?.MarkerNotice is { } marker ? marker + " · " : "") +
        (store.RelocationWarning is { } relocation ? relocation + " · " : "") +
        (sharedList is null ? "Локальный список · общий .v8i выбирается в настройках" : $"Общий список: {library.ListPath.Replace("\0", "\\0")} · неподдерживаемых записей: {sharedList.Warnings.Count}") +
        CommonDescription() +
        (listOverrideActive ? " · --list: только этот сеанс" : "") +
        (library.Appearance.LoadWarning is { } warning ? " · " + warning : "") +
        (recoveryNotice is not null ? " · " + recoveryNotice : "");
    private void Reload() => _ = ReloadAsync();
    private async Task ReloadAsync(bool initial = false)
    {
        if (reading || closed) return;
        if (!await ResolvePendingProfile() || closed) return;
        reading = true; loadFailed = true; infoGeneration++;
        DisableCommands();
        status.Text = "Загрузка списка…";
        var path = store.PathName;
        var currentList = sharedList is null ? "" : library.ListPath;
        Library? settingsSnapshot = null;
        V8iStore? refreshedList = null;
        string? defaultList = null;
        InterruptedWriteException? interrupted = null;
        var loadedCommon = CommonLoad.Empty;
        try
        {
            var refreshedStore = new LibraryStore(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var loaded = await Task.Run(() =>
            {
                var settings = settingsSnapshot = refreshedStore.Load();
                defaultList = settings.ListPath;
                var listPath = listOverrideActive && !initialListLoaded ? initialListPath ?? settings.ListPath
                    : initial ? settings.ListPath : currentList;
                refreshedList = listPath.Length == 0 ? null : new V8iStore(listPath);
                var result = refreshedList is not null ? refreshedList.Load(LibrarySources.Select(settings, path, listPath))
                    : string.IsNullOrEmpty(settings.ListPath) ? settings : LibrarySources.Select(settings, path, "");
                loadedCommon = LoadCommonLists(result, path);
                if (result.Moved is null || listOverrideActive) return result;
                // Moved portable folder: write the new paths once; a read-only folder keeps working in memory.
                try { result = ProfileRelocation.Complete(result, refreshedStore); AppLog.Info("profile.moved.saved"); }
                // A damaged history (InvalidDataException) must not block the list; the old anchor stays until it is repaired.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException) { AppLog.Warning("profile.moved.not_saved", ex); }
                return result;
            });
            if (closed) return;
            loadSeconds = watch.Elapsed.TotalSeconds;
            library = loaded;
            if (!initialListLoaded) _ = CheckUpdatesAsync(library.UpdateMode, library.UpdateRepository, library.KeepVersions);
            initialListLoaded = true;
            savedDefaultList = defaultList;
            AppLog.Configure(library);
            AppLog.Info("library.load.ready");
            if (refreshedList?.Warnings.Count > 0) AppLog.Warning("library.unsupported_records");
            if (library.Appearance.LoadWarning is not null) AppLog.Warning("appearance.fallback");
            store = refreshedStore; sharedList = refreshedList; commonLists = loadedCommon;
            if (commonLists.Lists.Any(x => x.Problem is not null)) AppLog.Warning("common_lists.not_loaded");
            executable.Text = library.Executable;
            loadFailed = false;
            RebuildInterface(); Refresh(); status.Text = SourceDescription();
            if (await LoadActivity() && !viewRestored) RestoreView();
        }
        catch (Exception ex)
        {
            AppLog.Error("library.load.failed", ex);
            interrupted = ex as InterruptedWriteException;
            if (closed) return;
            commonLists = CommonLoad.Empty;
            if (initial && settingsSnapshot is not null) { library = settingsSnapshot; sharedList = refreshedList; RebuildInterface(); }
            loadFailed = true; RefreshCommands();
            status.Text = $"Редактирование отключено. Список не загружен: {LegacyImport.ErrorMessage(ex)}";
        }
        finally
        {
            reading = false;
            if (!closed) ShowDetails();
        }
        if (interrupted is not null) await OfferInterruptedRecovery(interrupted, reload: true);
    }
    private void BeginWriting(string message)
    {
        reading = writing = true; loadFailed = true; infoGeneration++;
        DisableCommands();
        status.Text = message;
    }
    private void EndWriting(bool failedBefore)
    {
        reading = writing = false; loadFailed = failedBefore;
        if (closed) return;
        ShowDetails();
    }
    private string? ReadOnlyList => sharedList?.ReadOnly;
    // The state the commands of the toolbar, the menus and the keys depend on (MainCommands).
    private CommandState CurrentState()
    {
        var row = bases.SelectedItem as LibraryRow;
        var selection = row switch
        {
            null => SelectionKind.None,
            { Orphan: not null } => SelectionKind.Orphan,
            { Section: LibrarySection.Orphans } => SelectionKind.Orphans,
            { Section: not LibrarySection.None } => SelectionKind.None,
            { Entry: not null, FavoriteShortcut: true } => SelectionKind.FavoriteShortcut,
            { Entry: not null } => SelectionKind.Base,
            _ => SelectionKind.Group
        };
        var group = selection == SelectionKind.Group ? row!.Group : null;
        // A base of a common list and a group only such bases are in are started and viewed, never changed.
        var own = row?.Entry is { } entry ? CommonOf(entry) is null
            : group is not null && Groups.All(library).Contains(group, StringComparer.OrdinalIgnoreCase);
        return new(!loadFailed, reading, ReadOnlyList is not null, selection, own, row?.Entry?.Kind,
            group is not null && Groups.Folder(library, group).Length > 0, clearingOrphans);
    }
    // A key of an unavailable command: its reason goes to the status line as the action said it before (MainCommands.Refusal);
    // nothing opens. A key of the list says nothing without a base, as before. Returns whether a reason was said.
    private bool Refuse(MainCommand command, bool list)
    {
        var refusal = MainCommands.Refusal(command, CurrentState());
        if (refusal == CommandRefusal.None || list && refusal == CommandRefusal.SelectBase) return false;
        var row = bases.SelectedItem as LibraryRow;
        switch (refusal)
        {
            case CommandRefusal.ReadOnlyList: return ListLocked();
            case CommandRefusal.CommonBase: return CommonLocked(row?.Entry);
            case CommandRefusal.CommonGroup: return row is not null && CommonGroupLocked(row.Group);
            case CommandRefusal.GroupFolder: if (row is null) return false; status.Text = GroupWithoutFolder(row.Group); return true;
            default: status.Text = MainCommands.RefusalText(refusal); return true;
        }
    }
    private static string GroupWithoutFolder(string group) => $"У группы «{group}» не указан каталог (свойства группы, «Каталог:»).";
    private void RefreshCommands()
    {
        var state = CurrentState();
        foreach (var (button, command) in commandButtons)
        {
            button.IsEnabled = MainCommands.CanExecute(command, state);
            // The hint follows the row (Delete on a favorite shortcut); «8.x» shows its filter itself (ShowPlatformFilterState).
            if (command == MainCommand.PlatformFilter) continue;
            var tip = MainCommands.Tip(command, state);
            if (!Equals(ToolTip.GetTip(button), tip)) { ToolTip.SetTip(button, tip); AutomationProperties.SetName(button, tip); }
        }
    }
    // The list is being read or written: the commands follow the state, the launch buttons wait.
    private void DisableCommands()
    {
        RefreshCommands();
        foreach (var button in launchButtons) button.IsEnabled = false;
    }
    // A change of the list itself while it is read-only: the reason goes to the status line and nothing opens.
    // Properties stay available: the local settings of a base are saved, a change of the list is refused on saving.
    private bool ListLocked()
    {
        if (ReadOnlyList is not { } reason) return false;
        status.Text = new ListReadOnlyException(reason).Message; return true;
    }
    private Library ProfileToSave(Library value) => listOverrideActive && savedDefaultList is { } defaultList
        ? LibrarySources.Select(value, store.PathName, defaultList) : value;

    private async Task<bool> SavePendingProfile()
    {
        if (!profilePending) return true;
        if (reading || closed) return false;
        var failedBefore = loadFailed;
        BeginWriting("Повторная запись локальных настроек…");
        try
        {
            var pending = ProfileToSave(library);
            await Task.Run(() => store.Save(pending));
            profilePending = false;
            status.Text = "Локальные настройки сохранены.";
            AppLog.Info("profile.retry.saved");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("profile.retry.failed", ex);
            status.Text = "Локальные настройки ещё не записаны: " + LegacyImport.ErrorMessage(ex) +
                " Изменения остаются в памяти. Устраните причину и нажмите «Перечитать список баз» для повторной записи.";
            return false;
        }
        finally { EndWriting(failedBefore); }
    }
    private async Task<bool> Persist(Library next, string? renamedFrom = null, string? renamedTo = null, Guid? ensureId = null,
        IReadOnlyDictionary<Guid, Guid>? copies = null, IReadOnlyDictionary<string, string>? groupMap = null, IReadOnlyCollection<Guid>? required = null)
    {
        if (loadFailed || reading || closed) return false;
        // The list is taken before the automatic snapshot: nothing else may change or switch it while the snapshot is written.
        BeginWriting("Сохранение списка…");
        await AutoSnapshot();
        AppLog.Debug("library.save.begin");
        Library? sharedSaved = null;
        V8iStore? rebased = null;
        var conflict = false; var lost = false;
        try
        {
            var shared = sharedList; var settings = store;
            library = await Task.Run(() =>
            {
                var result = next;
                if (shared is not null)
                {
                    try { sharedSaved = result = shared.Save(result, renamedFrom, renamedTo, ensureId, copies, groupMap); }
                    // Another program changed the list since it was read: this change goes over theirs unless it touches the same records.
                    catch (ListChangedException)
                    {
                        AppLog.Info("library.save.external_change");
                        (rebased, result) = shared.SaveOverExternalChange(result, renamedFrom, renamedTo, ensureId, copies, groupMap, required);
                        sharedSaved = result;
                    }
                }
                settings.Save(ProfileToSave(result));
                return result;
            });
            if (rebased is not null) sharedList = rebased;
            profilePending = false;
            status.Text = (rebased is not null ? "Сохранено поверх изменений другой программы. " : "Сохранено. ") + SourceDescription();
            AppLog.Info("library.saved");
            return true;
        }
        catch (Exception ex)
        {
            if (rebased is not null) sharedList = rebased;
            conflict = ex is ListConflictException;
            AppLog.Error(sharedSaved is null ? "library.save.failed" : "profile.save.after_shared.failed", ex);
            if (sharedSaved is not null)
            {
                library = sharedSaved; profilePending = true; Refresh();
                status.Text = "Общий список сохранён, но локальные настройки не записаны: " + LegacyImport.ErrorMessage(ex) +
                    " Изменения остаются в памяти. Повторите сохранение или нажмите «Перечитать список баз» после устранения причины.";
            }
            else status.Text = $"Не удалось сохранить: {LegacyImport.ErrorMessage(ex)}";
            // Both the write and its rollback failed: the store no longer knows the file, editing waits for a new load,
            // which finds the marker and offers the recovery.
            lost = sharedList?.Lost == true;
            if (lost) status.Text += " Редактирование отключено; нажмите «Перечитать список баз».";
            // A marker of another window's unfinished write turned up while the changed list was read again:
            // the same offer as on loading, not only its reason.
            if (ex is InterruptedWriteException interrupted)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await OfferInterruptedRecovery(interrupted, reload: true));
            // The same records changed on both sides: the list is read again, so the next attempt starts from theirs.
            // A list that became read-only after it was loaded is read again too, so the buttons and the status line show it.
            // A refused write access on such a list is read again too: the new load decides whether it is read-only now.
            else if (conflict || ex is ListReadOnlyException or UnauthorizedAccessException && sharedSaved is null && ReadOnlyList is null)
            {
                var message = status.Text;
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await ReloadAsync();
                    // A failed reading keeps its own reason; the list is called read again only when it was.
                    if (!closed) status.Text = message + (loadFailed ? " " + status.Text : " Список перечитан.");
                });
            }
            return false;
        }
        finally { EndWriting(lost); }
    }
    private const string GoneMessage = "База удалена из списка другой программой. Закройте окно; при необходимости добавьте базу заново.";

    private BaseEntry? OnCurrent(BaseEntry original, BaseEntry edited) => BaseEdit.OnCurrent(library.Bases, original, edited);

    /// <summary>The original's «+»: the place and the platform, then the window that creates a base with the platform or connects an existing one.</summary>
    private async Task AddBase()
    {
        // One addition at a time: Insert or the button again while the panel or the window is open does nothing.
        if (adding) return;
        adding = true;
        try { await AddBaseCore(); }
        finally { adding = false; }
    }

    private async Task AddBaseCore()
    {
        if (ListLocked()) return;
        IReadOnlyList<PlatformInstallation> installations;
        try { installations = await Task.Run(() => PlatformCatalog.Scan().Installations); }
        catch (Exception ex) { AppLog.Warning("platform.scan.failed", ex); installations = []; }
        if (closed) return;
        // The platforms of the computer; a base is made by a thick client 1cv8 (the families that have one), 8.3 when none is found.
        var families = installations.Where(x => !x.Thin).Select(x => $"{x.Version.Major}.{x.Version.Minor}").Distinct().OrderByDescending(x => Version.Parse(x)).ToList();
        if (families.Count == 0) families.Add("8.3");
        // «Основная версия платформы» of the settings comes first when it is installed.
        if (families.Remove(library.MainVersion)) families.Insert(0, library.MainVersion);
        // The panel drops from the «+» button, whichever way the command came (the button, Insert, the context menu).
        Control anchor = addButton is { IsEffectivelyVisible: true } ? addButton : bases;
        if (await NewBasePlacement.Show(anchor, typography, families) is not { } place) { RestoreListFocus(); return; }
        // A base on a web server is only connected; the platform cannot create it.
        if (place.Kind == ConnectionKind.Web)
        { await Edit(null, new BaseEntry { Kind = ConnectionKind.Web, PlatformVersion = place.Family }); return; }
        var group = (bases.SelectedItem as LibraryRow)?.Group ?? "";
        if (await NewBaseWindow.Show(this, typography, library, place.Kind, place.Family, installations, group) is not { } result) { RestoreListFocus(); return; }
        if (!result.Created) { await Edit(null, result.Entry); return; }
        // The base exists on the disk now; the list is written after it. A refusal of the list leaves the base where it is and says so.
        // The list may have changed while the platform worked: the name is made unique against the list as it is now.
        var added = result.Entry;
        var taken = library.Bases.Select(x => x.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var n = 1; taken.Contains(added.Name); n++) added = added with { Name = result.Entry.Name + "_" + n };
        if (await Persist(library with { Bases = library.Bases.Append(added).ToList() }, ensureId: added.Id))
        {
            search.Text = ""; collapsed.Clear(); collapsedSections.Clear(); Refresh(added.Id);
            status.Text = $"Информационная база «{added.Name}» создана и добавлена в список: {added.Location}";
        }
        else status.Text = $"Информационная база создана ({added.Location}), но список не сохранён или сохранён не полностью: {status.Text} " +
            "Проверьте список; при необходимости подключите базу командой «Добавить» → «Подключить существующую информационную базу».";
        RestoreListFocus();
    }

    private async Task Edit(BaseEntry? entry, BaseEntry? preset = null, int page = 0)
    {
        if (entry is null && ListLocked()) return;
        var vault = new PasswordVault(store.PathName);
        // The passwords are those of this list: the same ID in another list has its own.
        var passwordSource = entry is null ? ActivitySource : SourceOf(entry);
        IReadOnlyDictionary<PasswordRole, string> saved = new Dictionary<PasswordRole, string>();
        string? warning = null;
        if (entry is not null)
        {
            try
            {
                var snapshot = library;
                var read = await Task.Run(() => vault.Read(snapshot, passwordSource, entry.Id));
                saved = read.Passwords;
                // The proxy password is shown only for the proxy it was saved for.
                if (saved.TryGetValue(PasswordRole.Proxy, out var proxy))
                {
                    var shown = saved.Where(x => x.Key != PasswordRole.Proxy).ToDictionary(x => x.Key, x => x.Value);
                    if (ProxyPassword.Unpack(entry, proxy) is { } open) shown[PasswordRole.Proxy] = open;
                    saved = shown;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { warning = LegacyImport.ErrorMessage(ex); AppLog.Warning("passwords.read.failed", ex); }
            if (closed) return;
        }
        // The scenarios of the page «Резервное копирование»; unreadable ones leave the base's choice as it is.
        BackupScenarioSet? backupScenarios = null;
        string? backupWarning = null;
        try { backupScenarios = await Task.Run(new BackupScenarioStore(store.PathName).Load); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            backupWarning = "Сценарии резервирования не прочитаны: " + LegacyImport.ErrorMessage(ex) + " Сценарий базы сохраняется без изменений.";
            AppLog.Warning("backup.scenarios.read_failed", ex);
        }
        if (closed) return;
        var commonAtOpen = CommonOf(entry);
        var dialog = new BaseDialog(entry, Groups.All(library), (bases.SelectedItem as LibraryRow)?.Group ?? "", saved, warning, async (result, passwords, lockFolder) =>
        {
            // A base of a common list: its record is never written; only its own «Дополнительные параметры» are kept in the settings
            // of the program (Codex audit 27.10, P2). Any other change is refused, so nothing typed is silently dropped.
            if (commonAtOpen is not null && entry is not null)
            {
                if (commonLists.Bases.FirstOrDefault(x => x.Entry.Id == entry.Id && x.ListPath == commonAtOpen.ListPath) is not { } common) return GoneMessage;
                if (passwords.Count > 0 || lockFolder is not null ||
                    result != (BaseEdit.AsDialogReturns(entry) with { AdditionalParameters = result.AdditionalParameters }))
                    return CommonReadOnly(common) + " Здесь сохраняются только «Дополнительные параметры»: верните остальные поля или закройте окно без сохранения.";
                var failed = await SaveCommonParameters(common, result.AdditionalParameters);
                if (failed is null) Refresh(entry.Id);
                return failed;
            }
            if (entry is not null)
            {
                // Compared with the base as the dialog returns it: a value the dialog only trimmed is not the user's change.
                var dialogAuthentication = result.Authentication;
                if (BaseEdit.OnCurrent(library.Bases, BaseEdit.AsDialogReturns(entry), result) is not { } current) return GoneMessage;
                // A password belongs to its user and repository: it is not written next to a user changed in another window meanwhile.
                if (passwords.Keys.Any(x => x != PasswordRole.Proxy) && !Equals(BaseEdit.AsDialogReturns(current).Authentication, dialogAuthentication))
                    return "Пользователь или хранилище базы изменены в другом окне. Свойства и пароли не сохранены: закройте окно и откройте свойства заново.";
                // So is the proxy password to its proxy server and user.
                if (passwords.ContainsKey(PasswordRole.Proxy) && !BaseEdit.SameProxy(current, result))
                    return "Прокси-сервер базы изменён в другом окне. Свойства и пароли не сохранены: закройте окно и откройте свойства заново.";
                result = current;
            }
            // A lock is written to the folder the page was read for: the connection changed in another window refuses the save.
            if (lockFolder is not null && (result.Kind != ConnectionKind.File || result.Location != lockFolder))
                return "Подключение базы изменено в другом окне. Свойства не сохранены, блокировка не записана: закройте окно и откройте свойства заново.";
            // The base first: a refused save changes no password. A vault refused after it is said as such; the dialog stays
            // open and «Сохранить» again writes the same base and retries the passwords.
            var items = library.Bases.Where(x => x.Id != result.Id).Append(result).ToList();
            var asked = result;
            // The proxy password is saved together with the proxy of the dialog.
            if (passwords.TryGetValue(PasswordRole.Proxy, out var typed) && typed is { Length: > 0 })
                passwords = passwords.ToDictionary(x => x.Key, x => x.Key == PasswordRole.Proxy ? ProxyPassword.Pack(asked, typed) : x.Value);
            if (!await Persist(library with { Bases = items }, ensureId: result.Id)) return status.Text ?? "Не удалось сохранить.";
            // Saving over another program's change keeps a proxy the user did not edit (Codex audit 14.10, P1): the proxy password
            // typed for the proxy of the dialog is not written next to another one. The other passwords are written.
            var proxyMoved = passwords.ContainsKey(PasswordRole.Proxy) &&
                (library.Bases.FirstOrDefault(x => x.Id == asked.Id) is not { } saved || !BaseEdit.SameProxy(saved, asked));
            if (proxyMoved) passwords = passwords.Where(x => x.Key != PasswordRole.Proxy).ToDictionary(x => x.Key, x => x.Value);
            if (passwords.Count > 0)
            {
                try { await Task.Run(() => vault.Write(passwordSource, result.Id, passwords)); AppLog.Info("passwords.saved"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    AppLog.Warning("passwords.save.failed", ex);
                    Refresh(result.Id);
                    return "Свойства базы сохранены, но пароли не записаны: " + LegacyImport.ErrorMessage(ex) + " Прежние пароли не изменены. Устраните причину и нажмите «Сохранить» ещё раз.";
                }
            }
            // Saving over another program's change keeps a connection the user did not edit: the lock is written only when the
            // base as saved is still the file base the page was read for.
            if (lockFolder is not null && library.Bases.FirstOrDefault(x => x.Id == result.Id) is var stored &&
                (stored is null || stored.Kind != ConnectionKind.File || stored.Location != lockFolder))
            {
                AppLog.Info("lock.skipped.connection_changed");
                Refresh(result.Id);
                return "Свойства базы сохранены, но её подключение изменено другой программой" + (stored is null ? "" : $" (теперь: {stored.Location})") +
                    ". Блокировка не записана: закройте окно и откройте свойства заново.";
            }
            if (proxyMoved)
            {
                AppLog.Info("passwords.proxy.skipped_proxy_changed");
                Refresh(result.Id);
                return "Свойства базы сохранены, но её прокси-сервер изменён другой программой. Пароль прокси не записан: закройте окно и откройте свойства заново.";
            }
            search.Text = ""; collapsed.Clear(); collapsedSections.Clear(); Refresh(result.Id); return null;
        }, (owner, heading, lines, action) => ConfirmDanger(heading, lines, action, owner), typography, preset,
            commonAtOpen is not null ? CommonReadOnly(commonAtOpen) + " Сохраняются только «Дополнительные параметры» — в настройках программы." : null,
            page, backupScenarios, backupWarning,
            sharedList is null ? ParametersPlace.Profile : ReadOnlyList is not null || CommonOf(entry) is not null ? ParametersPlace.ReadOnlyList : ParametersPlace.List,
            CommonLists.ListFileOf(commonAtOpen, sharedList is not null ? library.ListPath : null));
        typography.ApplyDialog(dialog);
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
    private void RestoreListFocus()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (closed || reading || bases.SelectedItem is not { } selected) return;
            bases.UpdateLayout();
            bases.ContainerFromItem(selected)?.Focus(NavigationMethod.Directional);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
    // The status line trims long text; a launch error is shown in full.
    private async Task ShowLaunchError(string message)
    {
        var dialog = new Window { Title = "Ошибка запуска", Width = 520, MinWidth = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var ok = Action("Закрыть", dialog.Close); ok.IsDefault = ok.IsCancel = true; buttons.Children.Add(ok);
        dialog.Content = DialogLayout.Frame(text, buttons); typography.ApplyDialog(dialog);
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
    private async void EditSelected()
    {
        if (reading || loadFailed || closed) return;
        if (bases.SelectedItem is not LibraryRow row) return;
        if (row.Entry is { } entry) await Edit(entry); else if (row is { Section: LibrarySection.None, Orphan: null }) EditGroup(row.Group);
    }
    private async void EditGroup(string? original)
    {
        // On a read-only list a group is not made or renamed, but its folder, a local setting, is still changed (Grok audit 13.10).
        var readOnly = ReadOnlyList is not null;
        if (readOnly && original is null) { ListLocked(); return; }
        if (original is not null && CommonGroupLocked(original)) return;
        var dialog = new Window { Title = original is null ? "Создание новой группы" : "Редактирование группы", Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 10 };
        var path = new TextBox { Text = original ?? "", PlaceholderText = "Введите название группы" };
        AutomationProperties.SetName(path, "Наименование группы");
        path.IsReadOnly = readOnly;
        panel.Children.Add(DialogLayout.Field("Наименование:", path));
        // The original's «Каталог:» (FormGroupProp.edtFolder): a folder for the group, opened by Shift+F12 and proposed for its new bases.
        var folder = new TextBox { Text = original is null ? "" : Groups.Folder(library, original), PlaceholderText = "Папка для ассоциации с группой" };
        AutomationProperties.SetName(folder, "Каталог группы");
        folder.InnerRightContent = SettingsWindow.Command("…", async () =>
        {
            try
            {
                var start = string.IsNullOrWhiteSpace(folder.Text) ? null : await StorageProvider.TryGetFolderFromPathAsync(folder.Text.Trim());
                var chosen = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог группы", AllowMultiple = false, SuggestedStartLocation = start });
                if (chosen.FirstOrDefault()?.TryGetLocalPath() is { } picked) folder.Text = Path.TrimEndingDirectorySeparator(picked);
            }
            catch (Exception ex) { status.Text = "Каталог не выбран: " + LegacyImport.ErrorMessage(ex); }
        });
        panel.Children.Add(DialogLayout.Field("Каталог:", folder));
        panel.Children.Add(DialogLayout.Label("Для вложенной группы укажите путь: Проекты/Клиент. Каталог открывается по Shift+F12 и предлагается для новых баз группы."));
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var saving = false;
        dialog.Closing += (_, e) => { if (saving) e.Cancel = true; };
        var save = Action("ОК", async () =>
        {
            if (saving) return;
            saving = true; panel.IsEnabled = buttons.IsEnabled = false;
            try
            {
                // After a conflict the list is read again; a group another program renamed or removed is not found by its old path.
                if (original is not null && !Groups.All(library).Contains(original, StringComparer.OrdinalIgnoreCase))
                { error.Text = $"Группа «{original}» изменена или удалена другой программой. Закройте окно и откройте группу заново."; return; }
                var target = Groups.Normalize(path.Text ?? "");
                if (readOnly && !string.Equals(original, target, StringComparison.Ordinal)) { ListLocked(); error.Text = status.Text; return; }
                // Only the folder changed: the list file is not touched, the group is not taken as renamed there.
                var renamed = original is null || !original.Equals(target, StringComparison.Ordinal);
                if (await Persist(Groups.WithFolder(Groups.Rename(library, original ?? "", target), target, folder.Text ?? ""),
                    renamed ? original : null, renamed ? target : null)) { saving = false; collapsed.Clear(); collapsedSections.Clear(); Refresh(); dialog.Close(); }
                else
                {
                    error.Text = status.Text;
                    if (profilePending) original = Groups.Normalize(path.Text ?? "");
                }
            }
            catch (ArgumentException ex) { error.Text = LegacyImport.ErrorMessage(ex); }
            finally { saving = false; panel.IsEnabled = buttons.IsEnabled = true; }
        });
        save.IsDefault = true;
        var cancel = Action("Отмена", dialog.Close); cancel.IsCancel = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        dialog.Content = DialogLayout.Frame(panel, buttons, error); typography.ApplyDialog(dialog);
        dialog.Opened += (_, _) => { path.Focus(); path.SelectAll(); };
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
    private async void Delete()
    {
        if (bases.SelectedItem is LibraryRow { Orphan: { } orphan }) { ClearOrphans([orphan]); return; }
        if (bases.SelectedItem is LibraryRow { Section: LibrarySection.Orphans }) { ClearOrphans(orphans); return; }
        if (loadFailed || bases.SelectedItem is LibraryRow { Section: not LibrarySection.None }) return;
        if (bases.SelectedItem is LibraryRow { FavoriteShortcut: true }) { RemoveFavorite(); return; }
        if (ListLocked()) return;
        if (bases.SelectedItem is LibraryRow { Entry: null } group)
        {
            if (CommonGroupLocked(group.Group)) return;
            if (CommonShown().Any(x => Groups.Contains(group.Group, x.Entry.Group)))
            { status.Text = $"В группе «{group.Group}» есть базы общих списков: удаляется только пустая группа."; return; }
            try { if (await Persist(Groups.RemoveEmpty(library, group.Group))) Refresh(); }
            catch (ArgumentException ex) { status.Text = LegacyImport.ErrorMessage(ex); }
            return;
        }
        if (Selected() is not { } entry || CommonLocked(entry)) return;
        if (entry.DisallowDelete) { status.Text = $"Удаление информационной базы «{entry.Name}» запрещено в её свойствах («Прочие свойства»)."; return; }
        var dialog = new Window { Title = "Удаление информационной базы", Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "Из списка будет удалена информационная база", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = entry.Name, FontSize = typography.DialogSize + 4, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Сама база и её файлы останутся на месте.", Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap });
        // As in the original: «Отмена» on the left, the action on the right.
        var buttons = new DockPanel();
        // Enter does not remove: «Отмена» is the default button and has the focus (UI/UX audit).
        var cancel = Action("Отмена", () => dialog.Close(false)); cancel.IsCancel = cancel.IsDefault = true;
        var remove = Action("Убрать", () => dialog.Close(true));
        DockPanel.SetDock(cancel, Dock.Left); DockPanel.SetDock(remove, Dock.Right);
        cancel.HorizontalAlignment = HorizontalAlignment.Left; remove.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(cancel); buttons.Children.Add(remove); dialog.Content = DialogLayout.Frame(panel, buttons);
        typography.ApplyDialog(dialog); dialog.Opened += (_, _) => cancel.Focus();
        var source = ActivitySource;
        if (await dialog.ShowDialog<bool>(this) && await Persist(library with { Bases = library.Bases.Where(x => x.Id != entry.Id).ToList() }))
        {
            Refresh();
            // The saved passwords of this list go with the base, the same ID in another list keeps its own; a failure leaves
            // them sealed in the vault and is only logged.
            var vault = new PasswordVault(store.PathName);
            try { await Task.Run(() => { if (File.Exists(vault.PathName)) vault.Remove(source, entry.Id); }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { AppLog.Warning("passwords.remove.failed", ex); }
        }
    }
    private void OpenSettings() => OpenPreferences("general");
    private void Launch(bool designer)
    {
        if (Selected() is not { } entry) return;
        // F4 and other paths to the configurator: the buttons are disabled, a key gets the reason in the status line.
        if (designer && entry.Kind == ConnectionKind.Web) { status.Text = MainCommands.WebDesignerText; return; }
        LaunchEntry(entry, designer);
    }
    // browser: the original's «Открыть в web-браузере»; it is recorded in the history like a start of 1С:Предприятие.
    private async void LaunchEntry(BaseEntry entry, bool designer, bool elevated = false, bool browser = false, LaunchOptions? options = null)
    {
        if (loadFailed || reading || closed) { if (!closed) status.Text = MainCommands.BusyText; return; }
        // «Перед запуском ИБ проверять версию Агента сервера»: the question comes before anything starts; the base may get a new version.
        if (!browser)
        {
            if (await CheckServerAgent(entry, designer, options) is not { } checkedEntry) return;
            entry = checkedEntry;
            if (loadFailed || reading || closed) { if (!closed) status.Text = MainCommands.BusyText; return; }
        }
        var snapshot = library; var source = SourceOf(entry); var persistent = PersistentIdentity(entry);
        var launched = false; var historySaved = false; var declined = false;
        BeginWriting("Запуск 1С…");
        AppLog.Info(designer ? "launch.designer.begin" : "launch.enterprise.begin");
        try
        {
            var client = session; var revision = sessionRevision;
            elevated = Core.Launcher.Elevated(entry, elevated || options?.Elevated == true);
            var branch = PlatformBranches.ForStart(snapshot, entry);
            var effective = options?.Apply(branch) ?? branch;
            var inBrowser = browser || options?.Platform is null && options?.AskLogin != true &&
                Core.Launcher.OpensInBrowser(effective, designer, client, elevated);
            var passwords = new PasswordVault(store.PathName).For(snapshot, source);
            await Task.Run(() => { if (browser) Core.Launcher.OpenInBrowser(entry); else Core.Launcher.Start(snapshot, entry, designer, client, elevated, options, passwords); });
            launched = true;
            // As in the original, the session choice covers one start of 1С:Предприятие, then returns to «Авто».
            // A choice made while this start was running is for the next start: it is kept. The browser item does not use it.
            if (!designer && !browser && revision == sessionRevision) ChooseSession(SessionClient.Auto);
            AppLog.Info(inBrowser ? "launch.browser.opened" : "launch.process.started");
            status.Text = inBrowser ? $"Открыто в браузере{(browser ? "" : " (App=WebClient)")}: {entry.Name} · {entry.Location}"
                : $"Процесс запущен: {entry.Name} · {(designer ? "Конфигуратор" : "Предприятие")}{(elevated ? " · от имени администратора" : "")}" +
                    (options?.AskLogin == true ? " · имя и пароль запросит 1С." : ".");
            try
            {
                activity = await Task.Run(() => activityStore.Record(source, entry, designer, persistent));
                historySaved = true;
            }
            catch (Exception ex) { AppLog.Warning("history.save.failed", ex); status.Text += " История не сохранена: " + LegacyImport.ErrorMessage(ex); }
        }
        catch (OperationCanceledException ex) { AppLog.Info("launch.elevation.declined"); declined = true; status.Text = ex.Message; }
        catch (Exception ex) { AppLog.Error("launch.failed", ex); status.Text = $"Ошибка запуска: {Core.Launcher.ErrorMessage(ex)}"; }
        finally { EndWriting(false); }
        if (!launched && !declined && !closed) await ShowLaunchError(status.Text ?? "");
        if (profilePending) status.Text += " Локальные настройки ещё не записаны; нажмите «Перечитать список баз» для повторной записи.";
        if (launched && historySaved && !profilePending)
        {
            if (snapshot.AfterLaunch == AfterLaunchAction.Minimize) WindowState = WindowState.Minimized;
            else if (snapshot.AfterLaunch == AfterLaunchAction.Close) { AppLog.Info("launch.action.close"); Close(); }
        }
    }
}

/// <summary>Where «Дополнительные параметры» of a base are saved: the profile (no list file), the record of a writable .v8i, or the
/// profile because the list is read-only (the attribute, no rights, a common list of 1CEStart).</summary>
internal enum ParametersPlace { Profile, List, ReadOnlyList }

internal sealed class BaseDialog : Window
{
    // The date field: its text box beside the calendar button. The space around the text (the button, paddings) is taken from the
    // laid out template, the text from a sample date in the dialog's font; never narrower than before.
    private static void FitDate(CalendarDatePicker date)
    {
        TextBox? box = null;
        date.TemplateApplied += (_, e) => box = e.NameScope.Find<TextBox>("PART_TextBox");
        void Fit(object? sender, EventArgs e)
        {
            if (box is not { Bounds.Width: > 0 } || date.Bounds.Width <= 0) return;
            date.LayoutUpdated -= Fit;
            var sample = Math.Max(UiTypography.Measure("00.00.0000", box.FontFamily, box.FontSize).Width,
                UiTypography.Measure(new DateTime(2000, 12, 28).ToString("d", CultureInfo.CurrentCulture), box.FontFamily, box.FontSize).Width);
            date.Width = TextFit.Fit(140, sample, date.Bounds.Width - box.Bounds.Width + box.Padding.Left + box.Padding.Right + 12);
        }
        date.LayoutUpdated += Fit;
    }

    /// <summary>The index of the «6. Блокировка» page.</summary>
    public const int LockPage = 5;

    /// <summary>App= of each item of «Режим», as the original writes it.</summary>
    public static readonly string[] ClientTypes = ["Auto", "ThinClient", "ThickClient", "ThickClient", "WebClient"];

    /// <summary>The item of «Режим» for App= of the list and the local «(УП)», as the original reads them.</summary>
    public static int ClientModeIndex(string clientType, bool managed) => clientType.Trim().ToLowerInvariant() switch
    {
        "thinclient" => 1, "thickclient" => managed ? 3 : 2, "webclient" => 4, _ => 0
    };

    /// <param name="savedPasswords">The passwords of the base from the vault; shown masked, written back only when changed.</param>
    /// <param name="saveEntry">Saves the base and the changed passwords (an empty one is removed); returns the error or null.</param>
    /// <param name="listFile">The .v8i the base belongs to, shown under the pages; null (the local list of the profile): no line.</param>
    public BaseDialog(BaseEntry? entry, List<string> groups, string selectedGroup, IReadOnlyDictionary<PasswordRole, string> savedPasswords,
        string? passwordWarning, Func<BaseEntry, IReadOnlyDictionary<PasswordRole, string?>, string?, Task<string?>> saveEntry,
        Func<Window, string, IEnumerable<string>, string, Task<bool>> confirm, UiTypography? typography = null, BaseEntry? preset = null,
        string? readOnly = null, int startPage = 0, BackupScenarioSet? backupScenarios = null, string? backupWarning = null,
        ParametersPlace parametersPlace = ParametersPlace.Profile, string? listFile = null)
    {
        var draftEntry = entry ?? new BaseEntry();
        // A new base may come with its connection (the original's «Подключить существующую информационную базу»).
        var initial = entry ?? preset;
        var saving = false;
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Title = entry is null ? "Добавление информационной базы" : "Свойства информационной базы";
        Width = 760; Height = 560; MinWidth = 380; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = new TextBox { Text = entry?.Name, PlaceholderText = "Например, Бухгалтерия" };
        // As the original's «Размещение информационной базы»: three switches, and each kind has its own fields (edtBaseDir;
        // edtServerCluster and edtServerBase; edtWebAddress), so switching the kind does not lose what was typed for another one.
        var initialKind = initial?.Kind ?? ConnectionKind.File;
        // The chosen kind is kept apart from the switches: when one is checked, the one before is still checked for a moment.
        var chosenKind = initialKind;
        var kindChanged = new List<Action>();
        RadioButton Place(string caption, ConnectionKind value)
        {
            var place = new RadioButton { Content = DialogLayout.Label(caption), GroupName = "connection-kind", IsChecked = initialKind == value };
            AutomationProperties.SetName(place, caption);
            place.IsCheckedChanged += (_, _) =>
            {
                if (place.IsChecked != true || chosenKind == value) return;
                chosenKind = value; foreach (var action in kindChanged) action();
            };
            return place;
        }
        var placeFile = Place("Компьютер или локальная сеть", ConnectionKind.File);
        var placeServer = Place("На сервере 1С:Предприятие", ConnectionKind.Server);
        var placeWeb = Place("На Web-сервере", ConnectionKind.Web);
        ConnectionKind Kind() => chosenKind;
        void OnKindChanged(Action action) => kindChanged.Add(action);
        var location = new TextBox { Text = initialKind == ConnectionKind.File ? initial?.Location : "", PlaceholderText = "Каталог существующей базы" };
        var (initialCluster, initialInfobase) = initialKind == ConnectionKind.Server ? BaseEdit.SplitServer(initial!.Location) : ("", "");
        var cluster = new TextBox { Text = initialCluster, PlaceholderText = "сервер или сервер:порт" };
        var serverBase = new TextBox { Text = initialInfobase, PlaceholderText = "имя базы" };
        var webAddress = new TextBox { Text = initialKind == ConnectionKind.Web ? initial?.Location : "", PlaceholderText = "https://сервер/имя_публикации" };
        AutomationProperties.SetName(name, "Название базы");
        AutomationProperties.SetName(location, "Каталог информационной базы");
        AutomationProperties.SetName(cluster, "Кластер серверов");
        AutomationProperties.SetName(serverBase, "Имя информационной базы в кластере");
        AutomationProperties.SetName(webAddress, "Строка адреса");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Error };
        var panel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        panel.Children.Add(DialogLayout.Field("Наименование", name));
        var connection = new StackPanel { Spacing = 10 };
        var places = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        var otherPlaces = new WrapPanel { Orientation = Orientation.Horizontal };
        placeServer.Margin = new Thickness(0, 0, 24, 0);
        otherPlaces.Children.Add(placeServer); otherPlaces.Children.Add(placeWeb);
        places.Children.Add(placeFile); places.Children.Add(otherPlaces);
        connection.Children.Add(places);
        // The original's PanelCaption: bold, in the colour of the section title (Grok audit 13.10).
        var kindHeading = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        connection.Children.Add(kindHeading);
        var filePanel = new StackPanel { Spacing = 6 };
        filePanel.Children.Add(DialogLayout.Label("Каталог информационной базы:")); filePanel.Children.Add(location);
        var serverPanel = new StackPanel { Spacing = 8 };
        serverPanel.Children.Add(DialogLayout.Field("Кластер серверов:", cluster));
        serverPanel.Children.Add(DialogLayout.Field("Имя инф. базы в кластере:", serverBase));
        // The original's chkCheckRAgent under the cluster fields: the agent of this computer is compared before every start. Windows only.
        var checkAgent = new CheckBox { Content = DialogLayout.Label("Перед запуском ИБ проверять версию Агента сервера 1С:Предприятия"),
            IsChecked = entry?.CheckServerAgent == true, IsVisible = OperatingSystem.IsWindows() };
        AutomationProperties.SetName(checkAgent, "Перед запуском ИБ проверять версию Агента сервера");
        ToolTip.SetTip(checkAgent, "Перед запуском версия клиента сравнивается с версией агента сервера (ragent) этого компьютера; при расхождении предлагается сменить версию.");
        serverPanel.Children.Add(checkAgent);
        var webPanel = new StackPanel { Spacing = 8 };
        webPanel.Children.Add(DialogLayout.Field("Строка адреса:", webAddress));
        connection.Children.Add(filePanel); connection.Children.Add(serverPanel); connection.Children.Add(webPanel);
        // The «…» of the original's edtBaseDir: the folder of the base.
        var browse = new Button { Content = "…", Padding = new Thickness(8, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(browse, "Выбрать каталог"); AutomationProperties.SetName(browse, "Выбрать каталог информационной базы");
        location.InnerRightContent = browse;
        // The folder glyph on the left of the original's edtBaseDir.
        var folderGlyph = typography is null ? UiIcons.Create("folder") : UiIcons.Create("folder", typography.IconSize); folderGlyph.Margin = new Thickness(6, 0, 0, 0); folderGlyph.VerticalAlignment = VerticalAlignment.Center;
        location.InnerLeftContent = folderGlyph;
        // The arrows move between the switches as in the original's group: the next one is checked and focused.
        var placesInOrder = new[] { placeFile, placeServer, placeWeb };
        foreach (var place in placesInOrder)
            place.KeyDown += (_, e) =>
            {
                var step = e.Key switch { Key.Down or Key.Right => 1, Key.Up or Key.Left => -1, _ => 0 };
                if (step == 0 || e.KeyModifiers != KeyModifiers.None) return;
                var next = placesInOrder[(Array.IndexOf(placesInOrder, place) + step + placesInOrder.Length) % placesInOrder.Length];
                next.IsChecked = true; next.Focus(); e.Handled = true;
            };
        // «Способ аутентификации» of the original's page: WSA= of the list, for a base on a web server (the thin client gets /WSA-).
        // The original shows «Запрашивать» for a record without WSA=; that alone changes nothing in the file or in the start.
        var webAuthenticationShown = entry?.WebServerAskCredentials ?? true;
        var webAuthentication = new ComboBox { ItemsSource = new[] { "Выбирать автоматически", "Запрашивать имя и пароль" },
            SelectedIndex = webAuthenticationShown ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(webAuthentication, "Способ аутентификации на веб-сервере");
        // A choice made by the user counts even when it is the one shown (a record without WSA= shows «Запрашивать»).
        var webAuthenticationChosen = false;
        webAuthentication.SelectionChanged += (_, _) => webAuthenticationChosen = true;
        var webAuthenticationField = DialogLayout.Field("Способ аутентификации:", webAuthentication);
        // «Скорость соединения» (ClientConnectionSpeed=, /O for the thin client) and «Использование Proxy» (UseProxy=, /NoProxy or
        // /Proxy) of the original's page, in its order. The proxy fields are enabled for «Явное указание настроек», as there.
        var speedValues = new[] { "", "Normal", "Low" };
        // A value of the list it does not know is shown as no choice (Grok audit 14.10): any choice then replaces the line.
        var connectionSpeed = new ComboBox { ItemsSource = new[] { "Выбирать при запуске", "Обычная", "Низкая" },
            SelectedIndex = entry is { ConnectionSpeed: null } ? -1 : Math.Max(0, Array.IndexOf(speedValues, entry?.ConnectionSpeed ?? "")),
            HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Не распознано в списке" };
        var speedChosen = false;
        connectionSpeed.SelectionChanged += (_, _) => speedChosen = true;
        AutomationProperties.SetName(connectionSpeed, "Скорость соединения");
        ToolTip.SetTip(connectionSpeed, "Скорость соединения с веб-сервером (ClientConnectionSpeed= в списке, ключ /O тонкого клиента).");
        // A record without UseProxy= passes nothing and 1C finds the proxy itself: it is shown as «Автоматическое определение».
        var proxyMode = new ComboBox { ItemsSource = new[] { "Не используется", "Автоматическое определение", "Явное указание настроек" },
            SelectedIndex = entry is { UseProxy: null, ListUseProxy.Length: > 0 } ? -1 : (int)(entry?.UseProxy ?? ProxyMode.Auto),
            HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Не распознано в списке" };
        AutomationProperties.SetName(proxyMode, "Использование прокси-сервера");
        var proxyChosen = false;
        proxyMode.SelectionChanged += (_, _) => proxyChosen = true;
        TextBox ProxyText(string value, string automation)
        { var box = new TextBox { Text = value }; AutomationProperties.SetName(box, automation); return box; }
        var proxyServer = ProxyText(entry?.ProxyServer ?? "", "Адрес прокси-сервера");
        var proxyPort = ProxyText(entry?.ProxyPort ?? "", "Порт прокси-сервера");
        var proxyUser = ProxyText(entry?.ProxyUser ?? "", "Пользователь прокси-сервера");
        var proxyFields = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 8, Margin = new Thickness(24, 0, 0, 0) };
        void ProxyCell(string caption, Control field, int row, int column)
        {
            var cell = DialogLayout.Field(caption, field); Grid.SetRow(cell, row); Grid.SetColumn(cell, column); proxyFields.Children.Add(cell);
        }
        // The keyboard goes as in the original's TabOrder: address, user, port, password.
        ProxyCell("Адрес:", proxyServer, 0, 0); ProxyCell("Пользователь:", proxyUser, 1, 0); ProxyCell("Порт:", proxyPort, 0, 1);
        void ShowProxy() => proxyFields.IsEnabled = proxyMode.SelectedIndex == (int)ProxyMode.Manual;
        ShowProxy(); proxyMode.SelectionChanged += (_, _) => ShowProxy();
        webPanel.Children.Add(DialogLayout.Field("Скорость соединения:", connectionSpeed));
        void ShowKind()
        {
            var shown = Kind();
            filePanel.IsVisible = shown == ConnectionKind.File; serverPanel.IsVisible = shown == ConnectionKind.Server; webPanel.IsVisible = shown == ConnectionKind.Web;
            kindHeading.Text = shown switch
            {
                ConnectionKind.File => "На данном компьютере или в локальной сети",
                ConnectionKind.Web => "На web-сервере",
                _ => "На сервере 1С:Предприятие"
            };
        }
        ShowKind(); OnKindChanged(ShowKind);
        browse.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог существующей базы", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) location.Text = path;
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
        };
        webPanel.Children.Add(webAuthenticationField);
        webPanel.Children.Add(DialogLayout.Field("Использование Proxy:", proxyMode));
        webPanel.Children.Add(proxyFields);
        // Only for a base being added: the properties of an existing base do not add anything.
        if (entry is null) connection.Children.Add(DialogLayout.Label("Добавляется подключение к существующей базе. Файлы базы не создаются и не перемещаются."));
        var details = new StackPanel { Spacing = 14 };
        var groupNames = new[] { "/" }.Concat(groups).ToList();
        var group = new ComboBox { ItemsSource = groupNames, SelectedItem = (entry?.Group ?? selectedGroup) is { Length: > 0 } current ? current : "/", HorizontalAlignment = HorizontalAlignment.Stretch };
        // A long group path ends with an ellipsis; the full path is in the tooltip.
        group.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) =>
        {
            var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis }; ToolTip.SetTip(text, value); return text;
        });
        ToolTip.SetTip(group, group.SelectedItem); group.SelectionChanged += (_, _) => ToolTip.SetTip(group, group.SelectedItem);
        details.Children.Add(DialogLayout.Field("Группа:", group));
        AutomationProperties.SetName(group, "Группа базы");
        var comment = new TextBox { Text = entry?.Comment ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55 };
        AutomationProperties.SetName(comment, "Комментарий базы");
        // The original's edtConfig and edtConfigVersion (smFormProperty.dfm, TabSheet1): the configuration is typed by hand until its
        // choice from v8updates.lst is made (decision of the user 14.10.2026); «Определить версию» is not shown yet. Both are local.
        var configuration = new TextBox { Text = entry?.Configuration ?? "", PlaceholderText = "введите название конфигурации" };
        AutomationProperties.SetName(configuration, "Конфигурация базы");
        ToolTip.SetTip(configuration, "Конфигурация, по которой необходимо проверять обновления");
        // As the original: a short field for the release, its form is not checked; wide enough for xx.xx.xxx.xxxx in the dialog's font.
        var release = new TextBox { Text = entry?.ConfigurationRelease ?? "", PlaceholderText = "xx.xx.xx.xx", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(release, "Релиз конфигурации");
        ToolTip.SetTip(release, "Текущий релиз конфигурации");
        release.AttachedToVisualTree += (_, _) => release.Width = Math.Ceiling(UiTypography.Measure("00.00.000.0000", release.FontFamily, release.FontSize).Width) +
            release.Padding.Left + release.Padding.Right + 12;
        var configurationSection = new StackPanel { Spacing = 8 };
        configurationSection.Children.Add(DialogLayout.Field("Комментарий:", comment));
        configurationSection.Children.Add(DialogLayout.Field("Конфигурация:", configuration));
        configurationSection.Children.Add(DialogLayout.Field("Релиз:", release));
        details.Children.Add(DialogLayout.Section("Сведения о конфигурации", configurationSection));
        var platformEditor = new PlatformEditor(this, entry?.Executable ?? "", entry?.PlatformRule, true, initial?.PlatformVersion ?? "", typography);
        // The original's «Режим» (cboApp): App= of the list; «(ОП)» and «(УП)» both are App=ThickClient, told apart locally.
        var clientModes = new[] { "Автоматический", "Тонкий клиент", "Толстый клиент (ОП)", "Толстый клиент (УП)", "Web-клиент" };
        var clientMode = new ComboBox { ItemsSource = clientModes, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = BaseDialog.ClientModeIndex(initial?.ClientType ?? "", initial?.ManagedApplication == true) };
        var initialClientMode = clientMode.SelectedIndex;
        AutomationProperties.SetName(clientMode, "Режим запуска информационной базы");
        ToolTip.SetTip(clientMode, "Режим запуска информационной базы (App= в списке). Толстый клиент: (ОП) — обычное приложение, (УП) — управляемое.");
        var platformSection = new StackPanel { Spacing = 8 };
        platformSection.Children.Add(platformEditor);
        platformSection.Children.Add(DialogLayout.Field("Режим:", clientMode));
        details.Children.Add(DialogLayout.Section("Используемая платформа", platformSection));
        var additional = new TextBox { Text = entry?.EffectiveParameters ?? "" };
        AutomationProperties.SetName(additional, "Дополнительные параметры запуска");
        var processing = new ProcessingEditor(this, entry?.ExternalProcessing ?? "", error);
        var launchSettings = new StackPanel { Spacing = 8 };
        // As the original: the first of «Параметры запуска»; every start of the base is elevated (Windows only).
        var runAsAdmin = new CheckBox { Content = DialogLayout.Label("Запуск от имени администратора"), IsChecked = initial?.RunAsAdmin == true,
            IsVisible = OperatingSystem.IsWindows() };
        ToolTip.SetTip(runAsAdmin, "Каждый запуск этой базы — от имени администратора (запрос UAC)");
        launchSettings.Children.Add(runAsAdmin);
        // As the original: «…» inside the field opens «Параметры запуска 1С:Предприятие 8» for the string; nothing is launched.
        additional.InnerRightContent = DialogLayout.Ellipsis("Параметры запуска 1С:Предприятие 8…", async () =>
        {
            var editor = new LaunchParametersWindow((entry ?? new BaseEntry()) with { Name = name.Text?.Trim() is { Length: > 0 } shown ? shown : "Новая база",
                AdditionalParameters = additional.Text ?? "" }, designer: false, editString: true);
            typography?.ApplyDialog(editor);
            if (await editor.ShowDialog<string?>(this) is { } text) { additional.Text = text; additional.Focus(); additional.CaretIndex = text.Length; }
        });
        launchSettings.Children.Add(DialogLayout.Field("Дополнительные параметры:", additional));
        launchSettings.Children.Add(ParameterHint.For(additional));
        // Where the string is kept: the record of the list (1CEStart reads it too), else the settings of the program.
        if (parametersPlace == ParametersPlace.ReadOnlyList)
            launchSettings.Children.Add(new TextBlock { Text = "Список только для чтения — параметры хранятся в настройках программы.",
                Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap });
        if (parametersPlace == ParametersPlace.List)
        {
            var accessCode = new TextBlock { Text = "Код разрешения /UC будет записан в список баз (.v8i): его увидят все, кто открывает этот список, " +
                "и он будет действовать при запуске из стартера 1С.", Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
            void ShowAccessCode() => accessCode.IsVisible = LaunchParameters.HasAccessCode(additional.Text ?? "");
            additional.TextChanged += (_, _) => ShowAccessCode(); ShowAccessCode();
            launchSettings.Children.Add(accessCode);
        }
        launchSettings.Children.Add(DialogLayout.Field("Внешняя обработка:", processing));
        launchSettings.Children.Add(DialogLayout.Label("Обработка запускается только в 1С:Предприятии. Для веб-базы параметры и обработка используют тонкий клиент. Имя и пароль задаются на странице «Аутентификация»."));
        details.Children.Add(DialogLayout.Section("Параметры запуска", launchSettings));
        // The original's page «Аутентификация». The mode is WA= of the list; the rest is local, passwords go to the vault.
        var authentication = entry?.Authentication ?? new AuthenticationSettings();
        var chooseAutomatically = new RadioButton { Content = DialogLayout.Label("Выбирать автоматически"), GroupName = "authentication-mode", IsChecked = entry?.AskCredentials != true };
        var askCredentials = new RadioButton { Content = DialogLayout.Label("Запрашивать имя и пароль"), GroupName = "authentication-mode", IsChecked = entry?.AskCredentials == true, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetName(chooseAutomatically, "Аутентификация: выбирать автоматически");
        AutomationProperties.SetName(askCredentials, "Аутентификация: запрашивать имя и пароль");
        var modes = new WrapPanel { Orientation = Orientation.Horizontal };
        modes.Children.Add(chooseAutomatically); modes.Children.Add(askCredentials);
        var passwordBoxes = new Dictionary<PasswordRole, TextBox>();
        TextBox Text(string value, string automation) { var box = new TextBox { Text = value }; AutomationProperties.SetName(box, automation); return box; }
        TextBox Secret(PasswordRole role, string automation)
        {
            var box = new TextBox { Text = savedPasswords.GetValueOrDefault(role, ""), PasswordChar = '\u25CF' };
            AutomationProperties.SetName(box, automation); passwordBoxes[role] = box; return box;
        }
        // A check box that enables the fields under it, as in the original.
        (CheckBox Box, StackPanel Fields) Switch(string caption, bool on)
        {
            var box = new CheckBox { Content = DialogLayout.Label(caption), IsChecked = on };
            AutomationProperties.SetName(box, caption);
            var fields = new StackPanel { Spacing = 8, Margin = new Thickness(24, 0, 0, 0), IsEnabled = on };
            box.IsCheckedChanged += (_, _) => fields.IsEnabled = box.IsChecked == true;
            return (box, fields);
        }
        var (enterpriseLogin, enterpriseFields) = Switch("Параметры аутентификации для режима «1С:Предприятие»", authentication.EnterpriseLogin);
        var enterpriseUser = Text(authentication.EnterpriseUser, "Пользователь 1С:Предприятия");
        enterpriseFields.Children.Add(DialogLayout.Field("Пользователь:", enterpriseUser));
        enterpriseFields.Children.Add(DialogLayout.Field("Пароль:", Secret(PasswordRole.Enterprise, "Пароль 1С:Предприятия")));
        var (designerLogin, designerFields) = Switch("Параметры аутентификации для режима «Конфигуратор»", authentication.DesignerLogin);
        var designerUser = Text(authentication.DesignerUser, "Пользователь конфигуратора");
        designerFields.Children.Add(DialogLayout.Field("Пользователь:", designerUser));
        designerFields.Children.Add(DialogLayout.Field("Пароль:", Secret(PasswordRole.Designer, "Пароль конфигуратора")));
        var osAuthentication = new CheckBox { Content = DialogLayout.Label("Использовать аутентификацию ОС"), IsChecked = authentication.OsAuthentication };
        AutomationProperties.SetName(osAuthentication, "Использовать аутентификацию ОС");
        var credentials = new StackPanel { Spacing = 10 };
        credentials.Children.Add(DialogLayout.Field("Аутентификация:", modes));
        credentials.Children.Add(enterpriseLogin); credentials.Children.Add(enterpriseFields);
        credentials.Children.Add(designerLogin); credentials.Children.Add(designerFields);
        credentials.Children.Add(osAuthentication);
        var (repository, repositoryFields) = Switch("Подключить хранилище конфигурации", authentication.Repository);
        var repositoryPath = Text(authentication.RepositoryPath, "Каталог хранилища конфигурации");
        repositoryPath.PlaceholderText = "Каталог или tcp://сервер/хранилище";
        var repositoryUser = Text(authentication.RepositoryUser, "Пользователь хранилища");
        repositoryFields.Children.Add(DialogLayout.Field("Каталог:", repositoryPath));
        repositoryFields.Children.Add(DialogLayout.Field("Пользователь:", repositoryUser));
        repositoryFields.Children.Add(DialogLayout.Field("Пароль:", Secret(PasswordRole.Repository, "Пароль хранилища")));
        // The proxy password of the page «Подключение» is saved with the others; ProxyPassword= of the list is not shown or changed.
        var proxyPassword = Secret(PasswordRole.Proxy, "Пароль прокси-сервера");
        if (proxyPassword.Text is not { Length: > 0 } && entry?.ListProxyPassword is { Length: > 0 }) proxyPassword.PlaceholderText = "задан в общем списке";
        ProxyCell("Пароль:", proxyPassword, 1, 1);
        var storage = new StackPanel { Spacing = 10 };
        storage.Children.Add(repository); storage.Children.Add(repositoryFields);
        storage.Children.Add(DialogLayout.Label("Хранилище подключается при запуске конфигуратора."));
        var authenticationPage = new StackPanel { Spacing = 14 };
        authenticationPage.Children.Add(DialogLayout.Section("Параметры аутентификации", credentials));
        authenticationPage.Children.Add(DialogLayout.Section("Хранилище конфигурации", storage));
        authenticationPage.Children.Add(DialogLayout.Label("Пароли хранятся в папке настроек БазоНавигатора в зашифрованном виде и при запуске передаются 1С в командной строке. " +
            "«Выбирать автоматически / Запрашивать имя и пароль» записывается в общий список, как в стандартном окне 1С."));
        if (passwordWarning is not null) authenticationPage.Children.Add(new TextBlock { Text = passwordWarning, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Error });
        // The original's page «Прочие свойства»: local to this program, like the comment.
        var disallowDelete = new CheckBox { Content = DialogLayout.Label("Запретить удаление из списка баз"), IsChecked = entry?.DisallowDelete == true };
        AutomationProperties.SetName(disallowDelete, "Запретить удаление из списка баз");
        var doubleClickItems = new[] { "<Использовать общие настройки>", "1С:Предприятие", "1С:Предприятие с аутентификацией",
            "1С:Предприятие с параметрами", "1С:Конфигуратор", "Открыть свойства информационной базы" };
        var doubleClick = new ComboBox { ItemsSource = doubleClickItems, SelectedIndex = entry?.DoubleClick is { } chosen ? (int)chosen + 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(doubleClick, "Действие при двойном щелчке");
        var permissions = new StackPanel { Spacing = 8 };
        permissions.Children.Add(disallowDelete);
        permissions.Children.Add(DialogLayout.Label("Запрет действует на команды БазоНавигатора. Стандартное окно 1С и другие программы могут убрать базу из общего списка."));
        var otherPage = new StackPanel { Spacing = 14 };
        otherPage.Children.Add(DialogLayout.Section("Разрешения", permissions));
        otherPage.Children.Add(DialogLayout.Section("Прочее", DialogLayout.Field("Действие при двойном щелчке:", doubleClick)));
        // The original's page «Блокировка»: the file 1Cv8.cdn in the folder of a file base. It is read when the page opens
        // and written or deleted on «Сохранить», after the base itself; an existing lock is read-only until «Изменить».
        var lockOn = new CheckBox { Content = DialogLayout.Label("Заблокировать информационную базу") };
        var lockStart = new CheckBox { Content = DialogLayout.Label("Начало:") };
        var lockEnd = new CheckBox { Content = DialogLayout.Label("Окончание:") };
        var startDate = new CalendarDatePicker { SelectedDateFormat = CalendarDatePickerFormat.Short, Width = 140 };
        var endDate = new CalendarDatePicker { SelectedDateFormat = CalendarDatePickerFormat.Short, Width = 140 };
        var startTime = new MaskedTextBox { Mask = "00:00:00", Width = 100 };
        var endTime = new MaskedTextBox { Mask = "00:00:00", Width = 100 };
        // Wide enough for the whole value in the dialog's font (Д3 of the Linux run: DejaVu Sans 22 cut «25.10.202», «00:41:5»).
        foreach (var date in new[] { startDate, endDate }) FitDate(date);
        foreach (var time in new[] { startTime, endTime })
            time.AttachedToVisualTree += (_, _) => time.Width = TextFit.Fit(100, UiTypography.Measure("00:00:00", time.FontFamily, time.FontSize).Width,
                time.Padding.Left + time.Padding.Right + 12);
        var startMenu = new Button { Content = "…" }; var endMenu = new Button { Content = "…" };
        ToolTip.SetTip(startMenu, "Определить начало"); ToolTip.SetTip(endMenu, "Определить продолжительность");
        var lockText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 75 };
        var templates = new Button { Content = "{..}", VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(templates, "Шаблон текста. {ДатаНач} и {ДатаКон} заменяются датами блокировки при сохранении.");
        var lockCode = new TextBox { MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(lockCode, "Код для входа при блокировке (ключ запуска /UC); можно оставить пустым");
        var lockState = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var lockEdit = new Button { Content = "Изменить", IsVisible = false };
        ToolTip.SetTip(lockEdit, "Изменить параметры блокировки");
        foreach (var (control, caption) in new (Control, string)[] { (lockOn, "Заблокировать информационную базу"), (lockStart, "Начало блокировки"),
            (lockEnd, "Окончание блокировки"), (startDate, "Дата начала блокировки"), (startTime, "Время начала блокировки"), (endDate, "Дата окончания блокировки"),
            (endTime, "Время окончания блокировки"), (startMenu, "Определить начало"), (endMenu, "Определить продолжительность"),
            (lockText, "Текст сообщения пользователям"), (templates, "Шаблон текста"), (lockCode, "Код разрешения"), (lockEdit, "Изменить блокировку") })
            AutomationProperties.SetName(control, caption);
        string? lockFolder = null; string? lockRaw = null; InfobaseLock? lockRead = null; var lockEditable = false; var lockLoading = false;
        // Each read has a number: only the latest one fills the page, and closing the dialog drops any read still running.
        var lockGeneration = 0;
        Closed += (_, _) => lockGeneration++;
        DateTime? Moment(CalendarDatePicker date, MaskedTextBox time, string what)
        {
            if (date.SelectedDate is not { } day) throw new ArgumentException($"Укажите дату {what} блокировки.");
            if (!TimeSpan.TryParseExact(time.Text ?? "", @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var clock)) throw new ArgumentException($"Время {what} блокировки указано неверно.");
            return day.Date + clock;
        }
        void SetMoment(CalendarDatePicker date, MaskedTextBox time, DateTime value) { date.SelectedDate = value.Date; time.Text = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        DateTime? TryMoment(CalendarDatePicker date, MaskedTextBox time) { try { return Moment(date, time, ""); } catch (ArgumentException) { return null; } }
        DateTime Now() { var now = DateTime.Now; return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); }
        void LockDefaults() { var start = Now().AddMinutes(5); SetMoment(startDate, startTime, start); SetMoment(endDate, endTime, start.AddMinutes(30)); }
        void ShowLock()
        {
            var on = lockEditable && lockOn.IsChecked == true;
            lockOn.IsEnabled = lockEditable;
            lockStart.IsEnabled = lockEnd.IsEnabled = lockText.IsEnabled = lockCode.IsEnabled = templates.IsEnabled = on;
            startDate.IsEnabled = startTime.IsEnabled = startMenu.IsEnabled = on && lockStart.IsChecked == true;
            endDate.IsEnabled = endTime.IsEnabled = endMenu.IsEnabled = on && lockEnd.IsChecked == true;
            lockEdit.IsVisible = !lockEditable && !lockLoading && lockRaw is not null;
        }
        var lockFilling = false; var lockToggled = false;
        lockOn.IsCheckedChanged += (_, _) =>
        {
            if (!lockFilling) lockToggled = true;
            // As in the original, switching the lock on starts from both dates: in 5 minutes for 30 minutes.
            if (!lockFilling && lockOn.IsChecked == true) { lockStart.IsChecked = lockEnd.IsChecked = true; LockDefaults(); }
            ShowLock();
        };
        lockStart.IsCheckedChanged += (_, _) => ShowLock(); lockEnd.IsCheckedChanged += (_, _) => ShowLock();
        lockEdit.Click += (_, _) => { lockEditable = true; ShowLock(); lockOn.Focus(); };
        MenuFlyout Menu(IEnumerable<(string Caption, Action Run)> items)
        {
            var menu = new MenuFlyout();
            foreach (var (caption, run) in items) { var item = new MenuItem { Header = caption }; item.Click += (_, _) => run(); menu.Items.Add(item); }
            return menu;
        }
        static string Minutes(int minutes, string prefix) => minutes < 60 ? $"{prefix} {minutes} минут" : $"{prefix} {minutes / 60} час{(minutes == 60 ? "" : "а")}";
        startMenu.Flyout = Menu(new[] { 0, 5, 10, 15, 20, 25, 30, 45, 50, 60, 120, 180 }.Select(minutes => (minutes == 0 ? "Немедленно" : Minutes(minutes, "Через"), (Action)(() =>
        {
            var start = Now().AddMinutes(minutes);
            SetMoment(startDate, startTime, start);
            if (TryMoment(endDate, endTime) is not { } end || end <= start) SetMoment(endDate, endTime, start.AddMinutes(10));
        }))));
        endMenu.Flyout = Menu(new[] { 5, 10, 15, 20, 25, 30, 60, 120, 180 }.Select(minutes => (Minutes(minutes, "На"), (Action)(() =>
            SetMoment(endDate, endTime, (lockStart.IsChecked == true ? TryMoment(startDate, startTime) : null) is { } start && start > Now() ? start.AddMinutes(minutes) : Now().AddMinutes(minutes))))));
        templates.Flyout = Menu(new[] { "Проводятся технические работы", "Выполняется обновление информационной базы" }.Select(line => (line + "...", (Action)(() =>
        {
            lockText.Text = line + Environment.NewLine + "Период блокировки с {ДатаНач} по {ДатаКон}";
            lockText.Focus(); lockText.CaretIndex = lockText.Text.Length;
        }))));
        async Task LoadLock()
        {
            var folder = location.Text?.Trim() ?? "";
            var generation = ++lockGeneration;
            lockFolder = folder; lockLoading = true; lockEditable = false; lockToggled = false; lockRaw = null; lockRead = null;
            lockState.Foreground = UiColors.Muted; lockState.Text = "Чтение файла блокировки…"; ShowLock();
            string? raw = null; string? failure = null;
            try { raw = await Task.Run(() => File.Exists(InfobaseLock.PathIn(folder)) ? File.ReadAllText(InfobaseLock.PathIn(folder), Encoding.UTF8) : null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { failure = LegacyImport.ErrorMessage(ex); }
            if (generation != lockGeneration) return;
            InfobaseLock? read = null;
            if (raw is not null) { try { read = InfobaseLock.Parse(raw); } catch (InvalidDataException ex) { failure = ex.Message; } }
            lockLoading = false; lockRaw = raw; lockRead = read; lockFilling = true;
            lockOn.IsChecked = read?.On == true; lockText.Text = read?.Message ?? ""; lockCode.Text = read?.Code ?? "";
            lockStart.IsChecked = read is null || read.Start is not null; lockEnd.IsChecked = read is null || read.End is not null;
            LockDefaults();
            if (read?.Start is { } start) SetMoment(startDate, startTime, start);
            if (read?.End is { } end) SetMoment(endDate, endTime, end);
            lockFilling = false;
            if (failure is not null && raw is null)
            { lockFolder = null; lockState.Foreground = UiColors.Error; lockState.Text = "Файл блокировки не прочитан: " + failure; }
            else if (failure is not null)
            { lockState.Foreground = UiColors.Error; lockState.Text = failure + " Нажмите «Изменить», чтобы заменить файл или снять блокировку."; }
            else if (read is not null)
            { lockState.Foreground = UiColors.Muted; lockState.Text = read.Describe(DateTime.Now) + " Чтобы изменить или снять блокировку, нажмите «Изменить»."; }
            else { lockEditable = true; lockState.Foreground = UiColors.Muted; lockState.Text = "Блокировка не установлена."; }
            ShowLock();
        }
        // The rows wrap on a narrow screen; the captions share one width, so the dates line up.
        WrapPanel MomentRow(CheckBox caption, Control date, Control time, Control menu)
        {
            var cell = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "lockCaption" } } };
            cell.Children.Add(caption);
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
            row.Children.Add(cell); row.Children.Add(date); row.Children.Add(time); row.Children.Add(menu);
            return row;
        }
        var startRow = MomentRow(lockStart, startDate, startTime, startMenu);
        var endRow = MomentRow(lockEnd, endDate, endTime, endMenu);
        var textRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        textRow.Children.Add(lockText); Grid.SetColumn(templates, 1); textRow.Children.Add(templates);
        var lockFields = new StackPanel { Spacing = 10, Margin = new Thickness(24, 0, 0, 0) };
        Grid.SetIsSharedSizeScope(lockFields, true);
        lockFields.Children.Add(startRow); lockFields.Children.Add(endRow);
        lockFields.Children.Add(DialogLayout.Field("Текст сообщения пользователям:", textRow));
        lockFields.Children.Add(DialogLayout.Field("Код разрешения:", lockCode));
        var lockBody = new StackPanel { Spacing = 10 };
        lockBody.Children.Add(lockOn); lockBody.Children.Add(lockFields); lockBody.Children.Add(lockState); lockBody.Children.Add(lockEdit);
        lockBody.Children.Add(DialogLayout.Label("Файл 1Cv8.cdn записывается в каталог базы при сохранении свойств. Пользователи, которые уже работают в базе, " +
            "не отключаются; без кода разрешения новые сеансы не открываются."));
        var lockPage = DialogLayout.Section("Установка блокировки информационной базы", lockBody);
        var lockUnavailable = DialogLayout.Section("Установка блокировки информационной базы",
            DialogLayout.Label(MainCommands.SetLockKindText));
        // The original's page «Резервное копирование» (smFormProperty.dfm:1048-1140): «Сценарий:», «Каталог:», «Префикс:» — local to this
        // program, like the comment. Without readable scenarios the choice is kept as it is and cannot be changed.
        var scenarioChoices = BackupScenarioEdit.Choices(backupScenarios?.Scenarios ?? [], backupScenarios is null ? null : entry?.BackupScriptId);
        var backupScenario = new ComboBox { ItemsSource = scenarioChoices.Select(x => x.Text).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, scenarioChoices.Select(x => x.Id).ToList().IndexOf(entry?.BackupScriptId)), IsEnabled = backupScenarios is not null };
        backupScenario.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => DialogLayout.Label(value ?? ""));
        AutomationProperties.SetName(backupScenario, "Сценарий резервирования");
        ToolTip.SetTip(backupScenario, "Сценарий, согласно которому будет выполняться резервное копирование");
        var backupFolder = new TextBox { Text = entry?.BackupFolder ?? "" };
        AutomationProperties.SetName(backupFolder, "Каталог резервных копий базы");
        ToolTip.SetTip(backupFolder, "Каталог назначения, в который будет производиться выгрузка");
        var backupGlyph = typography is null ? UiIcons.Create("folder") : UiIcons.Create("folder", typography.IconSize);
        backupGlyph.Margin = new Thickness(6, 0, 0, 0); backupGlyph.VerticalAlignment = VerticalAlignment.Center;
        backupFolder.InnerLeftContent = backupGlyph;
        var backupBrowse = new Button { Content = "…", Padding = new Thickness(8, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(backupBrowse, "Выбрать каталог"); AutomationProperties.SetName(backupBrowse, "Выбрать каталог резервных копий базы");
        backupBrowse.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог резервных копий", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) backupFolder.Text = path;
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
        };
        backupFolder.InnerRightContent = backupBrowse;
        var backupPrefix = new TextBox { Text = entry?.BackupPrefix ?? "", MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(backupPrefix, "Префикс имени файла резервной копии");
        ToolTip.SetTip(backupPrefix, "Префикс, который будет добавлен к имени файла");
        var backupBody = new StackPanel { Spacing = 10 };
        backupBody.Children.Add(DialogLayout.Field("Сценарий:", backupScenario));
        backupBody.Children.Add(DialogLayout.Field("Каталог:", backupFolder));
        backupBody.Children.Add(DialogLayout.Field("Префикс:", backupPrefix));
        backupBody.Children.Add(DialogLayout.Label("Пустой каталог — «1. Каталог» сценария, папка Backup рядом с файловой базой или общий «Каталог по-умолчанию» " +
            "из настроек; относительный каталог отсчитывается от общего. Префикс подставляется в шаблон имени файла вместо {Prefix}. Сценарии " +
            "задаются в настройках программы («Сценарии резервирования»)."));
        if (backupWarning is not null) backupBody.Children.Add(new TextBlock { Text = backupWarning, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Error });
        var backupPage = DialogLayout.Section("Резервное копирование", backupBody);
        var navigation = new ListBox { ItemsSource = new[] { "1. Сведения", "2. Подключение", "3. Аутентификация", "4. Резервное копирование", "5. Прочие свойства", "6. Блокировка" }, SelectedIndex = 0,
            Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(navigation, "Разделы свойств базы");
        ToolTip.SetTip(navigation, "Alt+1 — Сведения; Alt+2 — Подключение; Alt+3 — Аутентификация; Alt+4 — Резервное копирование; Alt+5 — Прочие свойства; Alt+6 — Блокировка");
        navigation.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => DialogLayout.Label(value ?? ""));
        // Clipped: nothing of the pages is drawn over the lines below them when the window leaves them little height.
        var pages = new Grid { ColumnDefinitions = new("170,*"), RowDefinitions = new("Auto,*"), ColumnSpacing = 12, RowSpacing = 10, ClipToBounds = true };
        pages.Children.Add(navigation); Grid.SetRowSpan(navigation, 2);
        // The vertical scroll bar of a page takes its own column on Windows and Linux alike (Linux re-check 27.10): no margin for it.
        // Wrapped hints are fitted to their column instead (DialogLayout.WrapWithin, at the end of the constructor).
        var pageContent = new ContentControl { Content = details };
        var page = new ScrollViewer { Content = pageContent, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(page, 1); Grid.SetRowSpan(page, 2); pages.Children.Add(page);
        var connectionPage = DialogLayout.Section("Размещение информационной базы", connection);
        void ShowPage()
        {
            var fileBase = Kind() == ConnectionKind.File;
            pageContent.Content = navigation.SelectedIndex switch { 1 => connectionPage, 2 => authenticationPage, 3 => backupPage, 4 => otherPage, LockPage => fileBase ? lockPage : lockUnavailable, _ => details };
            if (navigation.SelectedIndex == LockPage && fileBase && lockFolder != (location.Text?.Trim() ?? "")) _ = LoadLock();
        }
        navigation.SelectionChanged += (_, _) => ShowPage();
        // A page asked by the caller, as the original's OpenBaseProperty with a page («Установить блокировку» opens «Блокировка»).
        if (startPage > 0) Opened += (_, _) => navigation.SelectedIndex = startPage;
        OnKindChanged(() => { if (navigation.SelectedIndex == LockPage) ShowPage(); });
        pages.SizeChanged += (_, _) =>
        {
            var narrow = pages.Bounds.Width < 560;
            var navigationWidth = Math.Max(170, new[] { "3. Аутентификация", "4. Резервное копирование", "5. Прочие свойства", "6. Блокировка" }.Max(x => UiTypography.Measure(x, FontFamily, FontSize).Width) + 36);
            pages.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(navigationWidth);
            pages.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRowSpan(navigation, narrow ? 1 : 2); navigation.MaxHeight = narrow ? Math.Max(100, FontSize * 4 + 24) : double.PositiveInfinity;
            // Narrow, the list shares the height left to the pages with the page instead of keeping its own: an Auto row went past
            // the bottom of the pages over the path line and its message at 22 pt in 380×350 (Linux check 28.10). The list scrolls.
            pages.RowDefinitions[0].Height = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            pages.RowDefinitions[0].MaxHeight = narrow ? navigation.MaxHeight : double.PositiveInfinity;
            Grid.SetColumn(page, narrow ? 0 : 1); Grid.SetRow(page, narrow ? 1 : 0); Grid.SetRowSpan(page, narrow ? 1 : 2);
        };
        Grid.SetRow(pages, 1); panel.Children.Add(pages);
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt) return;
            if (e.Key == Key.D1 || e.Key == Key.NumPad1) { navigation.SelectedIndex = 0; e.Handled = true; }
            if (e.Key == Key.D2 || e.Key == Key.NumPad2) { navigation.SelectedIndex = 1; e.Handled = true; }
            if (e.Key == Key.D3 || e.Key == Key.NumPad3) { navigation.SelectedIndex = 2; e.Handled = true; }
            if (e.Key == Key.D4 || e.Key == Key.NumPad4) { navigation.SelectedIndex = 3; e.Handled = true; }
            if (e.Key == Key.D5 || e.Key == Key.NumPad5) { navigation.SelectedIndex = 4; e.Handled = true; }
            if (e.Key == Key.D6 || e.Key == Key.NumPad6) { navigation.SelectedIndex = LockPage; e.Handled = true; }
        };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить" };
        ToolTip.SetTip(save, "Сохранить · Ctrl+Enter");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && save.IsEnabled && save.IsEffectivelyEnabled)
            { e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        };
        // The connection of the chosen kind as the list keeps it; the server's two fields become server\base.
        string LocationText()
        {
            if (Kind() == ConnectionKind.File) return location.Text?.Trim() ?? "";
            if (Kind() == ConnectionKind.Web) return webAddress.Text?.Trim() ?? "";
            return BaseEdit.ServerLocation(cluster.Text ?? "", serverBase.Text ?? "", initialKind == ConnectionKind.Server ? initial!.Location : null);
        }
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true; panel.IsEnabled = buttons.IsEnabled = false;
            try
            {
                var clientPath = platformEditor.Client.Text?.Trim() ?? "";
                var rule = platformEditor.ReadRule();
                if (rule is null && clientPath != (entry?.Executable ?? "") && clientPath.Length > 0 && (!Path.IsPathFullyQualified(clientPath) || !await Task.Run(() => File.Exists(clientPath)))) throw new ArgumentException("Клиент не найден. Укажите полный путь или оставьте поле пустым.");
                var launchText = additional.Text ?? "";
                var withParameters = BaseEdit.WithParameters(draftEntry, launchText, parametersPlace == ParametersPlace.List);
                // A string kept as it was (inherited from a read-only list) is not checked again: it was not typed here.
                if (withParameters.AdditionalParameters is not null || withParameters.ListParameters != draftEntry.ListParameters) LaunchParameters.Validate(launchText);
                var processingPath = processing.PathText;
                if (processingPath != (entry?.ExternalProcessing ?? ""))
                    await Task.Run(() => LaunchParameters.ValidateProcessing(processingPath));
                // «Каталог:» of «Резервное копирование» follows the rule of the scenario folders (the Windows set on Windows; Grok audit, part 3).
                if (BackupScenarioEdit.InvalidFolder(backupFolder.Text ?? "", OperatingSystem.IsWindows()))
                {
                    navigation.SelectedIndex = 3; backupFolder.Focus();
                    throw new ArgumentException("Резервное копирование: каталог содержит некорректные символы!");
                }
                // Over the settings the page was opened with: their unknown fields stay (Codex full audit 24.10, P2-6).
                var settings = authentication with
                {
                    EnterpriseLogin = enterpriseLogin.IsChecked == true, EnterpriseUser = enterpriseUser.Text?.Trim() ?? "",
                    DesignerLogin = designerLogin.IsChecked == true, DesignerUser = designerUser.Text?.Trim() ?? "",
                    OsAuthentication = osAuthentication.IsChecked == true,
                    Repository = repository.IsChecked == true, RepositoryPath = repositoryPath.Text?.Trim() ?? "", RepositoryUser = repositoryUser.Text?.Trim() ?? ""
                };
                var result = withParameters with { Name = name.Text?.Trim() ?? "", Kind = Kind(), Location = LocationText(), Group = Groups.Normalize(group.SelectedItem as string ?? ""), Executable = clientPath, PlatformRule = rule,
                    Comment = comment.Text ?? "", ExternalProcessing = processingPath,
                    Configuration = configuration.Text?.Trim() ?? "", ConfigurationRelease = release.Text?.Trim() ?? "",
                    Authentication = settings.IsEmpty ? null : settings, AskCredentials = askCredentials.IsChecked == true,
                    WebServerAskCredentials = webAuthenticationChosen ? webAuthentication.SelectedIndex == 1 : entry?.WebServerAskCredentials,
                    ConnectionSpeed = speedChosen && connectionSpeed.SelectedIndex >= 0 ? speedValues[connectionSpeed.SelectedIndex] : entry is null ? "" : entry.ConnectionSpeed,
                    // An untouched choice keeps a record without UseProxy= so; a chosen one is written even when it is the one shown.
                    UseProxy = proxyChosen ? (ProxyMode)proxyMode.SelectedIndex : entry?.UseProxy,
                    ProxyServer = proxyServer.Text?.Trim() ?? "", ProxyPort = proxyPort.Text?.Trim() ?? "", ProxyUser = proxyUser.Text?.Trim() ?? "",
                    DisallowDelete = disallowDelete.IsChecked == true, RunAsAdmin = runAsAdmin.IsChecked == true,
                    CheckServerAgent = OperatingSystem.IsWindows() ? checkAgent.IsChecked == true : entry?.CheckServerAgent == true,
                    // An untouched «Режим» keeps App= as written (its spelling, «Auto» or none).
                    ClientType = clientMode.SelectedIndex == initialClientMode ? draftEntry.ClientType : BaseDialog.ClientTypes[clientMode.SelectedIndex],
                    ManagedApplication = clientMode.SelectedIndex == initialClientMode ? draftEntry.ManagedApplication : clientMode.SelectedIndex == 3, DoubleClick = doubleClick.SelectedIndex > 0 ? (BaseDoubleClick)(doubleClick.SelectedIndex - 1) : null,
                    // Unreadable scenarios keep the base's choice; «<Использовать сценарий по умолчанию>» is none.
                    BackupScriptId = backupScenarios is null ? draftEntry.BackupScriptId : scenarioChoices[Math.Max(0, backupScenario.SelectedIndex)].Id,
                    BackupFolder = backupFolder.Text?.Trim() ?? "", BackupPrefix = backupPrefix.Text?.Trim() ?? "" };
                result.Validate();
                if (result.Kind == ConnectionKind.Web && result.UseProxy == ProxyMode.Manual) LaunchAuthentication.CheckProxy(result.ProxyServer, result.ProxyPort);
                // Only what the user changed: an unreadable vault shows empty fields, and leaving them empty changes nothing.
                var passwords = passwordBoxes.Where(x => (x.Value.Text ?? "") != savedPasswords.GetValueOrDefault(x.Key, ""))
                    .ToDictionary(x => x.Key, x => (string?)(x.Value.Text ?? ""));
                // A proxy changed with its password left in the field: the password goes with the new proxy.
                if (entry is not null && !BaseEdit.SameProxy(result, entry) && proxyPassword.Text is { Length: > 0 } kept)
                    passwords[PasswordRole.Proxy] = kept;
                if (passwords.Values.Any(x => x!.Any(char.IsControl))) throw new ArgumentException("Пароль не должен содержать переносы строк и управляющие символы.");
                error.Text = "Сохранение…";
                // The lock is checked and confirmed before anything is saved, and written after the base, as in the original.
                (string Folder, InfobaseLock? Lock)? lockChange = null;
                if (result.Kind == ConnectionKind.File && lockEditable && !lockLoading && lockFolder is not null)
                {
                    if (result.Location != lockFolder) throw new ArgumentException("Каталог базы изменён после чтения блокировки. Откройте страницу «Блокировка» ещё раз и проверьте её.");
                    InfobaseLock? wanted = null;
                    if (lockOn.IsChecked == true)
                    {
                        var start = lockStart.IsChecked == true ? Moment(startDate, startTime, "начала") : null;
                        var end = lockEnd.IsChecked == true ? Moment(endDate, endTime, "окончания") : null;
                        if (start is { } from && end is { } to && to <= from) throw new ArgumentException("Окончание блокировки должно быть позже начала.");
                        var code = lockCode.Text?.Trim() ?? "";
                        if (code.Any(char.IsControl)) throw new ArgumentException("Код разрешения не должен содержать переносы строк и управляющие символы.");
                        wanted = new InfobaseLock(true, start, end, InfobaseLock.Expand(lockText.Text ?? "", start, end), code,
                            lockRead?.Parameter ?? "", lockRead?.Tail ?? "", lockRead?.Framed ?? true);
                    }
                    // Removal only when the user switched the lock off: «Изменить» alone changes nothing for a file with flag 0.
                    // A damaged file is shown switched off, and «Изменить» there is the way to remove it, as the page says.
                    if (wanted is not null ? wanted != lockRead : lockRaw is not null && (lockToggled || lockRead is null)) lockChange = (lockFolder, wanted);
                }
                if (lockChange is { } asked)
                {
                    var lines = new List<string> { $"База: {result.Name}", $"Каталог: {asked.Folder}" };
                    if (asked.Lock is { } planned)
                    {
                        lines.Add($"Период: с {planned.Start?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "момента сохранения"} " +
                            $"по {planned.End?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "снятия блокировки"}.");
                        lines.Add(planned.Code.Length > 0 ? "Войти в базу можно будет только с кодом разрешения." : "Код разрешения не задан: войти в базу не сможет никто, пока блокировка действует.");
                        if (planned.End is { } until && until <= DateTime.Now) lines.Add("Окончание уже прошло: блокировка действовать не будет.");
                        if (string.IsNullOrWhiteSpace(planned.Message)) lines.Add("Текст сообщения пользователям пустой.");
                    }
                    var heading = asked.Lock is null ? "Снять блокировку информационной базы?" : "Установить блокировку информационной базы?";
                    if (!await confirm(this, heading, lines, asked.Lock is null ? "Да, снять" : "Да, установить")) { error.Text = "Сохранение отменено."; return; }
                }
                var failure = await saveEntry(result, passwords, lockChange?.Folder);
                if (failure is not null) { error.Text = failure; return; }
                if (lockChange is { } apply)
                {
                    try
                    {
                        var raw = lockRaw;
                        await Task.Run(() => { if (apply.Lock is null) InfobaseLock.Remove(apply.Folder, raw); else apply.Lock.Write(apply.Folder, raw); });
                        AppLog.Info(apply.Lock is null ? "lock.removed" : "lock.set");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        AppLog.Warning("lock.write.failed", ex);
                        var changed = ex is InfobaseLockChangedException;
                        error.Text = $"Свойства базы сохранены, но блокировка не {(apply.Lock is null ? "снята" : "установлена")}: " + (changed
                            ? ex.Message + " Страница перечитана: проверьте блокировку и нажмите «Сохранить» ещё раз."
                            : (ex is DirectoryNotFoundException ? ex.Message : LegacyImport.ErrorMessage(ex)) + " Устраните причину и нажмите «Сохранить» ещё раз.");
                        if (changed) { lockFolder = null; navigation.SelectedIndex = LockPage; ShowPage(); }
                        return;
                    }
                }
                saving = false;
                Close();
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
            finally { saving = false; panel.IsEnabled = buttons.IsEnabled = true; }
        };
        var cancel = new Button { Content = "Закрыть", IsCancel = true }; cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        // A base of a common list of 1CEStart: the page is shown, only «Дополнительные параметры» are saved, into the settings of the
        // program (the reason stands where errors do; the save callback refuses any other change).
        if (readOnly is not null)
        {
            Title += " (общий список)";
            error.Text = readOnly; error.Foreground = UiColors.Muted;
            save.Click += (_, _) => error.Foreground = UiColors.Error;
        }
        // The original's LabelV8Info under the pages: the list file of the base, muted, underlined under the pointer; a long path is
        // trimmed (the full one in the tooltip), the window does not widen. A click or Enter/Space opens it in a text editor, as
        // «Сервис → Список ИБ ▸ Открыть в редакторе»; a missing file or a refusal is said under it (the original is silent).
        if (listFile is not null)
        {
            var shownPath = listFile.Replace("\0", "\\0");
            // A prefix and the end of the path with «…» between them, so the drive and the file name stay; PathSegmentEllipsis cut a
            // file name wider than the window without «…».
            var pathText = new TextBlock { Text = shownPath, TextTrimming = TextTrimming.PrefixCharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            // UiTypography.ApplyDialog, called by the owner after this constructor, wraps every text of the dialog: this one stays a line.
            pathText.PropertyChanged += (_, e) =>
            { if (e.Property == TextBlock.TextWrappingProperty && pathText.TextWrapping != TextWrapping.NoWrap) pathText.TextWrapping = TextWrapping.NoWrap; };
            var link = new Button { Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0,
                Foreground = UiColors.Muted, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Content = pathText };
            // The theme's hover and press backgrounds and colours are not those of a link: the line stays transparent and turns the
            // colour of a link instead; its own text (not that of the tooltip) is underlined under the pointer and with the keyboard focus.
            foreach (var state in new[] { ":pointerover", ":pressed" })
                link.Styles.Add(new Style(s => s.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.ForegroundProperty, UiColors.SectionText) } });
            foreach (var state in new[] { ":pointerover", ":focus-visible" })
                link.Styles.Add(new Style(s => s.OfType<Button>().Class(state).Child().OfType<TextBlock>())
                { Setters = { new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline) } });
            AutomationProperties.SetName(link, "Файл списка баз: " + shownPath);
            ToolTip.SetTip(link, "Открыть в текстовом редакторе: " + shownPath);
            // The link says its own failure under itself and never touches the error line of the dialog: a save result, the note of
            // a common list and a later save error keep their text and colour (Codex recheck and Grok audit 28.10). A later success
            // clears only this message. Wrapped (ApplyDialog), hidden when empty; the window keeps its width. The path is not repeated:
            // it is on the line right above.
            var linkMessage = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
            // As the error line of DialogLayout.Frame, the message scrolls past a few lines: with the long path at a large font it
            // would push the pages over the buttons in a small window.
            var linkMessages = new ScrollViewer { Content = linkMessage, MaxHeight = 64, IsVisible = false,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            void SayLink(string? text) { linkMessage.Text = text; linkMessages.IsVisible = !string.IsNullOrEmpty(text); }
            // One activation at a time: a double click or held Enter on a slow share opens one editor. A dialog closed during the
            // existence check opens nothing and says nothing; a launch already handed to the system is not recalled (Codex audit 28.10, P3).
            var opening = false;
            var dialogClosed = false;
            Closed += (_, _) => dialogClosed = true;
            link.Click += async (_, _) =>
            {
                if (opening) return;
                opening = true;
                try
                {
                    string? failure;
                    try
                    {
                        // The list may lie on a slow share: the check is off the UI thread. It stays, as the original's: Notepad would
                        // offer to create a missing file. File.Exists is also false for a file it cannot reach, hence «или недоступен».
                        var exists = await Task.Run(() => File.Exists(listFile));
                        if (dialogClosed) return;
                        failure = !exists ? "Файл списка баз не найден или недоступен."
                            : await ListEditor.Open(Launcher, listFile) ? null : "Не удалось открыть файл списка баз.";
                    }
                    catch (Exception ex) { AppLog.Warning("list.editor.failed", ex); failure = "Не удалось открыть файл списка баз: " + ex.Message; }
                    if (dialogClosed) return;
                    SayLink(failure);
                }
                finally { opening = false; }
            };
            var linkLine = new StackPanel { Spacing = 4 };
            linkLine.Children.Add(link); linkLine.Children.Add(linkMessages);
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(linkLine, 2); panel.Children.Add(linkLine);
        }
        foreach (var shown in new Control[] { details, connectionPage, authenticationPage, backupPage, otherPage, lockPage, lockUnavailable })
            DialogLayout.WrapWithin(shown);
        Content = DialogLayout.Frame(panel, buttons, error, scrollBody: false);
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
