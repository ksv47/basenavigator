using Avalonia.Platform.Storage;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private bool killingProcesses;

    // The original's «Сервис» menu under the arrow of the list heading.
    private void ShowServiceMenu(Control anchor)
    {
        var menu = ClassicMenu("Сервис");
        var kill = new MenuItem { Header = MenuHeader("Завершение процессов «1С:Предприятие 8»…"), Classes = { "scaled" }, IsEnabled = !killingProcesses };
        AutomationProperties.SetName(kill, "Завершение процессов «1С:Предприятие 8»");
        kill.Click += (_, _) => KillClientProcesses();
        // The original's order: the list, finding a base in it, then the search on the computer and ending the processes.
        var searchBases = new MenuItem { Header = MenuHeader("Поиск информационных баз на компьютере…"), Classes = { "scaled" }, IsEnabled = !loadFailed && !reading && ReadOnlyList is null };
        AutomationProperties.SetName(searchBases, "Поиск информационных баз на компьютере");
        searchBases.Click += (_, _) => SearchBasesOnComputer();
        // As the original: the folders of 1C, the list, finding a base, the installed versions, the search, ending the processes.
        MenuItem Plain(string label, System.Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = MenuHeader(label, gesture), Classes = { "scaled" } };
            AutomationProperties.SetName(item, label.TrimEnd('…')); item.Click += (_, _) => action(); return item;
        }
        var roots = BaseCacheRoots.Current;
        // The original's «Восстановить версию» (actAppRollback) of the system menu; without its Ctrl+Alt+B (the user's choice 15.10).
        var restore = Plain("Восстановить версию…", ShowRestoreVersion);
        restore.IsEnabled = !restoringVersion;
        menu.ItemsSource = new Control[]
        {
            Plain("Каталог настроек пользователя (Roaming)", () => OpenFolder(roots.User)), Plain("Каталог программного кэша (Local)", () => OpenFolder(roots.Program)),
            new Separator(), ListMenu(), FindInListMenu(), new Separator(), Plain("Список установленных версий 1С:Предприятие 8", OpenVersions),
            new Separator(), searchBases, new Separator(), kill,
            // The original opens these from its system menu (Alt+F11), which is not carried over: they end the service menu.
            new Separator(), Plain("Краткая справка", ShowShortHelp, Shortcuts.KeysOf(ShortcutCommand.ShortHelp)[0].ToString()),
            restore, Plain("О программе…", ShowAbout)
        };
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    private bool restoringVersion;

    // «Восстановление версии»: the chosen archived version is prepared now and put in place by the helper after the program closes.
    private async void ShowRestoreVersion()
    {
        if (closed || restoringVersion) return;
        restoringVersion = true;
        try
        {
            var repository = library.UpdateRepository; var keep = library.KeepVersions;
            Updater Create() => Program.CreateUpdater(repository, keep);
            var chosen = await RestoreVersionWindow.Choose(this, typography, Create);
            if (chosen is null || closed) return;
            status.Text = $"Подготовка возврата версии {chosen.Version}…";
            try
            {
                updateNotice = await Task.Run(() => RestoreVersionWindow.Restore(Create(), chosen, Program.CurrentVersion));
                AppLog.Info("update.files_changed");
                if (!closed && !reading && !writing) status.Text = SourceDescription();
            }
            catch (Exception ex)
            {
                AppLog.Warning("update.failed", ex);
                if (!closed) status.Text = "Восстановление версии не выполнено: " + LegacyImport.ErrorMessage(ex);
            }
        }
        catch (Exception ex) { AppLog.Warning("update.failed", ex); if (!closed) status.Text = "Не удалось открыть окно восстановления версии: " + ex.Message; }
        finally { restoringVersion = false; if (!closed) RestoreListFocus(); }
    }

    private async void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) { status.Text = $"Каталог ещё не создан: {folder}. 1С создаёт его при первом запуске."; return; }
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) status.Text = $"Не удалось открыть каталог {folder}.";
        }
        catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); status.Text = $"Не удалось открыть каталог {folder}: {ex.Message}"; }
    }

    // As the original: a strong warning, the choice «Нет, я не уверен» by default, then the count of ended processes.
    // Unlike the original the processes are found first, so the warning names them and nothing is asked when there are none.
    private async void KillClientProcesses()
    {
        if (killingProcesses || closed) return;
        killingProcesses = true;
        try
        {
            var found = await Task.Run(() => ClientProcesses.Find());
            if (found.Count == 0) { status.Text = "Процессы, относящиеся к «1С:Предприятие 8», не найдены."; return; }
            var names = string.Join(", ", found.GroupBy(ClientProcesses.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Count() == 1 ? x.Key : $"{x.Key} ×{x.Count()}"));
            var identities = found.Select(ClientProcesses.Identity).ToHashSet();
            foreach (var process in found) process.Dispose();
            if (!await ConfirmKill(found.Count, names)) { status.Text = "Завершение процессов отменено."; return; }
            // The question may stay open for minutes: only the confirmed processes that still run are ended.
            AppLog.Info("processes.kill.begin");
            var result = await Task.Run(() => ClientProcesses.EndConfirmed(identities));
            AppLog.Info("processes.kill.done");
            status.Text = result.Status(identities.Count);
        }
        catch (Exception ex) { AppLog.Warning("processes.kill.failed", ex); status.Text = "Не удалось завершить процессы: " + ex.Message; }
        finally { killingProcesses = false; }
    }

    private Task<bool> ConfirmKill(int count, string names) => ConfirmDanger(
        "Внимание! Будут принудительно завершены все запущенные процессы «1С:Предприятие 8» этого пользователя.",
        [$"Найдено процессов: {count} ({names}).",
         "Выполняйте эту операцию, только если сеансы зависли и не отвечают. Несохранённые данные во всех " +
         "открытых сеансах 1С, включая конфигуратор, будут потеряны.",
         "Завершить процессы «1С:Предприятие 8»?"],
        "Да, завершить");

    // A dangerous action: the safe answer is on the left, the default one and Escape; the action is on the right.
    private async Task<bool> ConfirmDanger(string heading, IEnumerable<string> lines, string action, Window? owner = null)
    {
        var dialog = new Window { Title = "Подтверждение", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        foreach (var line in lines) panel.Children.Add(DialogLayout.Label(line));
        var buttons = new DockPanel();
        var no = Action("Нет, я не уверен", () => dialog.Close(false)); no.IsCancel = no.IsDefault = true;
        var yes = Action(action, () => dialog.Close(true));
        DockPanel.SetDock(no, Dock.Left); DockPanel.SetDock(yes, Dock.Right);
        no.HorizontalAlignment = HorizontalAlignment.Left; yes.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(no); buttons.Children.Add(yes);
        dialog.Content = DialogLayout.Frame(panel, buttons);
        typography.ApplyDialog(dialog); dialog.Opened += (_, _) => no.Focus();
        var confirmed = await dialog.ShowDialog<bool>(owner ?? this);
        RestoreListFocus();
        return confirmed;
    }
}
