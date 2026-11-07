using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private readonly ActivityStore activityStore;
    private Activity activity = new();
    private bool viewRestored;
    private string ActivitySource => ActivityStore.Source(store.PathName, library.ListPath);
    private bool IsFavorite(BaseEntry entry)
    {
        var source = SourceOf(entry);
        return activity.Favorites.Any(x => x.Source == source && x.Id == entry.Id);
    }

    // The original's «Перейти к Избранному» (the star under the list, Ctrl+J): the section opens and gets the focus.
    private void GoToFavorites()
    {
        collapsedSections.Remove(LibrarySection.Favorites);
        BuildRows("section:Favorites"); if (bases.SelectedItem is { } row) bases.ScrollIntoView(row);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            bases.UpdateLayout();
            if (bases.SelectedItem is { } selected) bases.ContainerFromItem(selected)?.Focus(Avalonia.Input.NavigationMethod.Directional);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async Task<bool> LoadActivity()
    {
        try
        {
            var loaded = await Task.Run(activityStore.Load);
            if (closed) return false;
            activity = loaded; BuildRows(); return true;
        }
        catch (Exception ex)
        {
            if (!closed) status.Text = $"Избранное и история недоступны: {LegacyImport.ErrorMessage(ex)} Файл: {activityStore.PathName}. Резервная копия: {activityStore.PathName}.bak.";
            return false;
        }
    }

    // Restores the list view of the previous session once; until then closing keeps the saved view.
    private void RestoreView()
    {
        viewRestored = true;
        columns = activity.Columns; BuildHeader(); listHeader.Width = MinimumListWidth();
        if (activity.Views.FirstOrDefault(x => x.Source == ActivitySource) is not { } view) return;
        collapsed.Clear(); collapsed.UnionWith(view.Collapsed);
        collapsedSections.Clear();
        foreach (var name in view.CollapsedSections)
            if (Enum.TryParse<LibrarySection>(name, out var section) && section != LibrarySection.None) collapsedSections.Add(section);
        RevealSelected(view.Selected);
        BuildRows(view.Selected);
        if (bases.SelectedItem is { } selected) bases.ScrollIntoView(selected);
    }

    // A row chosen through the search may sit in a closed branch; open the branch so the selection comes back.
    private void RevealSelected(string? key)
    {
        if (key is null) return;
        string? group = null;
        if (key.StartsWith("favorite:", StringComparison.Ordinal)) { collapsedSections.Remove(LibrarySection.Favorites); return; }
        if (key.StartsWith("group:", StringComparison.Ordinal)) group = Groups.Parent(key["group:".Length..]);
        else if (Guid.TryParse(key, out var id) && ShownBases().FirstOrDefault(x => x.Id == id) is { } entry) group = entry.Group;
        if (group is null) return;
        collapsedSections.Remove(LibrarySection.All);
        for (; group.Length > 0; group = Groups.Parent(group)) collapsed.Remove(group);
    }

    private void SaveView()
    {
        if (!viewRestored) return;
        var view = new ListView(ActivitySource)
        {
            Selected = (bases.SelectedItem as LibraryRow)?.Key,
            Collapsed = [.. collapsed.Order(StringComparer.OrdinalIgnoreCase)],
            CollapsedSections = [.. collapsedSections.Select(x => x.ToString()).Order(StringComparer.Ordinal)]
        };
        // Closing must not hang on a slow or lost profile folder: wait a bounded time, then let the write finish or fail alone.
        // After the wait the process may exit first; then the last view is lost, the previous file stays intact.
        var store = activityStore; var layout = columns;
        var saving = Task.Run(() =>
        {
            try { store.SaveView(view, layout); }
            catch (Exception ex) { AppLog.Warning("view.not_saved", ex); }
        });
        if (!saving.Wait(TimeSpan.FromSeconds(2))) AppLog.Warning("view.save.slow");
    }

    private bool PersistentIdentity(BaseEntry entry) => CommonOf(entry) is { } common ? common.PersistentId
        : sharedList is null || sharedList.HasPersistentId(entry.Id);
    private bool RequirePersistentIdentity(BaseEntry entry)
    {
        if (PersistentIdentity(entry)) return true;
        if (CommonOf(entry) is { } common)
        { status.Text = $"В записи общего списка {common.ListPath} нет постоянного ID: избранное для этой базы недоступно."; return false; }
        if (ReadOnlyList is { } reason)
        { status.Text = $"В записи .v8i нет постоянного ID, а список только для чтения ({reason}): избранное и ярлык для этой базы недоступны."; return false; }
        status.Text = "В записи .v8i нет постоянного ID. Сначала сохраните свойства базы через Alt+Enter; затем можно добавить избранное или создать ярлык.";
        return false;
    }

    private void ToggleFavorite()
        => SetFavorite(removeOnly: false);

    private void RemoveFavorite()
        => SetFavorite(removeOnly: true);

    private async void SetFavorite(bool removeOnly)
    {
        if (loadFailed || reading || closed || Selected() is not { } entry) return;
        if (!RequirePersistentIdentity(entry)) return;
        var source = SourceOf(entry);
        var selected = (bases.SelectedItem as LibraryRow)?.Key;
        var shortcut = bases.SelectedItem is LibraryRow { FavoriteShortcut: true };
        BeginWriting("Сохранение избранного…");
        try
        {
            var result = await Task.Run(() => activityStore.ToggleFavorite(source, entry.Id, removeOnly));
            activity = result.Activity;
            BuildRows(!result.Enabled && shortcut ? "section:Favorites" : selected);
            status.Text = result.Enabled ? "База добавлена в избранное." : "База убрана из избранного.";
        }
        catch (Exception ex) { status.Text = "Избранное не сохранено: " + LegacyImport.ErrorMessage(ex); }
        finally { EndWriting(false); }
    }

    private async void OpenHistory()
    {
        if (reading || closed) return;
        var failedBefore = loadFailed;
        reading = true; loadFailed = true;
        foreach (var button in editingButtons.Concat(launchButtons)) button.IsEnabled = false;
        bool loaded;
        try { loaded = await LoadActivity(); }
        finally { EndWriting(failedBefore); }
        if (!loaded || closed) return;
        var dialog = new Window { Title = "История запусков", Width = 610, Height = 420, MinWidth = 450, MinHeight = 280,
             WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sources = HistorySources();
        var items = activity.History.Where(x => sources.Contains(x.Source)).ToList();
        var history = new ListBox { ItemsSource = items.Select(x => $"{x.Time.ToLocalTime():dd.MM.yyyy HH:mm} · {x.Name} · {(x.Designer ? "Конфигуратор" : "Предприятие")}" +
            (!x.PersistentIdentity ? " · нет постоянного ID" : BaseOf(x.Source, x.Id) is null ? " · отсутствует в списке" : "")).ToList() };
        history.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) => DialogLayout.Label(text ?? ""));
        var panel = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        panel.Children.Add(new TextBlock { Text = items.Count == 0 ? "Для этого списка пока нет запусков." : "Последние запуски из текущего списка", TextWrapping = TextWrapping.Wrap });
        Put(panel, history, 1);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var repeat = Action("Повторить запуск", () =>
        {
            if (history.SelectedIndex < 0) return;
            var record = items[history.SelectedIndex];
            var entry = BaseOf(record.Source, record.Id);
            if (entry is null || loadFailed || !record.PersistentIdentity) return;
            dialog.Close(); LaunchEntry(entry, record.Designer);
        });
        repeat.IsEnabled = false;
        history.SelectionChanged += (_, _) => repeat.IsEnabled = !loadFailed && history.SelectedIndex >= 0 && items[history.SelectedIndex].PersistentIdentity && BaseOf(items[history.SelectedIndex].Source, items[history.SelectedIndex].Id) is not null;
        var close = Action("Закрыть", dialog.Close); close.IsCancel = true;
        buttons.Children.Add(repeat); buttons.Children.Add(close);
        panel.RowDefinitions = new RowDefinitions("Auto,*"); panel.Margin = new Thickness(0);
        dialog.Content = DialogLayout.Frame(panel, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        await dialog.ShowDialog(this);
    }

    private async void CreateShortcut()
    {
        if (loadFailed || reading || closed || Selected() is not { } entry) return;
        if (CommonLocked(entry) || !RequirePersistentIdentity(entry)) return;
        try
        {
            var extension = OperatingSystem.IsWindows() ? "lnk" : "desktop";
            var suggestion = string.Concat(entry.Name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Ярлык запуска в режиме 1С:Предприятие", SuggestedFileName = "База " + suggestion + "." + extension,
                DefaultExtension = extension, ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Ярлык") { Patterns = ["*." + extension] }]
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (closed || reading || loadFailed) return;
            var request = new ShortcutRequest(Path.GetFullPath(store.PathName), library.ListPath.Length > 0 ? Path.GetFullPath(library.ListPath) : "", entry.Id, false);
            // A shortcut reads the profile, so a first-run profile is written before the shortcut.
            var settings = store; var profile = ProfileToSave(library);
            BeginWriting("Создание ярлыка…");
            try
            {
                await Task.Run(() =>
                {
                    if (!File.Exists(settings.PathName)) settings.Save(profile);
                    ShortcutWriter.Write(path, request, entry.Name);
                });
            }
            finally { EndWriting(false); }
            status.Text = "Ярлык создан: " + path;
        }
        catch (Exception ex) { status.Text = "Не удалось создать ярлык: " + LegacyImport.ErrorMessage(ex); }
    }
}
