using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

// The original's «Снимки списка информационных баз» (smFormBasesSnap) and «Создать снимок списка ИБ»: copies of the list
// in the Snapshots folder next to the settings, to look at and to put back.
public sealed partial class MainWindow
{
    private ListSnapshots Snapshots => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.PathName))!, "Snapshots"));
    private string SnapshotSource => sharedList is not null ? library.ListPath : "Локальный список БазоНавигатора";

    // The shared .v8i byte for byte; the local list in the .v8i format of 1CEStart.
    private byte[] CurrentListBytes(Library snapshot, bool shared) =>
        shared ? ListSnapshots.ReadList(snapshot.ListPath) : ListMaintenance.Utf8WithBom(ListMaintenance.ToV8i(snapshot));

    // The standard list of 1CEStart is the list of 8.3, as the original marks it.
    private static string? SnapshotVersion(Library snapshot, bool shared) =>
        shared && PathsEqual(snapshot.ListPath, V8iStore.StandardPath) ? "8.3" : null;

    // Before the first change of a day the list as it was is kept: the original's automatic snapshots. A failure here never
    // stops the change itself; it is written to the log.
    private async Task AutoSnapshot()
    {
        var snapshot = library; var shared = sharedList is not null; var source = SnapshotSource; var folder = Snapshots;
        try
        {
            await Task.Run(() => folder.CreateAutoIfNeeded(source,
                () => (CurrentListBytes(snapshot, shared), shared ? null : LocalListState.Write(snapshot)), "Автоматически перед изменениями", 20,
                SnapshotVersion(snapshot, shared)));
        }
        catch (Exception ex) { AppLog.Warning("snapshot.auto.failed", ex); }
    }

    private async Task<ListSnapshot?> CreateSnapshot(string description)
    {
        var snapshot = library; var shared = sharedList is not null; var source = SnapshotSource; var folder = Snapshots;
        try
        {
            // A local list also keeps its own state (client, version rule, unknown fields), which the .v8i text cannot carry.
            var version = SnapshotVersion(snapshot, shared);
            var created = await Task.Run(() => folder.Create(source, CurrentListBytes(snapshot, shared), description, shared ? null : LocalListState.Write(snapshot), version: version));
            status.Text = $"Снимок списка создан: {created.Description} ({created.Bases} баз).";
            return created;
        }
        catch (Exception ex) { AppLog.Warning("snapshot.create.failed", ex); status.Text = "Снимок не создан: " + LegacyImport.ErrorMessage(ex); return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // «Создать снимок списка ИБ для 8.3 / 8.2 / 8.1»: the standard list of the branch, read byte for byte whether or not it is
    // the open one; nothing is written to it. It is put back only into the open list, as every other snapshot.
    private async Task CreateBranchSnapshot(string branch, string path)
    {
        var folder = Snapshots;
        try
        {
            var created = await Task.Run(() => folder.Create(path, ListSnapshots.ReadList(path), $"Снимок ИБ от {DateTime.Now:dd.MM.yyyy HH:mm}", version: branch));
            status.Text = $"Снимок списка {branch} создан: {created.Description} ({created.Bases} баз).";
        }
        catch (Exception ex) { AppLog.Warning("snapshot.branch.failed", ex); status.Text = $"Снимок списка {branch} не создан: " + LegacyImport.ErrorMessage(ex); }
    }

    private async void ShowSnapshotsMenu(Control anchor)
    {
        var menu = ClassicMenu("Снимки");
        MenuItem Item(string label, System.Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = MenuHeader(label, gesture), Classes = { "scaled" }, IsEnabled = !loadFailed && !reading };
            AutomationProperties.SetName(item, label); item.Click += (_, _) => action(); return item;
        }
        IReadOnlyList<(string Branch, string Path)> branches;
        try { branches = await Task.Run(SnapshotBranches.Available); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { branches = []; }
        if (closed) return;
        var items = new List<Control>
        {
            Item("Снимки информационных баз…", ShowSnapshots, "Ctrl+L"), new Separator(),
            Item("Создать снимок списка", async () => await CreateSnapshot("Снимок " + Path.GetFileName(SnapshotSource))),
        };
        // Only the branches whose list exists; 8.2 and 8.3 take the same file, as in the original.
        foreach (var (branch, path) in branches)
        {
            var item = Item($"Создать снимок списка ИБ для {branch}", async () => await CreateBranchSnapshot(branch, path));
            ToolTip.SetTip(item, path); items.Add(item);
        }
        menu.ItemsSource = items;
        // The button stands at the right of the toolbar: the menu opens to the left of its right edge and stays inside the window.
        menu.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    private async void ShowSnapshots()
    {
        if (closed) return;
        var dialog = new Window { Title = "Снимки списка информационных баз", Width = 720, Height = 460, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var items = new ObservableCollection<ListSnapshot>();
        var message = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };
        var dateWidth = Math.Ceiling(UiTypography.Measure("00.00.0000 00:00:00", typography.DialogFamily, typography.DialogSize).Width) + 8;
        var countWidth = Math.Ceiling(UiTypography.Measure("Баз: 9999", typography.DialogFamily, typography.DialogSize).Width) + 8;
        var versionWidth = Math.Ceiling(UiTypography.Measure("Версия", typography.DialogFamily, typography.DialogSize).Width) + 8;
        var list = new ListBox { ItemsSource = items, Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(list, "Снимки списка");
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ListSnapshot>((item, _) =>
        {
            if (item is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new($"3*,{dateWidth},{versionWidth},{countWidth},2*"), ColumnSpacing = 10 };
            void Cell(string text, int column) { var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }; ToolTip.SetTip(cell, text); Grid.SetColumn(cell, column); grid.Children.Add(cell); }
            Cell((item.Description.Length > 0 ? item.Description : "Снимок") + (item.Auto ? " (авто)" : ""), 0); Cell(item.Created.ToString("dd.MM.yyyy HH:mm:ss"), 1);
            Cell(item.Version ?? "—", 2); Cell(item.Bases.ToString(), 3); Cell(item.Source, 4);
            return grid;
        });
        void Load()
        {
            items.Clear();
            try { foreach (var item in Snapshots.List()) items.Add(item); message.Text = items.Count == 0 ? "Снимков пока нет. «Создать снимок» сохранит текущий список." : $"Снимков: {items.Count}. Папка: {Snapshots.Folder}"; }
            catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); }
        }
        ListSnapshot? Chosen() { if (list.SelectedItem is ListSnapshot item) return item; message.Text = "Выберите снимок в списке."; return null; }
        var create = Action("Создать снимок", async () => { if (await CreateSnapshot("Снимок " + Path.GetFileName(SnapshotSource)) is not null) Load(); });
        var view = Action("Просмотр", async () => { if (Chosen() is { } item) await ViewSnapshot(dialog, item); });
        var restore = Action("Восстановить", async () => { if (Chosen() is { } item && await RestoreSnapshot(dialog, item)) dialog.Close(); });
        var delete = Action("Удалить", async () =>
        {
            if (Chosen() is not { } item) return;
            if (!await ConfirmDanger("Удалить снимок списка?", [$"{item.Description} · {item.Created:dd.MM.yyyy HH:mm} · баз: {item.Bases}", "Сам список баз не меняется."], "Да, удалить", dialog)) return;
            try { var failed = Snapshots.Delete([item]); Load(); if (failed.Count > 0) message.Text = "Снимок не удалён: файл занят (открыт в другом окне). Повторите позже."; }
            catch (Exception ex) { Load(); message.Text = LegacyImport.ErrorMessage(ex); }
        });
        var clear = Action("Очистить", async () =>
        {
            if (items.Count == 0) return;
            if (!await ConfirmDanger($"Удалить все снимки списка: {items.Count}?", ["Сами списки баз не меняются. Восстановить удалённые снимки нельзя."], "Да, удалить все", dialog)) return;
            try { var failed = Snapshots.Delete(items.ToList()); Load(); if (failed.Count > 0) message.Text = $"Не удалено снимков: {failed.Count} — файлы заняты (открыты в другом окне). Повторите позже."; }
            catch (Exception ex) { Load(); message.Text = LegacyImport.ErrorMessage(ex); }
        });
        var close = Action("Закрыть", () => dialog.Close()); close.IsCancel = true;
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        // As the original's toolbar: «Восстановить», «Просмотр» on the left, «Удалить», «Очистить» on the right; «Создать снимок» is ours.
        foreach (var button in new[] { restore, view, create }) { button.Margin = new Thickness(0, 0, 6, 6); tools.Children.Add(button); }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { delete, clear }) { button.Margin = new Thickness(6, 0, 0, 6); right.Children.Add(button); }
        var body = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        var toolbar = new DockPanel();
        DockPanel.SetDock(right, Dock.Right); toolbar.Children.Add(right); toolbar.Children.Add(tools);
        body.Children.Add(toolbar);
        // As the original: a header row «Описание / Дата / Версия» (ours adds «Баз» and «Источник»); the cells keep the list's padding.
        var header = new Grid { ColumnDefinitions = new($"3*,{dateWidth},{versionWidth},{countWidth},2*"), ColumnSpacing = 10, Margin = new Thickness(7, 3, 7, 3) };
        foreach (var (caption, column) in new[] { ("Описание", 0), ("Дата", 1), ("Версия", 2), ("Баз", 3), ("Источник", 4) })
        { var cell = new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(cell, column); header.Children.Add(cell); }
        var table = new DockPanel();
        var headerFrame = new Border { Child = header, Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0) };
        DockPanel.SetDock(headerFrame, Dock.Top); table.Children.Add(headerFrame); table.Children.Add(list);
        Grid.SetRow(table, 1); body.Children.Add(table);
        Grid.SetRow(message, 2); body.Children.Add(message);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(close);
        dialog.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 3)) } });
        dialog.Content = DialogLayout.Frame(body, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        Load();
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }

    // In memory: the snapshot may hold secrets of the list, no temporary copy is written (Codex full audit 24.10, P1-2).
    private static Library ParseSnapshot(byte[] content) => V8iStore.Read(content);

    private async Task ViewSnapshot(Window owner, ListSnapshot item)
    {
        string text;
        try
        {
            var parsed = await Task.Run(() => ParseSnapshot(Snapshots.Read(item)));
            text = string.Join("\n", parsed.Bases.OrderBy(x => x.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => (x.Group.Length > 0 ? x.Group + " / " : "") + x.Name + " — " + BaseClipboard.ConnectionString(x)));
            if (text.Length == 0) text = "В снимке нет баз.";
        }
        catch (Exception ex) { text = "Снимок не прочитан: " + LegacyImport.ErrorMessage(ex); }
        var dialog = new Window { Title = $"Снимок: {item.Description} · {item.Created:dd.MM.yyyy HH:mm}", Width = 720, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var close = Action("Закрыть", () => dialog.Close()); close.IsCancel = close.IsDefault = true; buttons.Children.Add(close);
        dialog.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(dialog);
        await dialog.ShowDialog(owner);
    }

    // As «Удалить из списка»: a restore that would drop a base whose removal is forbidden is refused, the bases named.
    private bool RefuseForbiddenRemovals(IEnumerable<BaseEntry> restored)
    {
        var forbidden = BaseEdit.ForbiddenRemovals(library.Bases, restored);
        if (forbidden.Count == 0) return false;
        status.Text = "Список не восстановлен: в снимке нет баз, удаление которых запрещено в их свойствах («Прочие свойства»): " +
            string.Join(", ", forbidden.Take(5).Select(x => "«" + x.Name + "»")) + (forbidden.Count > 5 ? $" и ещё {forbidden.Count - 5}" : "") +
            ". Снимите запрет или добавьте эти базы после восстановления.";
        return true;
    }

    // Before anything is put back, the current list is saved as a snapshot: restoring can itself be undone.
    private async Task<bool> RestoreSnapshot(Window owner, ListSnapshot item)
    {
        if (loadFailed || reading || closed) { status.Text = "Список занят или не загружен."; return false; }
        if (ListLocked()) return false;
        byte[] content; Library parsed;
        try { content = await Task.Run(() => Snapshots.Read(item)); parsed = await Task.Run(() => ParseSnapshot(content)); }
        catch (Exception ex) { status.Text = "Снимок не прочитан: " + LegacyImport.ErrorMessage(ex); return false; }
        if (RefuseForbiddenRemovals(parsed.Bases)) return false;
        var shared = sharedList is not null;
        var other = !item.Source.Equals(SnapshotSource, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!await ConfirmDanger($"Восстановить список из снимка от {item.Created:dd.MM.yyyy HH:mm}?",
            [$"Сейчас в списке баз: {library.Bases.Count}; в снимке: {parsed.Bases.Count}.",
             other ? $"Снимок сделан с другого списка: {item.Source}. Он заменит открытый список: {SnapshotSource}." : $"Список: {SnapshotSource}.",
             shared ? "Файл списка заменяется содержимым снимка целиком; прежний текст сохранится в .bak и отдельным снимком."
                    : "Базы и группы локального списка заменяются базами снимка; прежнее состояние сохранится отдельным снимком.",
             "Файлы самих информационных баз не меняются."], "Да, восстановить", owner)) return false;
        if (await CreateSnapshot("Перед восстановлением снимка от " + item.Created.ToString("dd.MM.yyyy HH:mm")) is null) return false;
        // The list may have changed while the question was open.
        if (RefuseForbiddenRemovals(parsed.Bases)) return false;
        if (shared)
        {
            var failedBefore = loadFailed; var target = sharedList!;
            BeginWriting("Восстановление списка из снимка…");
            try { await Task.Run(() => target.Restore(content)); AppLog.Info("snapshot.restored"); }
            catch (Exception ex) { AppLog.Warning("snapshot.restore.failed", ex); EndWriting(failedBefore); status.Text = "Список не восстановлен: " + LegacyImport.ErrorMessage(ex); return false; }
            EndWriting(failedBefore);
            await ReloadAsync();
            status.Text = $"Список восстановлен из снимка от {item.Created:dd.MM.yyyy HH:mm}. Прежний — в .bak и в снимках.";
            return true;
        }
        // The snapshot's own state wins; a snapshot of a shared list or of the original has none, so a base with the same ID
        // keeps the client, the version rule and the unknown fields it has now.
        List<BaseEntry> restoredBases; List<string> restoredGroups; List<GroupFolder>? restoredFolders = null;
        try
        {
            if (await Task.Run(() => Snapshots.ReadState(item)) is { } state) { var local = LocalListState.Read(state); restoredBases = local.Bases; restoredGroups = local.Groups; restoredFolders = local.GroupFolders; }
            else
            {
                restoredBases = BaseEdit.KeepLocal(parsed.Bases, library.Bases);
                restoredGroups = parsed.Groups;
            }
        }
        catch (Exception ex) { status.Text = "Снимок не прочитан: " + LegacyImport.ErrorMessage(ex); return false; }
        if (RefuseForbiddenRemovals(restoredBases)) return false;
        // The folders of the snapshot; an older snapshot has none, so the folders now stay with the groups it still has.
        var restored = library with { Bases = restoredBases, Groups = restoredGroups };
        restored = restored with { GroupFolders = Groups.Existing(restoredFolders ?? library.GroupFolders, restored) };
        if (!await Persist(restored)) return false;
        search.Text = ""; collapsed.Clear(); collapsedSections.Clear(); Refresh();
        status.Text = $"Локальный список восстановлен из снимка от {item.Created:dd.MM.yyyy HH:mm}.";
        return true;
    }
}
