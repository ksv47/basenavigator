using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

internal sealed class StorageModeWindow : Window
{
    public StorageModeWindow(string currentSettings, AppearanceSettings appearance, SettingsStorage? storage = null)
    {
        storage ??= new SettingsStorage();
        var expected = storage.ReadPortable();
        var type = UiTypography.From(appearance);
        Title = "Режим хранения настроек · БазоНавигатор";
        Width = 620; Height = 460; MinWidth = 340; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 12 };
        TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(Label("Папка текущего сеанса: " + Path.GetDirectoryName(Path.GetFullPath(currentSettings))));
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Обычный", "Портативный" }, SelectedIndex = expected ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(mode, "Режим хранения настроек");
        body.Children.Add(DialogLayout.Field("Сохранённый режим для следующих запусков:", mode));
        var path = Label("");
        void UpdatePath() => path.Text = "Папка настроек: " + storage.DirectoryFor(mode.SelectedIndex == 1);
        mode.SelectionChanged += (_, _) => UpdatePath(); UpdatePath();
        body.Children.Add(path);
        body.Children.Add(Label("Обычный режим хранит настройки отдельно для каждого пользователя. " +
            "Портативный — в папке Settings рядом с программой; требуется право записи в её каталог."));
        body.Children.Add(Label("Выбор применяется после перезапуска. Данные не переносятся и не удаляются. " +
            "Если в выбранной папке уже есть настройки, будут загружены они; иначе откроется пустой список. " +
            "Ключи --settings, --portable и --normal имеют приоритет над сохранённым режимом."));
        var message = Label("");
        AutomationProperties.SetName(message, "Результат сохранения режима");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var saving = false;
        var save = SettingsWindow.Command("Сохранить режим", async () =>
        {
            if (saving) return;
            saving = true; buttons.IsEnabled = mode.IsEnabled = false;
            var selected = mode.SelectedIndex == 1;
            try
            {
                await Task.Run(() => storage.SaveMode(selected, expected));
                expected = selected;
                message.Foreground = UiColors.Text;
                message.Text = "Режим сохранён. Закройте программу и запустите её снова. Текущий сеанс продолжает работать с прежними настройками.";
            }
            catch (Exception ex)
            {
                AppLog.Error("storage.mode.failed", ex);
                message.Foreground = UiColors.Error;
                message.Text = "Режим не изменён. " + (ex switch
                {
                    DirectoryNotFoundException => "Папка программы или настроек недоступна.",
                    FileNotFoundException => "Файл переключателя режима недоступен.",
                    _ => LegacyImport.ErrorMessage(ex)
                });
            }
            finally { saving = false; buttons.IsEnabled = mode.IsEnabled = true; }
        });
        buttons.Children.Add(save);
        var close = SettingsWindow.Command("Закрыть", Close); close.IsCancel = true;
        buttons.Children.Add(close);
        Content = DialogLayout.Frame(body, buttons, message);
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        type.ApplyDialog(this);
    }
}
