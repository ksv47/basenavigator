using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

// The original's «Найти информационную базу в списке ▸» and «Поиск информационных баз на компьютере» (FormSearchBases).
public sealed partial class MainWindow
{
    private MenuItem FindInListMenu()
    {
        MenuItem Item(string label, System.Action action)
        {
            var item = new MenuItem { Header = MenuHeader(label), Classes = { "scaled" }, IsEnabled = !loadFailed && !closed };
            AutomationProperties.SetName(item, label); item.Click += (_, _) => action(); return item;
        }
        var menu = new MenuItem { Header = MenuHeader("Найти информационную базу в списке"), Classes = { "scaled" }, ItemsSource = new[]
        {
            Item("По идентификатору (ID)…", FindById),
            Item("По вхождению в строке соединения", () => { SetSearchMode(SearchMode.Connection); search.Focus(); search.SelectAll(); })
        } };
        AutomationProperties.SetName(menu, "Найти информационную базу в списке");
        return menu;
    }

    private static readonly Regex GuidText = new(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant);

    // Any text with an ID is accepted: the ID itself, a line of .v8i or the path of a cache folder named by it.
    private async void FindById()
    {
        var dialog = new Window { Title = "Найти информационную базу по ID", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var field = new TextBox { PlaceholderText = "00000000-0000-0000-0000-000000000000" };
        AutomationProperties.SetName(field, "Идентификатор базы");
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(DialogLayout.Field("ID:", field));
        panel.Children.Add(DialogLayout.Label("Можно вставить строку списка .v8i или путь каталога кэша 1С: из текста берётся первый идентификатор."));
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        BaseEntry? found = null;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var cancel = Action("Отмена", () => dialog.Close()); cancel.IsCancel = true;
        var find = Action("Найти", () =>
        {
            var match = GuidText.Match(field.Text ?? "");
            if (!match.Success) { error.Text = "В тексте нет идентификатора."; return; }
            var id = Guid.Parse(match.Value);
            found = ShownBases().FirstOrDefault(x => x.Id == id);
            if (found is null) { error.Text = $"В этом списке нет базы с ID {id}."; return; }
            dialog.Close();
        });
        find.IsDefault = true;
        buttons.Children.Add(cancel); buttons.Children.Add(find);
        dialog.Content = DialogLayout.Frame(panel, buttons, error); typography.ApplyDialog(dialog);
        dialog.Opened += (_, _) => field.Focus();
        await dialog.ShowDialog(this);
        if (found is null || closed) { RestoreListFocus(); return; }
        // The base is shown wherever it is: the search is cleared and its groups are opened.
        search.Text = ""; collapsedSections.Remove(LibrarySection.All);
        foreach (var group in collapsed.Where(x => Groups.Contains(x, found.Group)).ToList()) collapsed.Remove(group);
        BuildRows(found.Id.ToString());
        bases.ScrollIntoView(bases.SelectedItem!);
        RestoreListFocus();
        status.Text = $"Найдена база: {found.Name}.";
    }

    private sealed class FoundRow(FoundBase found)
    {
        public FoundBase Found { get; } = found;
        public bool Checked { get; set; } = true;
    }

    private async void SearchBasesOnComputer()
    {
        if (loadFailed || reading || closed || ListLocked()) return;
        var dialog = new Window { Title = "Поиск информационных баз", Width = 760, Height = 560, MinWidth = 420, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var roots = new ObservableCollection<string>(BaseFinder.DefaultRoots());
        var results = new ObservableCollection<FoundRow>();
        CancellationTokenSource? running = null;
        var adding = false;

        // «Пути поиска»
        var paths = new ListBox { ItemsSource = roots, Height = 110, Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(paths, "Пути поиска");
        var addPath = Action("Добавить…", async () =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка для поиска", AllowMultiple = true });
            foreach (var folder in folders) if (folder.TryGetLocalPath() is { } path && !roots.Contains(path)) roots.Add(path);
        });
        var removePath = Action("Удалить", () => { if (paths.SelectedItem is string path) roots.Remove(path); });
        var pathButtons = new StackPanel { Spacing = 6 }; pathButtons.Children.Add(addPath); pathButtons.Children.Add(removePath);
        var pathRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        pathRow.Children.Add(paths); Grid.SetColumn(pathButtons, 1); pathRow.Children.Add(pathButtons);

        // «Правила добавления»
        var usePrefix = new CheckBox { Content = "Префикс:", IsChecked = true };
        var prefix = new TextBox { Text = "new_", Width = 120 };
        var addDate = new CheckBox { Content = "Дату изменения", IsChecked = true };
        var names = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { usePrefix, prefix, addDate })
        { control.Margin = new Thickness(0, 0, 10, 4); control.VerticalAlignment = VerticalAlignment.Center; names.Children.Add(control); }
        // As the original: «1С:Предприятие 8.x»; 8.5 is ours (platform-folders-20261010).
        var version = new ComboBox { ItemsSource = new[] { "8.3", "8.5", "8.2", "8.1" }.Select(x => "1С:Предприятие " + x).ToList(), SelectedIndex = 0 };
        AutomationProperties.SetName(version, "Версия платформы новых баз");
        // As the original (rgGroup.ItemIndex = 0): «Создать новую группу» is chosen, its name is typed by the user.
        var newGroup = new RadioButton { Content = "Создать новую группу:", GroupName = "found-group", IsChecked = true };
        var groupName = new TextBox { PlaceholderText = "Введите название группы" };
        var existing = new RadioButton { Content = "Добавить в существующую:", GroupName = "found-group" };
        var groupChoice = new ComboBox { ItemsSource = new[] { "/" }.Concat(Groups.All(library)).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var selectedGroup = (bases.SelectedItem as LibraryRow)?.Group;
        if (!string.IsNullOrEmpty(selectedGroup)) groupChoice.SelectedItem = selectedGroup;
        AutomationProperties.SetName(groupName, "Название новой группы"); AutomationProperties.SetName(groupChoice, "Существующая группа");
        var rules = new StackPanel { Spacing = 8 };
        // The original's order: the group, then the version, then «Добавить к имени информационной базы».
        rules.Children.Add(DialogLayout.Label("Группа размещения подключаемых информационных баз:"));
        rules.Children.Add(newGroup); rules.Children.Add(groupName);
        rules.Children.Add(existing); rules.Children.Add(groupChoice);
        rules.Children.Add(DialogLayout.Field("Версия:", version));
        rules.Children.Add(DialogLayout.Label("Добавить к имени информационной базы"));
        rules.Children.Add(names);

        // Fluent draws tab headers in a large font; here they follow the dialog font, as the original's page tabs.
        TabItem Tab(string header, Control content) => new() { Header = header, Content = content, FontSize = typography.DialogSize, MinHeight = 0, Padding = new Thickness(8, 4) };
        // «Правила поиска», as the original: the date of the last change and the size of the file.
        var anyDate = new RadioButton { Content = "Любая дата", GroupName = "found-date", IsChecked = true };
        var notLater = new RadioButton { Content = "Не позднее:", GroupName = "found-date" };
        var date = new CalendarDatePicker { SelectedDate = DateTime.Today, IsEnabled = false, SelectedDateFormat = CalendarDatePickerFormat.Short };
        AutomationProperties.SetName(date, "Дата изменения не позднее");
        notLater.IsCheckedChanged += (_, _) => date.IsEnabled = notLater.IsChecked == true;
        var anySize = new RadioButton { Content = "Любой размер", GroupName = "found-size", IsChecked = true };
        var givenSize = new RadioButton { Content = "Указанный размер:", GroupName = "found-size" };
        var condition = new ComboBox { ItemsSource = new[] { "Больше чем", "Меньше чем", "Равен" }, SelectedIndex = 0, IsEnabled = false };
        var megabytes = new NumericUpDown { Minimum = 0, Maximum = 10_000_000, Increment = 10, Value = 0, FormatString = "0", IsEnabled = false, MinWidth = 120 };
        AutomationProperties.SetName(condition, "Условие размера"); AutomationProperties.SetName(megabytes, "Размер, МБ");
        givenSize.IsCheckedChanged += (_, _) => condition.IsEnabled = megabytes.IsEnabled = givenSize.IsChecked == true;
        var dateBox = new StackPanel { Spacing = 6 }; dateBox.Children.Add(anyDate);
        var dateRow = new WrapPanel { Orientation = Orientation.Horizontal }; notLater.Margin = new Thickness(0, 0, 8, 0); dateRow.Children.Add(notLater); dateRow.Children.Add(date); dateBox.Children.Add(dateRow);
        var sizeBox = new StackPanel { Spacing = 6 }; sizeBox.Children.Add(anySize);
        var sizeRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { givenSize, condition, megabytes, DialogLayout.Label("МБ") }) { control.Margin = new Thickness(0, 0, 8, 4); control.VerticalAlignment = VerticalAlignment.Center; sizeRow.Children.Add(control); }
        sizeBox.Children.Add(sizeRow);
        var searchRules = new StackPanel { Spacing = 12 };
        searchRules.Children.Add(DialogLayout.Section("Дата изменения файла", dateBox));
        searchRules.Children.Add(DialogLayout.Section("Размер файла", sizeBox));
        FoundFilter Filter() => new(notLater.IsChecked == true ? date.SelectedDate : null,
            givenSize.IsChecked == true ? (SizeCondition)(condition.SelectedIndex + 1) : SizeCondition.Any, (double)(megabytes.Value ?? 0));
        var tabs = new TabControl { ItemsSource = new[] { Tab("Пути поиска", pathRow), Tab("Правила поиска", new ScrollViewer { Content = searchRules }),
            Tab("Правила добавления", new ScrollViewer { Content = rules }) } };

        // Results
        var list = new ListBox { ItemsSource = results, Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(list, "Найденные информационные базы");
        var sizeWidth = Math.Ceiling(UiTypography.Measure("999,99 МБ", typography.DialogFamily, typography.DialogSize).Width) + 4;
        var dateWidth = Math.Ceiling(UiTypography.Measure("00.00.00", typography.DialogFamily, typography.DialogSize).Width) + 4;
        var checkWidth = Math.Ceiling(typography.DialogSize * 2) + 4;
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FoundRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var check = new CheckBox { IsChecked = row.Checked, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
            check.IsCheckedChanged += (_, _) => row.Checked = check.IsChecked == true;
            // Fixed size and date columns keep the rows aligned, as the columns of the original's list view.
            var grid = new Grid { ColumnDefinitions = new($"{checkWidth},2*,{sizeWidth},{dateWidth},3*"), ColumnSpacing = 10 };
            grid.Children.Add(check);
            TextBlock Cell(string text, int column, bool right = false)
            {
                var cell = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
                ToolTip.SetTip(cell, text); Grid.SetColumn(cell, column); grid.Children.Add(cell); return cell;
            }
            Cell(Path.GetFileName(row.Found.Folder), 1); Cell(SizeFormat.Short(row.Found.Size), 2, true);
            Cell(row.Found.Modified.ToString("dd.MM.yy"), 3); Cell(row.Found.Folder, 4);
            AutomationProperties.SetName(check, row.Found.Folder);
            return grid;
        });
        var progress = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap, Text = "Выберите пути и нажмите «Найти». Уже добавленные в список базы не показываются." };
        var checkAll = Action("Включить все", () => { foreach (var row in results) row.Checked = true; list.ItemsSource = null; list.ItemsSource = results; });
        var uncheckAll = Action("Отключить все", () => { foreach (var row in results) row.Checked = false; list.ItemsSource = null; list.ItemsSource = results; });
        var resultTools = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { checkAll, uncheckAll }) { button.Margin = new Thickness(0, 0, 6, 0); resultTools.Children.Add(button); }

        var body = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8 };
        body.Children.Add(tabs);
        Grid.SetRow(resultTools, 1); body.Children.Add(resultTools);
        // As the original's list view: headers «Наименование / Размер / Дата / Папка» over the same columns.
        var header = new Grid { ColumnDefinitions = new($"{checkWidth},2*,{sizeWidth},{dateWidth},3*"), ColumnSpacing = 10, Margin = new Thickness(13, 3, 13, 3) };
        foreach (var (caption, column, right) in new[] { ("Наименование", 1, false), ("Размер", 2, true), ("Дата", 3, false), ("Папка", 4, false) })
        {
            var cell = new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
            Grid.SetColumn(cell, column); header.Children.Add(cell);
        }
        var table = new DockPanel();
        var headerFrame = new Border { Child = header, Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0) };
        DockPanel.SetDock(headerFrame, Dock.Top); table.Children.Add(headerFrame); table.Children.Add(list);
        Grid.SetRow(table, 2); body.Children.Add(table);
        Grid.SetRow(progress, 3); body.Children.Add(progress);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        Button? start = null, apply = null;
        start = Action("Найти", async () =>
        {
            if (running is not null) { running.Cancel(); return; }
            if (roots.Count == 0) { progress.Text = "Добавьте хотя бы одну папку для поиска."; return; }
            running = new CancellationTokenSource(); var token = running.Token;
            start!.Content = "Остановить"; apply!.IsEnabled = false; results.Clear();
            var snapshot = library.Bases.ToList(); var folders = roots.ToList(); var filter = Filter();
            var visited = 0; var found = 0;
            var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), Avalonia.Threading.DispatcherPriority.Background,
                (_, _) => progress.Text = $"Поиск… Проверено папок: {Volatile.Read(ref visited)}. Найдено: {found}.");
            timer.Start();
            try
            {
                await Task.Run(() =>
                {
                    var listed = BaseFinder.ListedFolders(snapshot);
                    foreach (var item in BaseFinder.Scan(folders, token, count => Volatile.Write(ref visited, count)))
                    {
                        if (BaseFinder.Listed(listed, item.Folder) || !filter.Matches(item)) continue;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { results.Add(new FoundRow(item)); found++; });
                    }
                }, token);
                progress.Text = $"Поиск завершён. Проверено папок: {visited}. Найдено новых баз: {results.Count}.";
            }
            catch (OperationCanceledException) { progress.Text = $"Поиск остановлен. Проверено папок: {visited}. Найдено новых баз: {results.Count}."; }
            catch (Exception ex) { progress.Text = "Поиск прерван: " + LegacyImport.ErrorMessage(ex); }
            finally { timer.Stop(); running = null; start.Content = "Найти"; apply.IsEnabled = true; }
        });
        apply = Action("Добавить в список информационных баз", async () =>
        {
            if (adding || running is not null) return;
            var chosen = results.Where(x => x.Checked).ToList();
            if (chosen.Count == 0) { progress.Text = "Отметьте базы, которые нужно добавить."; return; }
            string group;
            try { group = newGroup.IsChecked == true ? Groups.Normalize(groupName.Text ?? "") : (groupChoice.SelectedItem as string ?? "/") == "/" ? "" : (string)groupChoice.SelectedItem!; }
            catch (ArgumentException ex) { progress.Text = LegacyImport.ErrorMessage(ex); return; }
            if (newGroup.IsChecked == true && group.Length == 0) { progress.Text = "Введите название новой группы."; return; }
            var text = usePrefix.IsChecked == true ? prefix.Text ?? "" : "";
            // The list may have changed while searching: a base added meanwhile is not added twice.
            var already = BaseFinder.ListedFolders(library.Bases);
            var entries = chosen.Where(x => !BaseFinder.Listed(already, x.Found.Folder)).Select(x => new BaseEntry
            {
                Name = BaseFinder.Name(x.Found, text, addDate.IsChecked == true), Kind = ConnectionKind.File, Location = x.Found.Folder,
                Group = group, PlatformVersion = (version.SelectedItem as string)?.Replace("1С:Предприятие ", "") ?? "8.3"
            }).ToList();
            adding = true; apply.IsEnabled = false;
            try
            {
                foreach (var entry in entries) entry.Validate();
                var groups = group.Length > 0 && !Groups.All(library).Contains(group, StringComparer.OrdinalIgnoreCase) ? library.Groups.Append(group).ToList() : library.Groups;
                if (entries.Count > 0 && await Persist(library with { Bases = [.. library.Bases, .. entries], Groups = groups }))
                {
                    foreach (var row in chosen) results.Remove(row);
                    collapsed.Clear(); collapsedSections.Clear(); Refresh(entries[0].Id);
                    progress.Text = $"Добавлено баз: {entries.Count}." + (chosen.Count > entries.Count ? $" Уже были в списке: {chosen.Count - entries.Count}." : "");
                    status.Text = $"Добавлено найденных баз: {entries.Count}. " + SourceDescription();
                }
                else if (entries.Count == 0) progress.Text = "Отмеченные базы уже есть в списке.";
                else progress.Text = status.Text ?? "Не удалось сохранить.";
            }
            catch (ArgumentException ex) { progress.Text = LegacyImport.ErrorMessage(ex); }
            finally { adding = false; apply.IsEnabled = running is null; }
        });
        var close = Action("Закрыть", () => dialog.Close()); close.IsCancel = true;
        buttons.Children.Add(start); buttons.Children.Add(apply); buttons.Children.Add(close);
        foreach (var button in buttons.Children.OfType<Button>()) button.Margin = new Thickness(0, 0, 6, 0);
        dialog.Closing += (_, e) => { if (adding) e.Cancel = true; else running?.Cancel(); };
        // Compact rows, as the original's list views.
        dialog.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)) } });
        dialog.Content = DialogLayout.Frame(body, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
}
