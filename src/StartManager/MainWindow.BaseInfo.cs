using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // The original's «Сводная информация» (acTotalBaseInfo, FormBaseInfo, Ctrl+Alt+I): the name, ID, connection, platform and list
    // of the selected base, read only; the values can be selected and copied.
    private async void ShowBaseInfo()
    {
        if (loadFailed || reading || closed) return;
        if (bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) { status.Text = MainCommands.ChooseBaseText; return; }
        var common = CommonOf(entry);
        information.TryGetValue(entry.Id, out var data);
        var platform = data is null ? "Определяется…" : (data.Release.Length > 0 ? data.Release : data.Platform) +
            (data.ArchitectureLabel.Length > 0 ? $" ({data.ArchitectureLabel})" : "") + (data.PlatformMissing ? " — не установлена" : "");
        var listPath = common?.ListPath ?? (library.ListPath.Length > 0 ? library.ListPath : "профиль БазоНавигатора: " + store.PathName);
        var dialog = new Window { Title = "Сводная информация", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        // The order of FormBaseInfo: the title, then the name, ID, connection, platform, list and the list type.
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Сведения по информационной базе", FontWeight = FontWeight.Bold, Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap });
        foreach (var (caption, value) in new[]
        {
            ("Наименование:", entry.Name), ("Идентификатор:", entry.Id.ToString()), ("Размещение:", BaseClipboard.ConnectionString(entry)),
            ("Платформа:", platform), ("Список баз:", listPath.Replace("\0", "\\0")), ("Тип списка баз:", common is null ? "Локальный" : "Общий")
        })
        {
            var text = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(text, caption.TrimEnd(':'));
            var label = DialogLayout.Label(caption); label.Foreground = UiColors.Muted;
            panel.Children.Add(DialogLayout.Field(label, text));
        }
        // As the original: one «Закрыть» in the middle.
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var close = SettingsWindow.Command("Закрыть", dialog.Close); close.IsCancel = close.IsDefault = true;
        buttons.Children.Add(close);
        dialog.Content = DialogLayout.Frame(panel, buttons);
        typography.ApplyDialog(dialog); dialog.Opened += (_, _) => close.Focus();
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }
}
