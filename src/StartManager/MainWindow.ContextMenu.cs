using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // The menus in the original's classic look: the list's context menu and the «Копировать» menus.
    private ContextMenu ClassicMenu(string name)
    {
        var menu = new ContextMenu { FontFamily = typography.Family, FontSize = typography.InterfaceSize,
            Background = UiColors.Surface, Foreground = UiColors.Text };
        AutomationProperties.SetName(menu, name);
        // Keep the menu on the screen: slide or flip it, and shrink it to scroll when it is still too tall (UI/UX re-check).
        menu.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.FlipY |
            PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.ResizeY;
        // Fluent keeps menu icons in a 16 px presenter; a selector with a class wins over its control theme.
        menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Template().Name("PART_IconPresenter"))
        { Setters = { new Setter(WidthProperty, typography.IconSize), new Setter(HeightProperty, typography.IconSize) } });
        // The original's classic menu: compact rows and a yellow highlight with a border.
        menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled"))
        { Setters = { new Setter(PaddingProperty, new Thickness(6, 3)), new Setter(MinHeightProperty, 0d) } });
        menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Child().OfType<Grid>().Child().OfType<TextBlock>())
        { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.Text) } });
        menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Child().OfType<Grid>().Child().OfType<TextBlock>().Class("gesture"))
        { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.Muted) } });
        menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Class(":disabled").Child().OfType<Grid>().Child().OfType<TextBlock>())
        { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.Muted) } });
        // The yellow highlight is light in both themes: its text is dark. Only the item's own header: a highlighted parent
        // does not darken the texts of its submenu.
        foreach (var state in new[] { ":pointerover", ":selected" })
            menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Class(state).Child().OfType<Grid>().Child().OfType<TextBlock>())
            { Setters = { new Setter(TextBlock.ForegroundProperty, Brush.Parse("#1F1F1F")) } });
        foreach (var state in new[] { ":pointerover", ":selected" })
            menu.Styles.Add(new Style(s => s.OfType<MenuItem>().Class("scaled").Class(state).Template().Name("PART_LayoutRoot"))
            { Setters = { new Setter(Border.BackgroundProperty, Brush.Parse("#FFE38A")), new Setter(Border.BorderBrushProperty, Brush.Parse("#E5B53A")),
                new Setter(Border.BorderThicknessProperty, new Thickness(1)) } });
        return menu;
    }

    private void AttachListContextMenu()
    {
        var menu = ClassicMenu("Действия со списком баз");
        bases.ContextMenu = menu;
        bases.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(bases).Properties.IsRightButtonPressed) return;
            var ancestors = (e.Source as Visual)?.GetSelfAndVisualAncestors().ToList() ?? [];
            if (ancestors.OfType<ScrollBar>().Any()) return;
            var container = ancestors.OfType<ListBoxItem>().FirstOrDefault();
            bases.SelectedItem = container?.Content as LibraryRow;
            bases.Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        bases.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ScrollBar>().Any() == true)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) => BuildContextMenu(menu);
        bases.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (!OperatingSystem.IsWindows() && (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            { e.Handled = true; bases.RaiseEvent(new ContextRequestedEventArgs()); }
        }, RoutingStrategies.Tunnel);
        bases.KeyDown += (_, e) =>
        {
            if (e.Handled) return;
            // Windows raises native ContextRequested. X11 requests it on release,
            // keeping the Menu key's release from dismissing a menu opened on press.
            if (!OperatingSystem.IsWindows() && (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            {
                e.Handled = true; return;
            }
            if (loadFailed) return;
            // The list's keys come from the table of Shortcuts, as the window's (see the constructor of MainWindow); a key whose
            // command is not available for the selected row (MainCommands) stays unhandled.
            if (ShortcutKeys.Find(ShortcutScope.List, e) is not { } command) return;
            if (!MainCommands.CanExecute(MainCommands.Of(command), CurrentState())) { e.Handled = Refuse(MainCommands.Of(command), list: true); return; }
            System.Action? action = command switch
            {
                ShortcutCommand.Launch => () => Launch(false),
                ShortcutCommand.Designer => () => Launch(true),
                ShortcutCommand.LaunchWithLogin => () => LaunchWithLogin(false),
                ShortcutCommand.DesignerWithLogin => () => LaunchWithLogin(true),
                ShortcutCommand.ToggleFavorite => ToggleFavorite,
                ShortcutCommand.Rename => () => EditEntryLabel(false),
                ShortcutCommand.MoveToGroup => () => EditEntryLabel(true),
                ShortcutCommand.AddBase => async () => await AddBase(),
                ShortcutCommand.AddGroup => () => EditGroup(null),
                ShortcutCommand.DuplicateBase => DuplicateBase,
                ShortcutCommand.Delete => Delete,
                _ => null
            };
            if (action is not null) { e.Handled = true; action(); }
        };
    }

    // Every menu header is a Grid with TextBlocks, so the colour styles reach exactly the header of their own item.
    // The gesture is drawn as text: Avalonia shows Key.Enter as "Return". The keys themselves are handled in KeyDown.
    private static Grid MenuHeader(string label, string? gesture = null)
    {
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 24, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (gesture is not null)
        {
            var keys = new TextBlock { Text = gesture, VerticalAlignment = VerticalAlignment.Center, Classes = { "gesture" } };
            Grid.SetColumn(keys, 1); header.Children.Add(keys);
        }
        return header;
    }

    // An item of the table of commands (MainCommands): its caption and key, on or off by its rule. A click checks the rule again
    // with the state of that moment (the list may have been read again while the menu was open); before: a check of the menu itself.
    private MenuItem CommandItem(MainCommand command, CommandState state, System.Action action, string? icon = null, string? caption = null,
        Func<bool>? before = null)
    {
        var label = caption ?? MainCommands.Caption(command, state);
        var gesture = MainCommands.KeyText(command);
        var item = new MenuItem { Header = MenuHeader(label, gesture), IsEnabled = MainCommands.CanExecute(command, state), Classes = { "scaled" } };
        if (icon is not null) item.Icon = UiIcons.Create(icon, typography.IconSize);
        AutomationProperties.SetName(item, label);
        if (gesture is not null) AutomationProperties.SetAcceleratorKey(item, gesture);
        item.Click += (_, _) => { if ((before is null || before()) && MainCommands.CanExecute(command, CurrentState())) action(); };
        return item;
    }

    private void BuildContextMenu(ContextMenu menu)
    {
        var target = bases.SelectedItem as LibraryRow;
        var source = library.ListPath;
        var ready = !loadFailed && !reading;
        var state = CurrentState();
        var entry = target?.Entry;
        var group = target is { Entry: null, Section: LibrarySection.None, Orphan: null };
        // A base of a common list and a group only such bases are in are started and viewed, never changed.
        var own = CommonOf(entry) is null && (!group || Groups.All(library).Contains(target!.Group, StringComparer.OrdinalIgnoreCase));
        bool SelectTarget()
        {
            if (loadFailed || reading || source != library.ListPath) return false;
            if (target is null) { bases.SelectedItem = null; return true; }
            var current = bases.Items.OfType<LibraryRow>().FirstOrDefault(x => x.Key == target.Key);
            if (current is null) { status.Text = "Строка изменилась. Откройте меню заново."; return false; }
            bases.SelectedItem = current; return true;
        }
        MenuItem Item(string label, string icon, string? gesture, bool enabled, System.Action action)
        {
            var item = new MenuItem { Header = MenuHeader(label, gesture), Icon = UiIcons.Create(icon, typography.IconSize), IsEnabled = enabled };
            AutomationProperties.SetName(item, label); item.Classes.Add("scaled");
            if (gesture is not null) AutomationProperties.SetAcceleratorKey(item, gesture);
            item.Click += (_, _) => { if (SelectTarget()) action(); };
            return item;
        }
        // The items of the table of commands: the same caption, key and availability as the toolbar and the keys.
        MenuItem Command(MainCommand command, string icon, System.Action action, string? caption = null) =>
            CommandItem(command, state, action, icon, caption, SelectTarget);
        var items = new List<Control>();
        if (entry is not null)
        {
            items.Add(Command(MainCommand.Launch, "play", () => Launch(false)));
            items.Add(Command(MainCommand.Designer, "designer", () => Launch(true)));
            items.Add(new Separator());
            // A favorite shortcut as the original's node of «Избранное» (pbV8ContextItemPopup): in place of the favorite item
            // «Удалить из избранных» — here the command Delete of the toolbar and Del, with its caption; no «Переместить в группу».
            if (target!.FavoriteShortcut) items.Add(Command(MainCommand.Delete, "delete", Delete));
            else
            {
                items.Add(Command(MainCommand.ToggleFavorite, "star-add", ToggleFavorite, IsFavorite(entry) ? "Убрать из избранного" : null));
                items.Add(Command(MainCommand.MoveToGroup, "move", () => EditEntryLabel(true)));
            }
            items.Add(new Separator());
        }
        items.Add(Command(MainCommand.AddBase, "add", async () => await AddBase()));
        items.Add(Command(MainCommand.AddGroup, "folder-add", () => EditGroup(null)));
        if (entry is not null)
            items.Add(Command(MainCommand.DuplicateBase, "duplicate", DuplicateBase));
        if (entry is not null)
        {
            items.Add(new Separator());
            items.Add(Item("Создать ярлык на рабочем столе…", "shortcut", null, ready && own, CreateShortcut));
        }
        if (entry is not null || group)
        {
            items.Add(new Separator());
            // A group as the original: «Изменить наименование» before «Удалить».
            if (group)
            {
                items.Add(Command(MainCommand.Rename, "rename", () => EditEntryLabel(false)));
                // The original's «Открыть каталог инф. базы» works for a group too: the folder of its properties.
                items.Add(Command(MainCommand.OpenFolder, "folder", OpenSelectedBaseFolder));
                items.Add(new Separator());
            }
            // A favorite shortcut has its removal above, in place of the favorite item. A forbidden removal or a group that is not
            // empty is refused by the action with its reason in the status line.
            if (!target!.FavoriteShortcut)
                items.Add(Command(MainCommand.Delete, "delete", Delete, group ? "Удалить пустую группу" : null));
            if (entry is not null)
            {
                // As in the original: clearing the temporary files and the 1C folders of this base.
                items.Add(Command(MainCommand.ClearCache, "clear", () => Avalonia.Threading.Dispatcher.UIThread.Post(ClearBaseCache)));
                var folders = new MenuItem { Header = MenuHeader("Открыть каталог"), Icon = UiIcons.Create("folder", typography.IconSize), Classes = { "scaled" } };
                AutomationProperties.SetName(folders, "Открыть каталог");
                folders.ItemsSource = new[]
                {
                    Item("Каталог настроек пользователя (Roaming)", "folder", null, true, () => OpenBaseFolder(entry, BaseCacheKind.UserSettings)),
                    Item("Каталог программного кэша (Local)", "folder", null, true, () => OpenBaseFolder(entry, BaseCacheKind.Program))
                };
                items.Add(folders);
                var copy = new MenuItem { Header = MenuHeader("Копировать"), Icon = UiIcons.Create("copy", typography.IconSize), Classes = { "scaled" },
                    ItemsSource = CopyItems(entry) };
                AutomationProperties.SetName(copy, "Копировать"); items.Add(copy);
            }
            if (entry is not null)
            {
                items.Add(new Separator());
                items.Add(Command(MainCommand.Rename, "rename", () => EditEntryLabel(false)));
            }
            items.Add(new Separator());
            items.Add(Command(MainCommand.AssignVersion, "edit", AssignVersion));
            // As the original's pbV8ContextGroup: «Свойства группы» (actProperty) for a group too.
            items.Add(Command(MainCommand.Properties, "edit", EditSelected, group ? "Свойства группы…" : null));
            // As the original: «Сводная информация» after a separator at the end of the base's menu.
            if (entry is not null && target is { Orphan: null }) { items.Add(new Separator()); items.Add(Command(MainCommand.BaseInfo, "database", ShowBaseInfo)); }
        }
        if (target is { Orphan: not null } or { Section: LibrarySection.Orphans }) { items.Add(new Separator()); items.AddRange(OrphanMenu(target, state, Item)); }
        items.Add(new Separator());
        // As the original's actV8iReload: «Перечитать список баз», F5.
        items.Add(CommandItem(MainCommand.Reload, state, Reload, "refresh"));
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var maxWidth = screen is null ? 750 : screen.WorkingArea.Width / screen.Scaling * .94;
        var labelWidth = items.OfType<MenuItem>().Max(x => UiTypography.Measure(AutomationProperties.GetName(x) ?? "", typography.Family, typography.InterfaceSize).Width);
        menu.Resources["FlyoutThemeMaxWidth"] = maxWidth;
        menu.MaxWidth = maxWidth;
        // The menu fits the screen (placement slides, flips and shrinks it, see ClassicMenu); what does not fit scrolls.
        if (screen is not null) menu.MaxHeight = Math.Max(200, screen.WorkingArea.Height / screen.Scaling - 16);
        menu.Width = Math.Min(maxWidth, labelWidth + UiTypography.Measure("Shift+Ctrl+F8", typography.Family, typography.InterfaceSize).Width + typography.IconSize + 88);
        menu.ItemsSource = items;
    }

    // The original's «Копировать» (tbClipboard, pbClipboard): the name, ID, connection string and path of a base.
    private MenuItem[] CopyItems(BaseEntry entry, IEnumerable<BaseClipboardKind>? kinds = null) =>
        (kinds ?? BaseClipboard.Kinds(entry)).Select(kind =>
        {
            var label = BaseClipboard.Label(kind);
            var item = new MenuItem { Header = MenuHeader(label), Icon = UiIcons.Create("copy", typography.IconSize), Classes = { "scaled" } };
            AutomationProperties.SetName(item, label);
            item.Click += async (_, _) => { if (BaseClipboard.Text(entry, kind) is { } text) await CopyText(text); };
            return item;
        }).ToArray();

    private async Task CopyText(string text)
    {
        try
        {
            if (Clipboard is not { } clipboard) { status.Text = "Буфер обмена недоступен."; return; }
            await clipboard.SetTextAsync(text);
            status.Text = "Скопировано в буфер обмена: " + text;
        }
        catch (Exception ex) { status.Text = "Не удалось скопировать в буфер обмена: " + ex.Message; }
    }

    // The button beside the favorite star, as tbClipboard beside the original's connection line.
    private void ShowCopyMenu(Control anchor)
    {
        if (bases.SelectedItem is not LibraryRow { Entry: { } entry }) { status.Text = MainCommands.ChooseBaseText; return; }
        var menu = ClassicMenu("Копировать");
        menu.ItemsSource = CopyItems(entry);
        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    // The original's search types (pbSearchType) under the magnifier of the search field.
    private void ShowSearchTypeMenu(Control anchor)
    {
        var menu = ClassicMenu("Вид поиска");
        // The caption and the key of the search commands of the table (Ctrl+Shift+F is shown as the original shows it).
        MenuItem Item(SearchMode mode, MainCommand command, string icon)
        {
            var label = MainCommands.Caption(command); var gesture = MainCommands.KeyText(command) ?? "";
            var item = new MenuItem { Header = MenuHeader(label, gesture), Icon = UiIcons.Create(icon, typography.IconSize), Classes = { "scaled" },
                ToggleType = MenuItemToggleType.Radio, IsChecked = searchMode == mode, GroupName = "search" };
            AutomationProperties.SetName(item, label); AutomationProperties.SetAcceleratorKey(item, gesture);
            item.Click += (_, _) => { SetSearchMode(mode); search.Focus(); };
            return item;
        }
        menu.ItemsSource = new[]
        {
            Item(SearchMode.Name, MainCommand.SearchByName, "search"),
            Item(SearchMode.Connection, MainCommand.SearchByConnection, "search-connection")
        };
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        // The keyboard goes to the menu: the arrows and Enter choose the search type.
        menu.Opened += (_, _) => menu.Items.OfType<MenuItem>().FirstOrDefault(x => x.IsChecked)?.Focus(NavigationMethod.Directional);
        menu.Open(anchor);
    }

    private void SetSearchMode(SearchMode mode)
    {
        if (searchMode == mode) return;
        searchMode = mode; ApplySearchMode(); BuildRows();
    }

    private void ApplySearchMode()
    {
        var connectionSearch = searchMode == SearchMode.Connection;
        if (search.InnerLeftContent is Button type) type.Content = UiIcons.Create(connectionSearch ? "search-connection" : "search", typography.IconSize);
        search.PlaceholderText = connectionSearch ? "Соединение" : "Ctrl+F";
        ToolTip.SetTip(search, connectionSearch ? $"Поиск / отбор по строке соединения ({MainCommands.KeyText(MainCommand.SearchByConnection)})"
            : $"Поиск / отбор по наименованию ({MainCommands.KeyText(MainCommand.Search)})");
        AutomationProperties.SetName(search, connectionSearch ? "Поиск базы по строке соединения" : "Поиск базы");
    }

    // The original's header menu «Колонки»: the name always stays, the other columns can be hidden.
    private void AttachHeaderMenu()
    {
        var menu = ClassicMenu("Колонки");
        menu.Opening += (_, _) =>
        {
            var caption = new MenuItem { Header = MenuHeader("Колонки"), IsEnabled = false, Classes = { "scaled" } };
            var items = new List<Control> { caption, new Separator() };
            foreach (var (column, title) in ListTitles.Skip(1))
            {
                var item = new MenuItem { Header = MenuHeader(title), Classes = { "scaled" }, ToggleType = MenuItemToggleType.CheckBox, IsChecked = columns.Visible(column) };
                AutomationProperties.SetName(item, "Колонка " + title);
                item.Click += (_, _) => ChangeColumns(columns.Toggle(column));
                items.Add(item);
            }
            menu.ItemsSource = items;
        };
        listHeader.ContextMenu = menu;
    }

    // Right click on the connection line: its path and connection string, as the original's pbConnectMenu.
    // The original's connection link (lbConnect, pbConnectMenu): a click opens the folder of a file base; the menu also changes
    // the folder and copies the path and the connection string.
    private void AttachConnectionMenu()
    {
        var menu = ClassicMenu("Каталог и подключение");
        menu.Opening += (_, e) =>
        {
            // A group with a folder: the line shows the folder, the menu opens it.
            var state = CurrentState();
            if (bases.SelectedItem is LibraryRow { Orphan: null, Entry: null, Section: LibrarySection.None } && state.GroupFolder)
            { menu.ItemsSource = new List<Control> { CommandItem(MainCommand.OpenFolder, state, OpenSelectedBaseFolder, "folder") }; return; }
            if (bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) { e.Cancel = true; return; }
            var items = new List<Control>
            {
                CommandItem(MainCommand.OpenFolder, state, OpenSelectedBaseFolder, "folder"),
                CommandItem(MainCommand.ChangeFolder, state, ChangeSelectedBaseFolder, "edit"),
                new Separator()
            };
            items.AddRange(CopyItems(entry, BaseClipboard.Kinds(entry).Where(x => x is BaseClipboardKind.Path or BaseClipboardKind.Connection).Reverse()));
            menu.ItemsSource = items;
        };
        connection.ContextMenu = menu;
        connection.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && bases.SelectedItem is LibraryRow { Orphan: null } row &&
                (row.Entry?.Kind == ConnectionKind.File || row is { Entry: null, Section: LibrarySection.None } && Groups.Folder(library, row.Group).Length > 0))
            { e.Handled = true; OpenSelectedBaseFolder(); }
        };
    }

    // The original's «Открыть каталог инф. базы» (Shift+F12): the folder of the selected file base or of the selected group.
    private async void OpenSelectedBaseFolder()
    {
        string folder;
        if (bases.SelectedItem is LibraryRow { Orphan: null, Entry: null, Section: LibrarySection.None } group)
        {
            folder = Groups.Folder(library, group.Group);
            if (folder.Length == 0) { status.Text = GroupWithoutFolder(group.Group); return; }
        }
        else
        {
            if (bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) return;
            if (entry.Kind != ConnectionKind.File) { status.Text = MainCommands.FileFolderText; return; }
            folder = entry.Location;
        }
        try
        {
            // A folder on a network share may answer slowly: it is checked off the interface thread.
            if (!await Task.Run(() => Directory.Exists(folder))) { status.Text = $"Каталог не найден: {folder}"; return; }
            if (closed) return;
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) status.Text = $"Не удалось открыть каталог {folder}.";
        }
        catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); status.Text = $"Не удалось открыть каталог {folder}: {ex.Message}"; }
    }

    // The original's «Изменить каталог инф. базы» (Shift+F11): another folder for a file base, written to its connection.
    // As the original, the folder is not checked for the base files: a base may be moved there later.
    private async void ChangeSelectedBaseFolder()
    {
        if (loadFailed || reading || bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) return;
        if (entry.Kind != ConnectionKind.File) { status.Text = MainCommands.FileFolderChangeText; return; }
        if (ListLocked() || CommonLocked(entry)) return;
        try
        {
            var start = await StorageProvider.TryGetFolderFromPathAsync(entry.Location).ConfigureAwait(true);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог информационной базы", AllowMultiple = false, SuggestedStartLocation = start });
            if (closed || folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            path = Path.TrimEndingDirectorySeparator(path);
            // The list may have been read again or changed while the folder was chosen: the folder goes to the record as it is now.
            if (loadFailed || reading || ListLocked()) return;
            if (BaseEdit.WithFolder(library.Bases, entry.Id, path) is not { } updated) { status.Text = GoneMessage; return; }
            if (BaseEdit.SameFolder(path, library.Bases.First(x => x.Id == entry.Id).Location)) { status.Text = "Выбран тот же каталог базы."; return; }
            updated.Validate();
            if (await Persist(library with { Bases = library.Bases.Select(x => x.Id == entry.Id ? updated : x).ToList() }, ensureId: entry.Id))
            { Refresh(entry.Id); status.Text = "Каталог базы изменён: " + path; }
        }
        catch (Exception ex) { status.Text = "Каталог не изменён: " + LegacyImport.ErrorMessage(ex); }
    }

    private async void EditEntryLabel(bool move)
    {
        if (loadFailed || bases.SelectedItem is not LibraryRow { Section: LibrarySection.None, Orphan: null } row) return;
        // A group's dialog decides itself what a read-only list allows (its folder).
        if (row.Entry is null) { if (!move) EditGroup(row.Group); return; }
        if (ListLocked() || CommonLocked(row.Entry)) return;
        var entry = row.Entry;
        // The group choice follows FormGetGroup: a fixed-size window, «Выбрать» and «Отмена» above the tree, «Развернуть все» and
        // «Свернуть все» on the right, the tree under its column header «Группа». Typing a new path below the tree is our addition.
        var dialog = move
            ? new Window { Title = "Выбор группы", Width = 580, Height = 440, MinWidth = 450, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner }
            : new Window { Title = "Изменение наименования информационной базы", Width = 520, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var field = new TextBox { Text = move ? entry.Group : entry.Name };
        AutomationProperties.SetName(field, move ? "Группа базы" : "Наименование базы");
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(DialogLayout.Field(move ? "Группа:" : "Наименование:", field));
        TreeViewItem? selected = null;
        Control? tree = null;
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        if (move)
        {
            panel.Children.Add(DialogLayout.Label("Выберите группу в дереве или укажите путь. Пустое поле — вне групп."));
            Control Folder(TextBlock label)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var icon = UiIcons.Create("folder", typography.IconSize); icon.VerticalAlignment = VerticalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.NoWrap;
                header.Children.Add(icon); header.Children.Add(label); return header;
            }
            // As the original's tree: the root in bold capitals, as the main list shows it.
            var rootLabel = DialogLayout.Label("ИНФОРМАЦИОННЫЕ БАЗЫ"); rootLabel.FontWeight = FontWeight.Bold;
            var rootGroup = new TreeViewItem { Header = Folder(rootLabel), Tag = "", IsExpanded = true };
            AutomationProperties.SetName(rootGroup, "Информационные базы");
            nodes[""] = rootGroup;
            foreach (var path in Groups.All(library))
            {
                var parent = "";
                foreach (var part in path.Split('/'))
                {
                    var full = parent.Length == 0 ? part : parent + "/" + part;
                    if (!nodes.ContainsKey(full))
                    {
                        var node = new TreeViewItem { Header = Folder(DialogLayout.Label(part)), Tag = full };
                        AutomationProperties.SetName(node, part);
                        nodes[parent].Items.Add(node); nodes[full] = node;
                    }
                    parent = full;
                }
            }
            var groups = new TreeView { ItemsSource = new[] { rootGroup }, Background = UiColors.Field };
            AutomationProperties.SetName(groups, "Существующие группы");
            groups.SelectionChanged += (_, _) => { if (groups.SelectedItem is TreeViewItem { Tag: string path }) field.Text = path; };
            // As the original's Timer1Timer: the tree collapsed, only the way to the base's group open (Grok audit 13.10).
            if (nodes.TryGetValue(entry.Group, out selected))
            {
                for (var parent = Groups.Parent(entry.Group); parent.Length > 0; parent = Groups.Parent(parent))
                    if (nodes.TryGetValue(parent, out var way)) way.IsExpanded = true;
                groups.SelectedItem = selected;
            }
            var header = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = UiColors.Surface,
                Child = new TextBlock { Text = "Группа", Margin = new Thickness(5, 3), FontWeight = FontWeight.SemiBold, Foreground = UiColors.Muted,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis } };
            var box = new DockPanel(); DockPanel.SetDock(header, Dock.Top); box.Children.Add(header); box.Children.Add(groups);
            // The field and an error below may grow with a large font: a few rows of the tree always stay.
            tree = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Background = UiColors.Field, Child = box, MinHeight = 110 };
        }
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        // A message about the previous choice goes when another one is made.
        if (move) field.TextChanged += (_, _) => error.Text = "";
        var buttons = new WrapPanel();
        var saving = false;
        dialog.Closing += (_, e) => { if (saving) e.Cancel = true; };
        var save = SettingsWindow.Command(move ? "Выбрать" : "ОК", async () =>
        {
            if (saving) return;
            saving = true; panel.IsEnabled = buttons.IsEnabled = false;
            try
            {
                // The base's own group is refused as the original's btnSelectClick does: nothing to write, the window stays.
                if (move && Groups.Normalize(field.Text ?? "").Equals(entry.Group, StringComparison.OrdinalIgnoreCase))
                { error.Text = "Элемент уже принадлежит выбранной группе!"; return; }
                var edited = move ? entry with { Group = Groups.Normalize(field.Text ?? "") } : entry with { Name = field.Text?.Trim() ?? "" };
                if (OnCurrent(entry, edited) is not { } updated) { error.Text = GoneMessage; return; }
                updated.Validate();
                if (await Persist(library with { Bases = library.Bases.Select(x => x.Id == entry.Id ? updated : x).ToList() }, ensureId: entry.Id))
                { saving = false; search.Text = ""; collapsed.Clear(); collapsedSections.Clear(); Refresh(entry.Id); dialog.Close(); }
                else error.Text = status.Text;
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
            finally { saving = false; panel.IsEnabled = buttons.IsEnabled = true; }
        });
        save.IsDefault = true;
        var cancel = SettingsWindow.Command("Отмена", dialog.Close); cancel.IsCancel = true;
        // Renaming follows the group dialog (Отмена, ОК); the group choice keeps the original's order.
        if (move && tree is not null)
        {
            // «Выбрать» of the original: bold, with the arrow.
            var choose = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            choose.Children.Add(UiIcons.Create("way", typography.IconSize));
            choose.Children.Add(new TextBlock { Text = "Выбрать", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
            save.Content = choose; save.MinWidth = 110; cancel.MinWidth = 90;
            buttons.Children.Add(save); buttons.Children.Add(cancel);
            Button Tool(string icon, string label, bool expand)
            {
                var button = new Button { Content = UiIcons.Create(icon, typography.IconSize), Padding = new Thickness(3), MinHeight = 0,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                button.Width = button.Height = Math.Max(26, typography.IconSize + 10);
                ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label);
                // Collapsing keeps the root open: collapsed, it would leave a single line. Expanding opens it too.
                button.Click += (_, _) => { foreach (var (path, node) in nodes) if (expand || path.Length > 0) node.IsExpanded = expand; };
                return button;
            }
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            tools.Children.Add(Tool("expand-all", "Развернуть все", true)); tools.Children.Add(Tool("collapse-all", "Свернуть все", false));
            // The buttons first, as the original's TabOrder: «Выбрать», «Отмена», then the tools.
            var top = new Grid { ColumnDefinitions = new("*,Auto") };
            top.Children.Add(buttons); Grid.SetColumn(tools, 1); top.Children.Add(tools);
            var messages = new ScrollViewer { Content = error, MaxHeight = 90, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
            error.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) messages.IsVisible = !string.IsNullOrEmpty(error.Text); };
            // Enter chooses the group, as the original's default «Выбрать»: the tree would only open or close the node.
            tree.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
                e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, RoutingStrategies.Tunnel);
            var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 8 };
            root.Children.Add(top);
            Grid.SetRow(tree, 1); root.Children.Add(tree);
            Grid.SetRow(panel, 2); root.Children.Add(panel);
            Grid.SetRow(messages, 3); root.Children.Add(messages);
            dialog.Content = root;
        }
        else
        {
            buttons.Children.Add(cancel); buttons.Children.Add(save);
            dialog.Content = DialogLayout.Frame(panel, buttons, error);
        }
        typography.ApplyDialog(dialog);
        // The minimum grows with the font as the window does, so the tree keeps its rows under a stacked field.
        if (move) dialog.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        dialog.Opened += (_, _) =>
        {
            // The group choice starts in the tree on the base's group, as the original; renaming starts in the name.
            if (selected is not null) { selected.BringIntoView(); selected.Focus(); }
            else { field.Focus(); field.SelectAll(); }
        };
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
}
