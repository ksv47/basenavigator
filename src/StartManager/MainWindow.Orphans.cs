using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

// The original's «Отсутствующие конфигурации»: cache folders of configurations no known base uses, at the end of the tree.
public sealed partial class MainWindow
{
    private IReadOnlyList<OrphanCacheEntry> orphans = [];
    private int orphanGeneration;
    private bool clearingOrphans;

    // Runs after every refresh of the list: the known IDs change with it. A newer scan replaces an older one.
    private async Task RefreshOrphans(Library snapshot)
    {
        var generation = ++orphanGeneration;
        try
        {
            var (found, knowledge) = await Task.Run(() => { var known = OrphanCache.KnownIds(snapshot); return (OrphanCache.Find(known), known); });
            if (closed || generation != orphanGeneration) return;
            // An unread list may name a base whose cache would look orphaned: nothing is shown until every source is read.
            if (!knowledge.Complete) { AppLog.Info("orphans.scan.incomplete"); found = []; }
            var changed = found.Count != orphans.Count || found.Zip(orphans).Any(x => x.First != x.Second);
            orphans = found;
            if (changed) BuildRows();
        }
        catch (Exception ex) { AppLog.Warning("orphans.scan.failed", ex); }
    }

    private Control OrphanRow(Grid row, Grid nameGrid, LibraryRow item, OrphanCacheEntry orphan)
    {
        // A red plate, as the original's rows of this section.
        nameGrid.Children.Add(new Border { Width = 13, Height = 8, CornerRadius = new CornerRadius(1), Background = Brush.Parse("#E0393E"), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = item.Name, FontSize = typography.ListSize, FontFamily = typography.ListFamily, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(label, string.Join("\n", orphan.Folders));
        AutomationProperties.SetAutomationId(row, item.Key);
        Grid.SetColumn(label, 1); nameGrid.Children.Add(label);
        Grid.SetColumnSpan(nameGrid, 2); row.Children.Add(nameGrid);
        var date = Cell(orphan.Modified?.ToString("dd.MM.yy") ?? "—", 2); date.IsVisible = columns.Visible(ListColumn.Date); row.Children.Add(date);
        var size = Cell(FormatSize(orphan.Size), 3); size.IsVisible = columns.Visible(ListColumn.Size); row.Children.Add(size);
        return row;
    }

    // The removal is the command Delete of the table (its key and availability), with the state of the removal itself.
    private MenuItem[] OrphanMenu(LibraryRow target, CommandState state, Func<string, string, string?, bool, System.Action, MenuItem> item)
    {
        var key = MainCommands.KeyText(MainCommand.Delete);
        var removable = MainCommands.CanExecute(MainCommand.Delete, state);
        if (target.Orphan is { } orphan)
            return
            [
                item("Удалить кэш конфигурации…", "delete", key, removable, () => ClearOrphans([orphan])),
                item("Открыть каталог", "folder", null, true, () => OpenOrphanFolder(orphan)),
            ];
        return [item("Удалить весь кэш отсутствующих конфигураций…", "delete", key, removable && orphans.Count > 0, () => ClearOrphans(orphans))];
    }

    private async void OpenOrphanFolder(OrphanCacheEntry orphan)
    {
        var folder = orphan.Folders.First();
        try { if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) status.Text = $"Не удалось открыть каталог {folder}."; }
        catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); status.Text = $"Не удалось открыть каталог {folder}: {ex.Message}"; }
    }

    // The same care as the cache panel: a question with the safe answer by default, a fresh check that the IDs are still
    // unknown right before removing (the list may have changed), folders held by 1C stay and are reported.
    private async void ClearOrphans(IReadOnlyList<OrphanCacheEntry> chosen)
    {
        if (clearingOrphans || closed || chosen.Count == 0) return;
        // The toolbar, the menus and Del follow the removal from its question to its end, cancelled or failed too.
        clearingOrphans = true; RefreshCommands();
        try
        {
            var total = chosen.Sum(x => x.Size);
            var names = chosen.Take(8).Select(x => $"• {x.Id} — {FormatSize(x.Size)}").ToList();
            if (chosen.Count > 8) names.Add($"и ещё {chosen.Count - 8}");
            // Linux keeps the settings in the cache folder itself (no Roaming); that folder goes whole.
            var shared = BaseCacheRoots.Current.Shared;
            var roaming = chosen.Any(x => x.UserFolders.Count > 0);
            if (!await ConfirmDanger($"Удалить кэш отсутствующих конфигураций: {chosen.Count}, {FormatSize(total)}?",
                [string.Join("\n", names),
                 "Этими ID не пользуется ни одна база из открытого списка, списков профиля, штатного списка 1С и общих списков 1CEStart.",
                 shared ? "Каталоги удаляются целиком, вместе с настройками пользователя этих конфигураций; восстановить их нельзя."
                 : roaming ? "Удаляются и настройки пользователя (Roaming) этих конфигураций; восстановить их нельзя." : "Удаляется программный кэш (Local); 1С воссоздаст его при запуске базы.",
                 "Каталоги, занятые запущенной 1С, не удаляются."],
                "Да, удалить")) { status.Text = "Удаление кэша отменено."; return; }
            if (closed) return;
            // The profile on disk is read again with the window's (another copy may have added a source or a base since, RC5-2),
            // and the decision and the removal are made under the lock of the profile (RC6-3).
            var snapshot = library; var profilePath = store.PathName;
            var (removed, failed) = await Task.Run(() =>
            {
                var result = OrphanCache.ClearUnknown(chosen, snapshot, profilePath);
                return (result.Removed, result.Failed);
            });
            AppLog.Info("orphans.cleared");
            status.Text = failed.Count == 0 ? $"Удалено каталогов кэша: {removed}."
                : $"Удалено каталогов кэша: {removed}; не удалось: {failed.Count} — вероятно, база открыта в 1С. {string.Join("; ", failed.Take(2))}";
            await RefreshOrphans(library);
        }
        catch (Exception ex) { AppLog.Warning("orphans.clear.failed", ex); status.Text = "Кэш не удалён: " + LegacyImport.ErrorMessage(ex); }
        finally { clearingOrphans = false; if (!closed) RefreshCommands(); }
    }
}
