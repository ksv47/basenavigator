using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

// The original's «Сервис → Список информационных баз ▸»: the folder and a copy of the list, the text editor,
// bases that are not found and a changed drive letter. «Форматировать список» is not carried over: it rewrites the file.
public sealed partial class MainWindow
{
    private MenuItem ListMenu()
    {
        var ready = !loadFailed && !reading && !closed;
        var changes = ready && ReadOnlyList is null;
        MenuItem Item(string label, bool enabled, System.Action action)
        {
            var item = new MenuItem { Header = MenuHeader(label), Classes = { "scaled" }, IsEnabled = enabled };
            AutomationProperties.SetName(item, label); item.Click += (_, _) => action(); return item;
        }
        var shared = sharedList is not null;
        var items = new List<Control>
        {
            Item("Открыть каталог списка информационных баз", ready, OpenListFolder),
            new Separator(),
            Item("Сохранить список информационных баз…", ready, SaveListCopy),
            new Separator(),
            Item("Открыть список в текстовом редакторе", ready && shared, OpenListInEditor),
            new Separator(),
            Item("Форматировать список информационных баз…", changes, FormatListNames),
            Item("Удалить несуществующие информационные базы…", changes, RemoveMissingBases),
            new Separator(),
            // The original's «Снимки ▾»: the snapshots of the list and a new one.
            Item("Снимки списка информационных баз…", ready, ShowSnapshots),
            Item("Создать снимок списка", ready, async () => await CreateSnapshot("Снимок " + Path.GetFileName(SnapshotSource))),
        };
        if (OperatingSystem.IsWindows()) { items.Add(new Separator()); items.Add(Item("Заменить букву диска…", changes, ReplaceDriveLetter)); }
        var menu = new MenuItem { Header = MenuHeader("Список информационных баз"), Classes = { "scaled" }, ItemsSource = items };
        AutomationProperties.SetName(menu, "Список информационных баз");
        return menu;
    }

    // The shared .v8i or, for the local list, the folder of the settings.
    private string ListFile => sharedList is not null ? library.ListPath : store.PathName;

    private async void OpenListFolder()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(ListFile))!;
        try { if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) status.Text = $"Не удалось открыть каталог {folder}."; }
        catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); status.Text = $"Не удалось открыть каталог {folder}: {ex.Message}"; }
    }

    // A copy of the list: the shared .v8i byte for byte; the local list as a .v8i in the format of 1CEStart.
    private async void SaveListCopy()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить список информационных баз", SuggestedFileName = "ibases.v8i", DefaultExtension = "v8i",
                FileTypeChoices = [new FilePickerFileType("Список 1С") { Patterns = ["*.v8i"] }]
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            var source = sharedList is not null ? library.ListPath : null;
            var snapshot = library;
            // The copy never replaces a file of the program: the profile, the history, the open list, their backups and locks.
            var vault = new PasswordVault(store.PathName);
            var own = new List<string> { store.PathName, activityStore.PathName, vault.PathName, vault.KeyPath };
            if (source is not null) own.Add(source);
            own.AddRange(own.SelectMany(x => new[] { x + ".bak", x + ".lock", x + ".writing" }).ToList());
            // The marker lies next to the physical file, which a symbolic name of the list does not show.
            var shared = sharedList;
            // A common list of 1CEStart is never written, also not by a copy chosen over it, whether or not the tree shows it.
            var loadedCommon = commonLists.Lists.Select(x => x.Path).ToList();
            await Task.Run(() => ListMaintenance.Export(path, source is not null ? File.ReadAllBytes(source) : ListMaintenance.Utf8WithBom(ListMaintenance.ToV8i(snapshot)),
                [.. own, .. CommonLists.GuardedFromCopy(loadedCommon), .. shared is not null ? [shared.MarkerPathName] : Array.Empty<string>()]));
            status.Text = $"Список сохранён: {path} (баз: {snapshot.Bases.Count}).";
        }
        catch (Exception ex) { AppLog.Warning("list.export.failed", ex); status.Text = "Список не сохранён: " + LegacyImport.ErrorMessage(ex); }
    }

    private async void OpenListInEditor()
    {
        if (sharedList is null) return;
        var path = library.ListPath;
        try
        {
            if (!await ListEditor.Open(Launcher, path)) { status.Text = $"Не удалось открыть {path}."; return; }
            status.Text = "Список открыт в редакторе. После правки нажмите «Обновить список»: изменения из редактора программа сама не перечитывает.";
        }
        catch (Exception ex) { AppLog.Warning("list.editor.failed", ex); status.Text = $"Не удалось открыть {path}: {ex.Message}"; }
    }

    private bool checkingMissing;

    // The search runs off the UI thread and the question may stay open: whatever changed the list meanwhile (an edit, a reload,
    // another list) cancels the removal. Before removing, the confirmed bases are checked on disk once more.
    private async void RemoveMissingBases()
    {
        if (loadFailed || reading || closed || checkingMissing || ListLocked()) return;
        checkingMissing = true;
        try
        {
            var snapshot = library;
            status.Text = "Поиск несуществующих информационных баз…";
            // A base whose removal is forbidden in its properties stays even when it is not found.
            var found = await Task.Run(() => ListMaintenance.Missing(snapshot.Bases));
            var missing = found.Where(x => !x.DisallowDelete).ToList(); var kept = found.Count - missing.Count;
            if (closed) return;
            const string changed = "Список изменился во время проверки. Ничего не удалено; повторите команду.";
            if (!ReferenceEquals(library, snapshot)) { status.Text = changed; return; }
            var keptNote = kept > 0 ? $" Не найдены, но запрещены к удалению и оставлены: {kept}." : "";
            if (missing.Count == 0) { status.Text = (kept > 0 ? "Несуществующих баз, которые можно убрать, нет." : "Несуществующих файловых баз в списке нет.") + keptNote; return; }
            var names = missing.Take(10).Select(x => $"• {x.Name} — {x.Location}").ToList();
            if (missing.Count > 10) names.Add($"и ещё {missing.Count - 10}");
            var confirmed = await ConfirmDanger($"Убрать из списка информационные базы, которые не найдены: {missing.Count}?",
                [string.Join("\n", names),
                 "Этих каталогов нет или в них нет файла 1Cv8.1CD. Если база лежит на отключённом сетевом или съёмном диске, она тоже считается ненайденной — подключите диск и обновите список.",
                 sharedList is not null ? "Прежний список сохранится рядом в файле .bak." : "Прежние настройки сохранятся рядом в файле .bak.",
                 "Не проверяются серверные базы, базы на веб-сервере, пути другой операционной системы и каталоги без права чтения." + keptNote],
                "Да, убрать");
            if (closed) return;
            if (!confirmed) { status.Text = "Удаление несуществующих баз отменено."; return; }
            if (!ReferenceEquals(library, snapshot)) { status.Text = changed; return; }
            var still = await Task.Run(() => ListMaintenance.Missing(missing));
            if (closed) return;
            if (!ReferenceEquals(library, snapshot)) { status.Text = changed; return; }
            var ids = still.Select(x => x.Id).ToHashSet();
            if (ids.Count == 0) { status.Text = "Базы нашлись снова: ничего не удалено."; return; }
            if (await Persist(library with { Bases = library.Bases.Where(x => !ids.Contains(x.Id)).ToList() }))
            {
                Refresh();
                status.Text = $"Убрано несуществующих баз: {ids.Count}." + (missing.Count > ids.Count ? $" Нашлись снова и оставлены: {missing.Count - ids.Count}." : "") + keptNote + " " + SourceDescription();
            }
        }
        finally { checkingMissing = false; }
    }

    // The original's «Форматирование списка баз»: the case of the names, trailing spaces and a replaced string, for groups
    // and bases. The whole result is written by one save (the group records keep their ID and fields): a refusal changes
    // nothing, and .bak holds the list as it was before the formatting.
    private async void FormatListNames()
    {
        if (loadFailed || reading || closed || ListLocked()) return;
        var dialog = new Window { Title = "Форматирование списка баз", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var groups = new CheckBox { Content = "Группы", IsChecked = true }; var basesBox = new CheckBox { Content = "Информационные базы", IsChecked = true };
        var cases = new[] { "Не форматировать", "Все буквы ЗАГЛАВНЫЕ", "Все буквы строчные", "Первая буква Заглавная", "Первая буква каждого слова Заглавная" };
        var caseButtons = cases.Select((text, index) => new RadioButton { Content = text, GroupName = "name-case", IsChecked = index == 0 }).ToArray();
        int CaseIndex() => Math.Max(0, Array.FindIndex(caseButtons, x => x.IsChecked == true));
        var trim = new CheckBox { Content = "Убрать концевые пробелы" };
        var replace = new CheckBox { Content = "Заменить строку:" };
        var from = new TextBox { IsEnabled = false }; var to = new TextBox { IsEnabled = false };
        AutomationProperties.SetName(from, "Заменить строку"); AutomationProperties.SetName(to, "На строку");
        replace.IsCheckedChanged += (_, _) => from.IsEnabled = to.IsEnabled = replace.IsChecked == true;
        var panel = new StackPanel { Spacing = 10 };
        var scope = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var box in new Control[] { DialogLayout.Label("Обрабатывать:"), groups, basesBox }) { box.Margin = new Thickness(0, 0, 12, 0); box.VerticalAlignment = VerticalAlignment.Center; scope.Children.Add(box); }
        panel.Children.Add(scope);
        var caseGroup = new StackPanel { Spacing = 2 };
        foreach (var button in caseButtons) caseGroup.Children.Add(button);
        panel.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(10, 6), Child = caseGroup });
        panel.Children.Add(trim);
        var replaceRow = new Grid { ColumnDefinitions = new("Auto,*,Auto,*"), ColumnSpacing = 8 };
        var onLabel = DialogLayout.Label("на:"); onLabel.VerticalAlignment = VerticalAlignment.Center;
        replace.VerticalAlignment = VerticalAlignment.Center;
        replaceRow.Children.Add(replace); Grid.SetColumn(from, 1); replaceRow.Children.Add(from);
        Grid.SetColumn(onLabel, 2); replaceRow.Children.Add(onLabel); Grid.SetColumn(to, 3); replaceRow.Children.Add(to);
        panel.Children.Add(replaceRow);
        var preview = DialogLayout.Label("");
        panel.Children.Add(preview);
        NameFormat Format() => new((NameCase)CaseIndex(), trim.IsChecked == true, replace.IsChecked == true ? from.Text ?? "" : "",
            replace.IsChecked == true ? to.Text ?? "" : "", groups.IsChecked == true, basesBox.IsChecked == true);
        (Library Library, IReadOnlyDictionary<string, string> GroupMap, int Bases, int Groups)? plan = null;
        void Preview()
        {
            try
            {
                var format = Format();
                var result = ListFormatting.Plan(library, format);
                plan = result;
                var byId = library.Bases.ToDictionary(x => x.Id);
                var examples = result.Library.Bases.Where(x => byId[x.Id].Name != x.Name).Take(3).Select(x => $"«{byId[x.Id].Name}» → «{x.Name}»")
                    .Concat(result.GroupMap.Where(x => !x.Key.Contains('/')).Take(2).Select(x => $"группа «{x.Key}» → «{x.Value}»"));
                preview.Text = $"Изменится наименований баз: {result.Bases}, групп: {result.Groups}." + (examples.Any() ? "\n" + string.Join("\n", examples) : "");
                preview.Foreground = UiColors.Text;
            }
            catch (ArgumentException ex) { plan = null; preview.Text = ex.Message; preview.Foreground = UiColors.Error; }
        }
        foreach (var box in new[] { groups, basesBox, trim, replace }) box.IsCheckedChanged += (_, _) => Preview();
        foreach (var button in caseButtons) button.IsCheckedChanged += (_, _) => Preview(); from.TextChanged += (_, _) => Preview(); to.TextChanged += (_, _) => Preview();
        Preview();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var cancel = Action("Закрыть", () => dialog.Close(false)); cancel.IsCancel = true;
        var run = Action("Выполнить", () => { if (plan is { } p && (p.Bases > 0 || p.GroupMap.Count > 0)) dialog.Close(true); });
        run.IsDefault = true;
        buttons.Children.Add(cancel); buttons.Children.Add(run);
        dialog.Content = DialogLayout.Frame(panel, buttons); typography.ApplyDialog(dialog);
        if (!await dialog.ShowDialog<bool>(this) || plan is not { } chosen || closed) { RestoreListFocus(); return; }
        // One save; the plan is built again on the list as it is now, in case it changed while the window was open.
        try
        {
            var final = ListFormatting.Plan(library, Format());
            if (await Persist(final.Library, groupMap: final.GroupMap))
            {
                Refresh();
                status.Text = $"Наименования отформатированы: баз {final.Bases}, групп {final.Groups}. Прежний список — в .bak.";
            }
        }
        catch (ArgumentException ex) { status.Text = LegacyImport.ErrorMessage(ex); }
        RestoreListFocus();
    }

    // The original's «Замена буквы диска в списке информационных баз» (FormReplacingDrive): the open list or the standard list of a
    // branch (decision of the user 11.10.2026). A list that is not open is read and written by its own store: a snapshot first, then the
    // .bak, the write marker and a refusal when another program changed the file meanwhile. A common list of 1CEStart is never offered.
    private async void ReplaceDriveLetter()
    {
        if (loadFailed || reading || closed || ListLocked()) return;
        var openPath = sharedList is not null ? Path.GetFullPath(library.ListPath) : null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // The files behind their names: a branch list that is the open list through a link is the open list, not another file.
        List<(string Label, string? Path, string? Branch)> files;
        try
        {
            files = await Task.Run(() =>
            {
                var result = new List<(string Label, string? Path, string? Branch)> { ("Открытый список" + (openPath is null ? " (локальный)" : ": " + openPath), null, null) };
                foreach (var (branch, path) in SnapshotBranches.Available())
                {
                    if (openPath is not null && ListAccess.SameFile(path, openPath)) continue;
                    var index = result.FindIndex(x => x.Path is not null && ListAccess.SameFile(x.Path, path));
                    if (index < 0) result.Add(($"Список {branch}: {Path.GetFullPath(path)}", Path.GetFullPath(path), branch));
                    else result[index] = (result[index].Label.Replace("Список ", "Список " + branch + " / "), result[index].Path, result[index].Branch);
                }
                return result;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = [("Открытый список" + (openPath is null ? " (локальный)" : ": " + openPath), null, null)]; }
        if (closed) return;
        // Drives of the computer first; any letter stays possible (a network drive may not be mapped yet).
        var present = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).Where(char.IsAsciiLetterUpper).Distinct().Order().ToList();
        // As the original: «C:» with a colon.
        var letters = present.Concat(Enumerable.Range('A', 26).Select(x => (char)x).Except(present)).Select(x => x + ":").ToArray();
        var dialog = new Window { Title = "Замена буквы диска в списке информационных баз", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var file = new ComboBox { ItemsSource = files.Select(x => x.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var from = new ComboBox { ItemsSource = letters, SelectedItem = "C:", MinWidth = 80 };
        var to = new ComboBox { ItemsSource = letters, SelectedItem = "D:", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(file, "Файл списка информационных баз"); AutomationProperties.SetName(from, "Заменить диск"); AutomationProperties.SetName(to, "На диск");
        // The layout of FormReplacingDrive.dfm: the note on top; in a frame the list file under its caption, then one row
        // «Заменить диск: [C:]  на диск: [ … ]»; «Закрыть» and «Выполнить» at the bottom right.
        var panel = new StackPanel { Spacing = 10 };
        var note = DialogLayout.Label("Будет выполнена замена буквы диска в пути подключения файловых информационных баз");
        note.TextAlignment = Avalonia.Media.TextAlignment.Center; panel.Children.Add(note);
        var framed = new StackPanel { Spacing = 6 };
        framed.Children.Add(DialogLayout.Label("Файл списка информационных баз"));
        framed.Children.Add(file);
        var drives = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        void Cell(Control control, int column) { Grid.SetColumn(control, column); drives.Children.Add(control); }
        var fromLabel = DialogLayout.Label("Заменить диск:"); fromLabel.VerticalAlignment = VerticalAlignment.Center;
        var toLabel = DialogLayout.Label("на диск:"); toLabel.VerticalAlignment = VerticalAlignment.Center; toLabel.Margin = new Thickness(16, 0, 0, 0);
        Cell(fromLabel, 0); Cell(from, 1); Cell(toLabel, 2); Cell(to, 3);
        framed.Children.Add(drives);
        panel.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(12, 8), Child = framed });
        var preview = DialogLayout.Label("");
        panel.Children.Add(preview);
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        // The chosen list as it is read now: the open library, or another file with its own store. «Выполнить» is available only when the
        // list read is the list selected (loadedIndex); a pending read of another file makes it unavailable.
        Library chosen = library; V8iStore? other = null; var generation = 0; var loadedIndex = 0;
        Button? applyButton = null;
        void Preview()
        {
            var used = chosen.Bases.Where(x => x.Kind == ConnectionKind.File && x.Location.Length >= 2 && x.Location[1] == ':')
                .Select(x => char.ToUpperInvariant(x.Location[0])).Where(char.IsAsciiLetterUpper).Distinct().Order().ToList();
            var count = ListMaintenance.CountOnDrive(chosen.Bases, ((string)from.SelectedItem!)[0]);
            preview.Text = $"На диске {from.SelectedItem} файловых баз: {count}. Меняется только путь в списке; файлы баз не переносятся." +
                (used.Count > 0 ? $"\nДиски в списке: {string.Join(", ", used)}." : "\nВ списке нет файловых баз с буквой диска.");
        }
        async void Choose()
        {
            var current = ++generation; error.Text = "";
            var selected = Math.Max(0, file.SelectedIndex);
            var (_, path, _) = files[selected];
            loadedIndex = -1; chosen = new Library(); other = null;
            if (applyButton is not null) applyButton.IsEnabled = false;
            if (path is null) { chosen = library; other = null; }
            else
            {
                preview.Text = "Чтение списка…";
                try
                {
                    var store = new V8iStore(path);
                    var read = await Task.Run(() => store.Load(new Library()));
                    if (current != generation) return;
                    if (store.ReadOnly is { } reason) { error.Text = "Список только для чтения: " + reason; }
                    (chosen, other) = (read, store);
                }
                catch (Exception ex) { if (current == generation) { error.Text = "Список не прочитан: " + LegacyImport.ErrorMessage(ex); chosen = new Library(); other = null; } return; }
            }
            if (current != generation) return;
            loadedIndex = selected;
            if (applyButton is not null) applyButton.IsEnabled = true;
            var first = chosen.Bases.Where(x => x.Kind == ConnectionKind.File && x.Location.Length >= 2 && x.Location[1] == ':')
                .Select(x => char.ToUpperInvariant(x.Location[0])).Where(char.IsAsciiLetterUpper).Order().FirstOrDefault();
            if (first != default) from.SelectedItem = first + ":";
            // «На диск»: another drive of the computer, else D.
            var source = ((string)from.SelectedItem!)[0];
            if (((string?)to.SelectedItem)?[0] == source)
                to.SelectedItem = (present.FirstOrDefault(x => x != source) is var drive && drive != default ? drive : source == 'D' ? 'E' : 'D') + ":";
            Preview();
        }
        from.SelectionChanged += (_, _) => Preview(); file.SelectionChanged += (_, _) => Choose(); Choose();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Action("Закрыть", () => dialog.Close(false)); cancel.IsCancel = true;
        var apply = Action("Выполнить", () =>
        {
            try
            {
                if (loadedIndex != file.SelectedIndex || files[file.SelectedIndex].Path is not null && other is null) { error.Text = "Список ещё читается или не прочитан."; return; }
                var (_, changed) = ListMaintenance.ReplaceDrive(chosen, ((string)from.SelectedItem!)[0], ((string)to.SelectedItem!)[0]);
                if (changed == 0) { error.Text = "На этом диске нет файловых баз."; return; }
                dialog.Close(true);
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        });
        apply.IsDefault = true; applyButton = apply; apply.IsEnabled = loadedIndex == file.SelectedIndex;
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        dialog.Content = DialogLayout.Frame(panel, buttons, error); typography.ApplyDialog(dialog);
        if (!await dialog.ShowDialog<bool>(this) || closed) { RestoreListFocus(); return; }
        // Everything the work needs is taken here, on the UI thread, as it was when «Выполнить» was pressed; a later read is ignored.
        generation++;
        var target = files[loadedIndex];
        var (fromLetter, toLetter) = (((string)from.SelectedItem!)[0], ((string)to.SelectedItem!)[0]);
        var (accepted, acceptedStore) = (chosen, other);
        try
        {
            var (next, changed) = ListMaintenance.ReplaceDrive(accepted, fromLetter, toLetter);
            if (target.Path is null)
            {
                if (await Persist(next)) { Refresh(); status.Text = $"Буква диска заменена у баз: {changed}. " + SourceDescription(); }
            }
            else if (acceptedStore is { } store)
            {
                var path = target.Path; var folder = Snapshots;
                BeginWriting("Замена буквы диска в списке…");
                try
                {
                    // The list as it was goes to a snapshot first: the replacement can be undone from «Снимки».
                    await Task.Run(() =>
                    {
                        folder.Create(path, ListSnapshots.ReadList(path), $"Перед заменой диска {fromLetter}: → {toLetter}:", version: target.Branch);
                        store.Save(next);
                    });
                    AppLog.Info("list.drive.replaced_other");
                    status.Text = $"Буква диска заменена у баз: {changed} в списке {path}. Прежний список — в .bak и в снимках.";
                }
                catch (Exception ex) { AppLog.Warning("list.drive.other_failed", ex); status.Text = "Список не изменён: " + LegacyImport.ErrorMessage(ex); }
                finally { EndWriting(false); }
            }
        }
        catch (ArgumentException ex) { status.Text = ex.Message; }
        RestoreListFocus();
    }
}
