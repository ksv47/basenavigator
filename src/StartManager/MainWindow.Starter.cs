using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using StartManager.Core;

namespace StartManager;

// The original's «1С:Стартер» (ButRunStarter): the standard window of 1C with its own list. The button starts the starter of the
// platform the selected base starts with (or the newest one); ▾ lists the starters found (decision of the user 11.10.2026).
// Nothing is passed to the starter and nothing is recorded; it does not need a selected base.
public sealed partial class MainWindow
{
    private bool startingStarter;

    private Control StarterButton()
    {
        var launch = LaunchAction("1С:Стартер", "database", () => RunStarter(null));
        ToolTip.SetTip(launch, "Запустить штатный стартер 1С");
        var panel = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(launch);
        var more = Action("", () => { });
        more.Content = UiIcons.Create("chevron-down", Math.Max(12, typography.IconSize - 4)); more.Padding = new Thickness(2, 0);
        more.MinHeight = launch.MinHeight; more.VerticalAlignment = VerticalAlignment.Stretch; more.BorderThickness = new Thickness(0, 1, 1, 1);
        AutomationProperties.SetName(more, "Выбор стартера по версии"); ToolTip.SetTip(more, "Выбор стартера по версии");
        more.Click += (_, _) => ShowStarterMenu(more);
        Grid.SetColumn(more, 1); panel.Children.Add(more);
        return panel;
    }

    // Everything that reads the disk runs off the UI thread: the folders may be on a slow disk.
    private static IReadOnlyList<PlatformInstallation> FindInstallations(Library snapshot) =>
        PlatformCatalog.Scan(PlatformCatalog.RootsOf(snapshot), snapshot.Bases.Select(x => x.Executable).Append(snapshot.Executable),
            snapshot.PlatformFolders).Installations;

    // The platform the selected base starts with; a base without a usable platform gives the newest starter.
    private static PlatformStarter? StarterFor(Library snapshot, BaseEntry? entry)
    {
        var installations = FindInstallations(snapshot);
        string? executable = null;
        if (entry is not null)
        {
            try { executable = PlatformCatalog.Resolve(snapshot, entry, installations); }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { AppLog.Info("starter.base_platform.unresolved"); }
        }
        return Starters.Choose(Starters.Find(installations), executable);
    }

    private async void ShowStarterMenu(Control anchor)
    {
        IReadOnlyList<PlatformStarter> found;
        var snapshot = library;
        try { found = await Task.Run(() => Starters.Find(FindInstallations(snapshot))); }
        catch (Exception ex) { status.Text = "Стартеры 1С не найдены: " + LegacyImport.ErrorMessage(ex); return; }
        if (closed) return;
        var menu = ClassicMenu("Выбор стартера по версии");
        var items = new List<Control>();
        foreach (var starter in found)
        {
            var text = starter.ToString();
            var item = new MenuItem { Header = MenuHeader(text), Classes = { "scaled" } };
            AutomationProperties.SetName(item, text); ToolTip.SetTip(item, starter.Executable);
            item.Click += (_, _) => RunStarter(starter);
            items.Add(item);
        }
        if (items.Count == 0)
            items.Add(new MenuItem { Header = MenuHeader("Установленные платформы 1С не найдены"), Classes = { "scaled" }, IsEnabled = false });
        menu.ItemsSource = items;
        menu.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    private async void RunStarter(PlatformStarter? chosen)
    {
        if (startingStarter || closed) return;
        startingStarter = true;
        try
        {
            var starter = chosen;
            if (starter is null)
            {
                var snapshot = library; var entry = (bases.SelectedItem as LibraryRow)?.Entry;
                starter = await Task.Run(() => StarterFor(snapshot, entry));
            }
            if (closed) return;
            if (starter is null) { status.Text = "Стартер 1С не найден: установленные платформы 1С не найдены."; return; }
            await Task.Run(() => Starters.Start(starter));
            AppLog.Info("starter.started");
            status.Text = $"Запущен стартер 1С: {starter}.";
        }
        catch (Exception ex) { AppLog.Warning("starter.failed", ex); if (!closed) status.Text = "Стартер 1С не запущен: " + LegacyImport.ErrorMessage(ex); }
        finally { startingStarter = false; RestoreListFocus(); }
    }
}
