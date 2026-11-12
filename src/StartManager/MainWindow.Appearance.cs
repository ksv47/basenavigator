using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private async void OpenPreferences(string section)
    {
        if (reading || closed) return;
        if (!await ResolvePendingProfile() || closed) return;
        var settingsStore = store;
        var baseline = library;
        if (loadFailed)
        {
            reading = true;
            try { settingsStore = new LibraryStore(store.PathName); baseline = await Task.Run(settingsStore.Load); }
            catch (Exception ex) { if (!closed) status.Text = "Не удалось открыть настройки: " + LegacyImport.ErrorMessage(ex); return; }
            finally { reading = false; }
        }
        if (closed) return;
        var dialog = new SettingsWindow(baseline, store.PathName, section,
            async (path, client, appearance) =>
            {
                var next = baseline;
                var source = sharedList;
                if (path != baseline.ListPath || loadFailed)
                {
                    next = LibrarySources.Select(baseline, store.PathName, path);
                    source = path.Length == 0 ? null : new V8iStore(path);
                    if (source is not null) next = await Task.Run(() => source.Load(next));
                }
                return (next with { Executable = client, Appearance = appearance }, source);
            },
            async (next, source, import) =>
            {
                string? warning = null;
                if (import is not null)
                {
                    var result = await Task.Run(() => LegacyImport.Apply(import, settingsStore, activityStore));
                    next = result.Library; source = null; warning = result.FavoriteError;
                }
                else await Task.Run(() => settingsStore.Save(next));
                var common = await Task.Run(() => LoadCommonLists(next, settingsStore.PathName));
                if (closed) return (next, warning);
                store = settingsStore; baseline = library = next; sharedList = source; commonLists = common;
                // Before Refresh(): its platform scan must see the folders just saved.
                PlatformCatalog.UseFolders(next);
                listOverrideActive = false;
                loadFailed = false;
                collapsed.Clear(); collapsedSections.Clear(); search.Text = "";
                RebuildInterface(); Refresh(); await LoadActivity();
                status.Text = warning ?? (import is null ? "Настройки сохранены. " + SourceDescription() : "Импорт завершён. Открыт локальный список.");
                return (library, warning);
            });
        dialog.HelpRequested = () => ShowShortHelpForSettings(dialog);
        await dialog.ShowDialog(this);
    }
}
