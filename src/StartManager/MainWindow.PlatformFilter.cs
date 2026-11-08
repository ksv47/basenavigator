using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // The original's «8.x» (smFormSelecttV8, Alt+F8): only the bases of one platform branch are shown; null — all. Kept until the
    // program closes, as the original's FPlatformFilter.
    private string? platformFilter;
    private Button? platformFilterButton;

    private bool PassesPlatformFilter(BaseEntry entry) => platformFilter is null || PlatformBranches.Of(library, entry) == platformFilter;

    // The original's «Отбор по версии платформы»: a small panel under the button; a branch is applied at once, «Без отбора» shows all.
    private void ShowPlatformFilter()
    {
        if (closed || platformFilterButton is not { } anchor) return;
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(2) };
        var popup = new Popup { Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            Child = new Border { Background = UiColors.Surface, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(12, 8),
                BoxShadow = BoxShadows.Parse("0 3 8 0 #40000000"), Child = panel } };
        panel.Children.Add(new TextBlock { Text = "Отбор по версии платформы", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        void Apply(string? branch)
        {
            popup.IsOpen = false;
            platformFilter = branch; ShowPlatformFilterState();
            BuildRows();
            status.Text = branch is null ? "Отбор по версии платформы снят." : $"Показаны базы платформы 1С:Предприятие {branch}.";
        }
        // The branches of the settings (decision of the user 11.10.2026), newest first, as the original lists its 8.4…8.1.
        foreach (var branch in PlatformBranches.Shown)
        {
            var option = new RadioButton { Content = "1С:Предприятие " + branch, GroupName = "platform-filter", IsChecked = platformFilter == branch, Margin = new Thickness(14, 0) };
            ToolTip.SetTip(option, $"Показать только базы платформы {branch}");
            option.Click += (_, _) => Apply(branch);
            panel.Children.Add(option);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var cancel = SettingsWindow.Command("Отмена", () => popup.IsOpen = false);
        var all = SettingsWindow.Command("Без отбора", () => Apply(null));
        buttons.Children.Add(cancel); buttons.Children.Add(all);
        panel.Children.Add(buttons);
        panel.SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, typography.Family);
        panel.SetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty, typography.InterfaceSize);
        panel.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, UiColors.Text);
        var host = (Panel)anchor.Parent!;
        popup.PlacementTarget = anchor; host.Children.Add(popup);
        popup.Closed += (_, _) => { host.Children.Remove(popup); RestoreListFocus(); };
        // Esc is «Отмена», as in the original's dialog (a popup does not close on Esc by itself).
        popup.Child.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; popup.IsOpen = false; } };
        popup.Opened += (_, _) => (panel.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true) ?? (Control)all).Focus();
        popup.IsOpen = true;
    }

    // As the original's UpdateButtonSelectV8: the button shows the chosen branch.
    private void ShowPlatformFilterState()
    {
        if (platformFilterButton is not { } button) return;
        button.Content = platformFilter ?? "8.x";
        ToolTip.SetTip(button, platformFilter is null ? MainCommands.Tip(MainCommand.PlatformFilter)
            : $"Отбор: 1С:Предприятие {platformFilter} · {MainCommands.KeyText(MainCommand.PlatformFilter)}");
    }
}
