using System.Data.Common;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

/// <param name="Entry">A created base (already on disk), or the connection to attach (to be completed in the properties).</param>
internal sealed record NewBaseResult(BaseEntry Entry, bool Created);

/// <summary>The original's «Размещение информационной базы» (FormSelectNew): a panel that drops from the «+» button with the place —
/// this computer, a server, a web server — as three tiles and a button for each platform to create the base for.</summary>
internal static class NewBasePlacement
{
    public static Task<(ConnectionKind Kind, string Family)?> Show(Control anchor, UiTypography typography, IReadOnlyList<string> families)
    {
        var done = new TaskCompletionSource<(ConnectionKind, string)?>();
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
        var kind = ConnectionKind.File;
        (ConnectionKind, string)? choice = null;
        var places = new[] { ("Компьютер", "place-computer", ConnectionKind.File), ("Сервер", "place-server", ConnectionKind.Server), ("Web-сервер", "place-web", ConnectionKind.Web) };
        var tiles = new List<(ToggleButton Tile, ConnectionKind Kind)>();
        void Mark() { foreach (var (tile, value) in tiles) { tile.IsChecked = value == kind; tile.BorderBrush = value == kind ? UiColors.SectionText : UiColors.Line; } }
        // The tiles wrap under a large font rather than widen the panel past the screen.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = 10, LineSpacing = 10 };
        foreach (var (text, icon, value) in places)
        {
            var face = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(new Border { Child = UiIcons.Create(icon, typography.DialogSize * 2.4), HorizontalAlignment = HorizontalAlignment.Center });
            face.Children.Add(new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center });
            var tile = new ToggleButton { Content = face, MinWidth = typography.DialogSize * 6, Padding = new Thickness(8, 8, 8, 6), BorderThickness = new Thickness(2),
                Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(tile, text);
            // A tile is chosen, never unchosen: clicking the chosen one keeps it.
            tile.Click += (_, _) => { kind = value; Mark(); };
            tiles.Add((tile, value)); row.Children.Add(tile);
        }
        Mark();
        var body = new StackPanel { Spacing = 10, Width = Math.Clamp(typography.DialogSize * 23, 300, 420) };
        body.Children.Add(DialogLayout.Section("Размещение информационной базы", row));
        var platforms = new StackPanel { Spacing = 6 };
        foreach (var family in families)
        {
            // The panel closes first and hands the choice on when it has closed: the next window never opens under a panel still shown.
            var button = SettingsWindow.Command("1С:Предприятие " + family, () => { choice = (kind, family); flyout.Hide(); });
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; button.Margin = new Thickness(0);
            platforms.Children.Add(button);
        }
        body.Children.Add(DialogLayout.Section("Создать для платформы", platforms));
        var cancel = SettingsWindow.Command("Отмена", () => flyout.Hide());
        cancel.HorizontalAlignment = HorizontalAlignment.Center; cancel.Margin = new Thickness(0); cancel.MinWidth = 90;
        body.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = cancel });
        flyout.Content = new Border { Background = UiColors.Surface, Padding = new Thickness(4), Child = body };
        body.SetValue(TextElement.FontFamilyProperty, typography.DialogFamily); body.SetValue(TextElement.FontSizeProperty, typography.DialogSize);
        body.SetValue(TextElement.ForegroundProperty, UiColors.Text);
        // Escape, a click outside, «Отмена»: closed with nothing chosen.
        flyout.Closed += (_, _) => done.TrySetResult(choice);
        flyout.Opened += (_, _) => tiles[0].Tile.Focus();
        flyout.ShowAt(anchor);
        return done.Task;
    }
}

/// <summary>The original's «Создание информационной базы» (FormNewBase): the place and the version, then one of four ways — connect an
/// existing base, make an empty one, make one from an installed template, make one and load a dump or a configuration. The base is made
/// by the platform itself (<see cref="InfobaseCreation"/>); nothing is added to the list until the platform says it is done.</summary>
internal static class NewBaseWindow
{
    private enum Page { Ways, Template, Load }

    public static async Task<NewBaseResult?> Show(Window owner, UiTypography typography, Library library, ConnectionKind kind, string family,
        IReadOnlyList<PlatformInstallation> installations, string group)
    {
        var onServer = kind == ConnectionKind.Server;
        var dialog = new Window { Title = $"1С:Предприятие {family}", Width = 660, Height = 560, MinWidth = 420, MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var page = Page.Ways;
        var running = false;
        CancellationTokenSource? cancelRun = null;
        var state = new NewBaseConnection(kind);

        var heading = new TextBlock { Text = onServer ? "Создание информационной базы на сервере" : "Создание информационной базы локально",
            FontWeight = FontWeight.SemiBold, Foreground = UiColors.SectionText, FontSize = typography.DialogSize + 3, TextWrapping = TextWrapping.Wrap };
        var location = new TextBox { PlaceholderText = onServer ? "сервер\\имя_базы" : (OperatingSystem.IsWindows() ? @"D:\Базы\Новая" : "/home/user/bases/new") };
        AutomationProperties.SetName(location, "Размещение информационной базы");
        // The folder of the group (its own or its parent's, the original's GetParentFolder), else «Единый каталог информационных баз» of the
        // settings: a new file base is proposed there; the name of the base is added by hand.
        var proposed = Groups.InheritedFolder(library, group) is { Length: > 0 } groupFolder ? groupFolder : library.BasesFolder;
        if (!onServer && proposed.Length > 0) location.Text = Path.EndsInDirectorySeparator(proposed) ? proposed : proposed + Path.DirectorySeparatorChar;
        var families = installations.Select(x => $"{x.Version.Major}.{x.Version.Minor}").Append(family).Distinct()
            .OrderByDescending(x => Version.Parse(x)).ToList();
        var version = new ComboBox { ItemsSource = families, SelectedItem = family, MinWidth = 90 };
        AutomationProperties.SetName(version, "Версия платформы");
        // The string is shown as it goes to 1C (values in single quotes); it is read-only until the pencil opens it (a server base only:
        // the DBMS and the database are written there).
        var connection = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
        AutomationProperties.SetName(connection, "Строка соединения");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Error };

        // The fields show the state; their own writes are not taken for the user's edits. TextChanged comes asynchronously, after the
        // write (Avalonia 12 docs; a flag set around the write does not hold then), so a text equal to the state is not an edit.
        void Show()
        {
            if (location.Text != state.Location) location.Text = state.Location;
            connection.Text = state.Text; connection.IsReadOnly = !state.Editable;
        }
        const string remade = "Строка соединения сформирована заново по полю «Сервер и имя базы в кластере».";
        location.TextChanged += (_, _) =>
        {
            if ((location.Text ?? "") == state.Location) return;
            var dropped = state.SetLocation(location.Text ?? "");
            Show();
            // The notice of a dropped string stays while the place is typed; other messages go.
            if (dropped) message.Text = remade; else if (message.Text != remade) message.Text = "";
        };
        connection.TextChanged += (_, _) => { if ((connection.Text ?? "") != state.Text) state.EditText(connection.Text ?? ""); };
        state.SetLocation(location.Text ?? ""); Show();

        // As the original: the caption above the field and «…» inside it, the version to the right in the same row.
        Button Small(string icon, string name, Action action)
        {
            var button = new Button { Content = icon.Length > 0 ? UiIcons.Create(icon, typography.DialogSize) : new TextBlock { Text = "…", FontWeight = FontWeight.Bold },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name); button.Click += (_, _) => action(); return button;
        }
        if (!onServer)
            location.InnerRightContent = Small("", "Выбрать каталог…", async () =>
            {
                try
                {
                    var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог новой информационной базы", AllowMultiple = false });
                    if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) location.Text = path;
                }
                catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); }
            });
        StackPanel Above(string caption, Control field) { var column = new StackPanel { Spacing = 3 }; column.Children.Add(DialogLayout.Label(caption)); column.Children.Add(field); return column; }
        var placeRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        placeRow.Children.Add(Above(onServer ? "Сервер и имя базы в кластере:" : "Размещение информационной базы:", location));
        var versionColumn = Above("Версия:", version); Grid.SetColumn(versionColumn, 1); placeRow.Children.Add(versionColumn);
        var stringRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 4 };
        var edit = Small("pencil", "Изменить строку соединения", () => { state.BeginEdit(); connection.IsReadOnly = !state.Editable; connection.Focus(); });
        edit.IsVisible = onServer; stringRow.Children.Add(edit);
        Grid.SetColumn(connection, 1); stringRow.Children.Add(connection);
        // As the original: «Вставить» takes a connection string from the clipboard into the place (the string itself stays selectable for
        // Ctrl+C). A server string with other parameters (the DBMS, the database…) is kept whole as an edited string, as after the pencil.
        var paste = Small("paste", "Вставить строку соединения", async () =>
        {
            PastedConnection pasted;
            try
            {
                if (TopLevel.GetTopLevel(dialog)?.Clipboard is not { } clipboard) { message.Text = "Буфер обмена недоступен."; return; }
                pasted = BaseClipboard.Paste(await clipboard.TryGetTextAsync(), kind);
            }
            catch (ArgumentException ex) { message.Text = ex.Message; return; }
            catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); return; }
            state.Paste(pasted); Show();
            message.Text = pasted.Notice ?? "";
        });
        Grid.SetColumn(paste, 2); stringRow.Children.Add(paste);
        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(heading);
        top.Children.Add(placeRow);
        top.Children.Add(stringRow);

        // ---- the pages
        var content = new ContentControl();
        var ways = new StackPanel { Spacing = 8 };
        var templatePage = new StackPanel { Spacing = 8 };
        var loadPage = new StackPanel { Spacing = 8 };

        // A button with an icon before its caption; its accessible name is the caption.
        StackPanel Face(string icon, string text)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            face.Children.Add(UiIcons.Create(icon, typography.DialogSize + 2)); face.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            return face;
        }
        // The four ways of the original: large buttons with an arrow, in a frame.
        Button Way(string text, Action action)
        {
            var b = SettingsWindow.Command(text, action); b.Content = Face("way", text); b.FontSize = typography.DialogSize + 1;
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0);
            b.Padding = new Thickness(16, 9); return b;
        }

        // Template page: the installed templates as vendor → solution → version.
        var tree = new TreeView { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), MinHeight = 170 };
        AutomationProperties.SetName(tree, "Установленные шаблоны");
        var templateNote = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };
        var templates = Array.Empty<InfobaseTemplate>() as IReadOnlyList<InfobaseTemplate>;
        void ShowTemplates(TemplateScan scan)
        {
            templates = scan.Templates;
            // The accessible name of an item is its caption (a string header is not announced by itself).
            static TreeViewItem Item(string header, object? tag = null) { var item = new TreeViewItem { Header = header, Tag = tag, IsExpanded = true }; AutomationProperties.SetName(item, header); return item; }
            // A version shows the platform its manifest names, grey, as the original: «3.1.12.205 (8.3)».
            static TreeViewItem Leaf(InfobaseTemplate template)
            {
                var item = Item(template.Label, template);
                if (template.Platform.Length == 0) return item;
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                header.Children.Add(new TextBlock { Text = template.Label });
                header.Children.Add(new TextBlock { Text = $"({template.Platform})", Foreground = UiColors.Muted });
                item.Header = header; AutomationProperties.SetName(item, $"{template.Label} ({template.Platform})"); return item;
            }
            var vendors = templates.GroupBy(x => x.Vendor).Select(vendor =>
            {
                var solutions = vendor.GroupBy(x => x.Solution).Select(solution =>
                { var item = Item(solution.Key); item.ItemsSource = solution.Select(Leaf).ToList(); return item; }).ToList();
                var group = Item(vendor.Key); group.ItemsSource = solutions; return group;
            }).ToList();
            tree.ItemsSource = vendors;
            templateNote.Text = (templates.Count == 0 ? "Установленные шаблоны не найдены. " : "") + (scan.Truncated ? "Показаны не все шаблоны: поиск остановлен по пределу. " : "") +
                "Каталоги шаблонов: " + string.Join("; ", TemplateCatalog.Roots()) + ".";
        }
        templatePage.Children.Add(DialogLayout.Label("Выберите поставляемую конфигурацию или демонстрационный пример для ознакомления"));
        templatePage.Children.Add(tree); templatePage.Children.Add(templateNote);

        // Load page: a dump, a configuration, or nothing.
        TextBox fileBox(string name) { var box = new TextBox(); AutomationProperties.SetName(box, name); return box; }
        var dumpBox = fileBox("Файл выгрузки информационной базы"); var configurationBox = fileBox("Файл конфигурации");
        var loadDump = new RadioButton { Content = DialogLayout.Label("Загрузить информационную базу (*.dt)"), GroupName = "new-base-load", IsChecked = true };
        var loadConfiguration = new RadioButton { Content = DialogLayout.Label("Загрузить конфигурацию (*.cf)"), GroupName = "new-base-load" };
        var loadNothing = new RadioButton { Content = DialogLayout.Label("Создать пустую информационную базу и ничего не загружать"), GroupName = "new-base-load" };
        Control Pick(TextBox box, string title, string pattern)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(24, 0, 0, 0) };
            row.Children.Add(box);
            var button = SettingsWindow.Command("Выбрать…", async () =>
            {
                try
                {
                    var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }, FilePickerFileTypes.All] });
                    if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) box.Text = path;
                }
                catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); }
            });
            button.Margin = new Thickness(0); Grid.SetColumn(button, 1); row.Children.Add(button); return row;
        }
        loadPage.Children.Add(loadDump); loadPage.Children.Add(Pick(dumpBox, "Выгрузка данных (*.dt)", "*.dt"));
        loadPage.Children.Add(loadConfiguration); loadPage.Children.Add(Pick(configurationBox, "Выгрузка конфигурации (*.cf)", "*.cf"));
        loadPage.Children.Add(loadNothing);
        loadDump.IsCheckedChanged += (_, _) => message.Text = ""; loadConfiguration.IsCheckedChanged += (_, _) => message.Text = ""; loadNothing.IsCheckedChanged += (_, _) => message.Text = "";

        var status = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };
        var create = SettingsWindow.Command("Создать", () => { });
        var cancel = SettingsWindow.Command("Отмена", () => { });
        cancel.IsCancel = true; create.IsDefault = true; create.FontWeight = FontWeight.Bold; create.Content = Face("way", "Создать");
        // The accessible name follows the caption: the same button cancels, goes back or interrupts a run.
        void SetCancel(string text) { cancel.Content = Face(text == "Назад" ? "back" : "close", text); AutomationProperties.SetName(cancel, text); }
        // As the original, «Создать» waits for a template to be chosen on the template page.
        void UpdateCreate() => create.IsEnabled = !running && (page != Page.Template || (tree.SelectedItem as TreeViewItem)?.Tag is InfobaseTemplate);
        tree.SelectionChanged += (_, _) => { UpdateCreate(); message.Text = ""; };
        var waysFrame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(14, 12), Child = ways };
        // The message of a failed run stays when the controls come back (keepMessage); a page change clears it.
        void ShowPage(Page next, bool keepMessage = false)
        {
            page = next; if (!keepMessage) message.Text = "";
            content.Content = next switch { Page.Template => templatePage, Page.Load => loadPage, _ => waysFrame };
            create.IsVisible = next != Page.Ways;
            SetCancel(next == Page.Ways ? "Отмена" : "Назад");
            UpdateCreate();
        }

        // ---- what the platform is asked for
        string? ClientOf()
        {
            try
            {
                var probe = new BaseEntry { Name = "new", Kind = kind, Location = "x", PlatformRule = new PlatformRule { Version = version.SelectedItem as string ?? family } };
                return PlatformCatalog.Resolve(library, probe, installations, designer: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { message.Text = ex.Message; return null; }
        }
        NewBaseRequest? Request(NewBaseSource source, string file)
        {
            if (ClientOf() is not { } client) return null;
            var (place, connectionString) = state.ForRequest();
            return new NewBaseRequest { Client = client, Kind = kind, Source = source, SourceFile = file, Location = place, ConnectionString = connectionString };
        }
        string NameOf(NewBaseRequest request)
        {
            // The name of the folder, or of the base in the cluster (the original names a new base so); the target is the one the platform got.
            var target = InfobaseCreation.ToEntry(request, "x", "", "").Location;
            var raw = request.Kind == ConnectionKind.File ? Path.GetFileName(Path.TrimEndingDirectorySeparator(target)) : target[(target.IndexOf('\\') + 1)..];
            var name = raw.Length > 0 ? raw : "Новая информационная база";
            var taken = library.Bases.Select(x => x.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var candidate = name; var n = 1;
            while (taken.Contains(candidate)) candidate = name + "_" + n++;
            return candidate;
        }

        async Task Make(NewBaseRequest request)
        {
            running = true; message.Text = ""; status.Text = "Подготовка…";
            content.IsEnabled = top.IsEnabled = create.IsEnabled = false; SetCancel("Прервать");
            cancelRun = new CancellationTokenSource();
            try
            {
                var plan = InfobaseCreation.Plan(request);
                var result = await InfobaseCreation.Run(plan, text => status.Text = text, cancel: cancelRun.Token);
                if (result.Success)
                {
                    var entry = InfobaseCreation.ToEntry(request, NameOf(request), group, version.SelectedItem as string ?? family);
                    running = false; dialog.Close(new NewBaseResult(entry, true)); return;
                }
                message.Text = result.Message; status.Text = "";
            }
            catch (ArgumentException ex) { message.Text = ex.Message; status.Text = ""; }
            catch (Exception ex) { message.Text = Launcher.ErrorMessage(ex); status.Text = ""; }
            finally
            {
                if (running) { running = false; content.IsEnabled = top.IsEnabled = create.IsEnabled = true; ShowPage(page, keepMessage: true); }
                cancelRun?.Dispose(); cancelRun = null;
            }
        }
        async void Create()
        {
            if (running) return;
            switch (page)
            {
                case Page.Template:
                    if ((tree.SelectedItem as TreeViewItem)?.Tag is not InfobaseTemplate template) { message.Text = "Выберите шаблон: версию решения в списке."; return; }
                    if (Request(NewBaseSource.Template, template.File) is { } t) await Make(t);
                    break;
                case Page.Load:
                    var source = loadDump.IsChecked == true ? NewBaseSource.LoadDump : loadConfiguration.IsChecked == true ? NewBaseSource.LoadConfiguration : NewBaseSource.Empty;
                    var file = source == NewBaseSource.LoadDump ? dumpBox.Text?.Trim() ?? "" : source == NewBaseSource.LoadConfiguration ? configurationBox.Text?.Trim() ?? "" : "";
                    if (Request(source, file) is { } l) await Make(l);
                    break;
            }
        }
        create.Click += (_, _) => Create();
        cancel.Click += (_, _) =>
        {
            if (running) { cancelRun?.Cancel(); return; }
            if (page != Page.Ways) ShowPage(Page.Ways); else dialog.Close(null);
        };

        // 1. connect an existing base: no creation; the properties window finishes it
        ways.Children.Add(Way("1. Подключить существующую информационную базу", () =>
        {
            string text;
            try { text = state.ConnectTarget(); }
            catch (ArgumentException ex) { message.Text = ex.Message; return; }
            if (onServer && text.IndexOf('\\') is var split && (split <= 0 || split == text.Length - 1)) { message.Text = "Укажите сервер и имя базы в кластере: сервер\\имя."; return; }
            if (text.Length == 0) { message.Text = "Укажите размещение информационной базы."; return; }
            dialog.Close(new NewBaseResult(new BaseEntry { Name = "", Group = group, Kind = kind, Location = text, PlatformVersion = version.SelectedItem as string ?? family }, false));
        }));
        // 2. an empty one
        ways.Children.Add(Way("2. Создать пустую информационную базу", async () => { if (!running && Request(NewBaseSource.Empty, "") is { } r) await Make(r); }));
        // 3. from a template
        ways.Children.Add(Way("3. Создать информационную базу из шаблона", async () =>
        {
            if (running) return;
            // The search is read-only and bounded, but a slow folder must not freeze the window: it runs apart and can be interrupted.
            running = true; message.Text = ""; status.Text = "Поиск установленных шаблонов…";
            content.IsEnabled = top.IsEnabled = create.IsEnabled = false; SetCancel("Прервать");
            cancelRun = new CancellationTokenSource(); var token = cancelRun.Token;
            TemplateScan? scan = null;
            try { scan = await Task.Run(() => TemplateCatalog.ScanAll(cancel: token), token); }
            catch (OperationCanceledException) { message.Text = "Поиск шаблонов прерван."; }
            catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); }
            finally
            {
                running = false; cancelRun?.Dispose(); cancelRun = null; status.Text = "";
                content.IsEnabled = top.IsEnabled = create.IsEnabled = true;
            }
            if (scan is null) { ShowPage(Page.Ways, keepMessage: true); return; }
            ShowTemplates(scan);
            if (scan.Templates.Count == 0) { ShowPage(Page.Ways, keepMessage: true); message.Text = templateNote.Text; return; }
            ShowPage(Page.Template);
        }));
        // 4. create and load
        ways.Children.Add(Way("4. Создать и загрузить информационную базу", () => ShowPage(Page.Load)));

        var buttons = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        buttons.Children.Add(cancel); Grid.SetColumn(create, 2); buttons.Children.Add(create);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(top); body.Children.Add(content);
        var footer = new StackPanel { Spacing = 4 };
        footer.Children.Add(status); footer.Children.Add(message);
        body.Children.Add(footer);
        // The frame cannot take a Grid of buttons as a Panel of buttons only; it takes any Panel.
        dialog.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(dialog);
        ShowPage(Page.Ways);
        dialog.Closing += (_, e) => { if (running) { e.Cancel = true; cancelRun?.Cancel(); } };
        dialog.Opened += (_, _) => location.Focus();
        return await dialog.ShowDialog<NewBaseResult?>(owner);
    }
}
