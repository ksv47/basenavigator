using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

internal sealed class AppearanceEditor : UserControl
{
    private sealed record FamilyChoice(string Name, string Label) { public override string ToString() => Label; }
    private AppearanceSettings original;
    public event Action? Changed;
    private bool resetRequested;
    private readonly Dictionary<string, (ComboBox Family, NumericUpDown Size, TextBlock Resolved)> fonts = [];
    private readonly ComboBox theme = new() { ItemsSource = new[] { "Системная", "Светлая", "Тёмная" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown rowHeight = new() { Minimum = 20, Maximum = 64, Increment = 1, FormatString = "G", HorizontalAlignment = HorizontalAlignment.Stretch };
    // The original's «Интенсивность выделения» (0–255), «Отображать всплывающие подсказки», «Отображать группы сверху».
    private readonly Slider intensity = new() { Minimum = 0, Maximum = 255, SmallChange = 8, LargeChange = 32, TickFrequency = 1,
        IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox tooltips = new() { Content = new TextBlock { Text = "Отображать всплывающие подсказки", TextWrapping = TextWrapping.Wrap } };
    private readonly CheckBox groupsOnTop = new() { Content = new TextBlock { Text = "Отображать группы сверху (при сортировке по наименованию)", TextWrapping = TextWrapping.Wrap } };
    private readonly Border preview = new() { Padding = new Thickness(14), BorderThickness = new Thickness(1) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private bool updating;

    public AppearanceEditor(AppearanceSettings settings)
    {
        original = settings;
        var type = UiTypography.From(settings);
        FontFamily = type.DialogFamily; FontSize = type.DialogSize;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(DialogLayout.Section("Стиль", FontEditor("list", "Шрифт списка", false)));
        var extra = new StackPanel { Spacing = 14 };
        extra.Children.Add(FontEditor("ui", "Меню и панели", true));
        extra.Children.Add(FontEditor("headers", "Заголовки колонок", true));
        extra.Children.Add(FontEditor("dialogs", "Диалоги", true));
        var advanced = new Expander { Header = new TextBlock { Text = "Шрифты интерфейса и диалогов", TextWrapping = TextWrapping.Wrap }, Content = extra, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(advanced, "Шрифты интерфейса и диалогов"); panel.Children.Add(advanced);
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = "Минимальная высота строки", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        rowHeight.MinWidth = Math.Max(96, FontSize * 4 + 40); Grid.SetColumn(rowHeight, 1); row.Children.Add(rowHeight);
        var tree = new StackPanel { Spacing = 8 };
        tree.Children.Add(row);
        tree.Children.Add(new TextBlock { Text = "Строка автоматически увеличится, если выбранному шрифту нужно больше места.", TextWrapping = TextWrapping.Wrap });
        tree.Children.Add(tooltips); tree.Children.Add(groupsOnTop);
        // As the original's panel: «Выделение активной строки» before «Настройка дерева списка».
        // The label above the slider: a narrow window with a large font keeps the whole width for the track.
        var selection = new StackPanel { Spacing = 4 };
        selection.Children.Add(new TextBlock { Text = "Интенсивность выделения:", TextWrapping = TextWrapping.Wrap });
        selection.Children.Add(intensity);
        panel.Children.Add(DialogLayout.Section("Выделение активной строки", selection));
        panel.Children.Add(DialogLayout.Section("Настройка дерева списка", tree));
        AutomationProperties.SetName(intensity, "Интенсивность выделения");
        AutomationProperties.SetName(tooltips, "Отображать всплывающие подсказки"); AutomationProperties.SetName(groupsOnTop, "Отображать группы сверху");
        var themeRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        themeRow.Children.Add(new TextBlock { Text = "Тема", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(theme, 1); themeRow.Children.Add(theme); panel.Children.Add(themeRow);
        AutomationProperties.SetName(theme, "Тема оформления");
        AutomationProperties.SetName(rowHeight, "Минимальная высота строки");
        var reset = SettingsWindow.Command("По умолчанию", () => { resetRequested = true; Load(original.Reset()); }); panel.Children.Add(reset);
        panel.Children.Add(preview); panel.Children.Add(message); Content = panel;
        theme.SelectionChanged += (_, _) => RefreshPreview(); rowHeight.ValueChanged += (_, _) => RefreshPreview();
        intensity.ValueChanged += (_, _) => RefreshPreview();
        tooltips.IsCheckedChanged += (_, _) => RefreshPreview(); groupsOnTop.IsCheckedChanged += (_, _) => RefreshPreview();
        rowHeight.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) RefreshPreview(); };
        Load(settings);
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private Control FontEditor(string key, string label, bool inherits)
    {
        var family = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, MaxDropDownHeight = 320 };
        var choices = new[] { new FamilyChoice("", inherits ? "Как у списка" : "Системный шрифт") }
            .Concat(UiTypography.InstalledFonts.Select(x => new FamilyChoice(x, x))).ToList();
        family.ItemsSource = choices;
        var size = new NumericUpDown { Minimum = 10, Maximum = 24, Increment = 1, FormatString = "G", MinWidth = Math.Max(96, FontSize * 4 + 40) };
        AutomationProperties.SetName(family, label + ": семейство"); AutomationProperties.SetName(size, label + ": размер");
        var resolved = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fonts.Add(key, (family, size, resolved));
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(Heading(label));
        var fields = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        fields.Children.Add(family); Grid.SetColumn(size, 1); fields.Children.Add(size);
        panel.Children.Add(fields); panel.Children.Add(resolved);
        family.SelectionChanged += (_, _) => RefreshPreview(); size.ValueChanged += (_, _) => RefreshPreview();
        size.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) RefreshPreview(); };
        return panel;
    }
    private void Load(AppearanceSettings settings)
    {
        updating = true;
        try
        {
            theme.SelectedIndex = (int)settings.Theme; rowHeight.Value = (decimal)settings.MinimumRowHeight;
            intensity.Value = settings.SelectionIntensity; tooltips.IsChecked = settings.ListTooltips; groupsOnTop.IsChecked = settings.GroupsOnTop;
            rowHeight.Text = rowHeight.Value.Value.ToString("G", rowHeight.NumberFormat);
            foreach (var (key, preference) in new[] { ("list", settings.List), ("ui", settings.Interface), ("headers", settings.Headers), ("dialogs", settings.Dialogs) })
            {
                var editor = fonts[key]; var choices = ((IEnumerable<FamilyChoice>)editor.Family.ItemsSource!).ToList();
                var choice = choices.FirstOrDefault(x => x.Name.Equals(preference.Family, StringComparison.OrdinalIgnoreCase));
                if (choice is null)
                {
                    choice = new(preference.Family, preference.Family + " (недоступен)"); choices.Add(choice); editor.Family.ItemsSource = choices;
                }
                editor.Family.SelectedItem = choice; editor.Size.Value = (decimal)preference.Size;
                editor.Size.Text = editor.Size.Value.Value.ToString("G", editor.Size.NumberFormat);
            }
        }
        finally { updating = false; }
        RefreshPreview();
    }
    public AppearanceSettings ReadSettings()
    {
        var next = Read();
        bool Same(FontPreference a, FontPreference b) => a.Family == b.Family && a.Size == b.Size;
        return !resetRequested && next.Theme == original.Theme && next.MinimumRowHeight == original.MinimumRowHeight &&
            next.SelectionIntensity == original.SelectionIntensity && next.ListTooltips == original.ListTooltips && next.GroupsOnTop == original.GroupsOnTop &&
            Same(next.List, original.List) && Same(next.Interface, original.Interface) &&
            Same(next.Headers, original.Headers) && Same(next.Dialogs, original.Dialogs) ? original : next;
    }
    private AppearanceSettings Read()
    {
        FontPreference Font(string key, FontPreference basis)
        {
            var editor = fonts[key];
            if (editor.Family.SelectedItem is not FamilyChoice choice || editor.Size.Value is null)
                throw new ArgumentException("Выберите шрифт и укажите размер от 10 до 24.");
            return basis with { Family = choice.Name, Size = ReadNumber(editor.Size) };
        }
        if (rowHeight.Value is null || theme.SelectedIndex < 0) throw new ArgumentException("Укажите высоту строки и тему.");
        var result = original with
        {
            UnrecognizedSource = null, LoadWarning = null,
            Theme = (AppearanceTheme)theme.SelectedIndex, MinimumRowHeight = ReadNumber(rowHeight),
            SelectionIntensity = Math.Round(Math.Clamp(intensity.Value, 0, 255)), ListTooltips = tooltips.IsChecked == true, GroupsOnTop = groupsOnTop.IsChecked == true,
            List = Font("list", original.List), Interface = Font("ui", original.Interface),
            Headers = Font("headers", original.Headers), Dialogs = Font("dialogs", original.Dialogs)
        };
        result.Validate(); return result;
    }
    private static double ReadNumber(NumericUpDown control)
    {
        // NumericUpDown may retain its previous Value while invalid text is visible.
        if (!decimal.TryParse(control.Text, System.Globalization.NumberStyles.Number, control.NumberFormat, out var value) ||
            value < control.Minimum || value > control.Maximum)
            throw new ArgumentException($"Введите число от {control.Minimum} до {control.Maximum}.");
        return (double)value;
    }
    private void RefreshPreview()
    {
        if (updating) return;
        Changed?.Invoke();
        try
        {
            var settings = Read(); var type = UiTypography.From(settings);
            var dark = settings.Theme == AppearanceTheme.Dark || settings.Theme == AppearanceTheme.System &&
                Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
            preview.Background = Brush.Parse(dark ? "#282E36" : "#F0EFEB");
            // Keep editor controls at the saved size while previewing large dialog fonts.
            foreach (var (key, requested, actual) in new[] { ("list", settings.List.Family, type.ListFamily), ("ui", settings.Interface.Family, type.Family), ("headers", settings.Headers.Family, type.HeaderFamily), ("dialogs", settings.Dialogs.Family, type.DialogFamily) })
                fonts[key].Resolved.Text = requested.Length == 0 || requested.Equals(actual.Name, StringComparison.OrdinalIgnoreCase)
                    ? "Используется " + actual.Name : requested + " недоступен → " + actual.Name;
            var sample = new StackPanel { Spacing = 14 };
            sample.Children.Add(new TextBlock { Text = "Предпросмотр", FontFamily = type.Family, FontSize = type.InterfaceSize, FontWeight = FontWeight.SemiBold });
            var table = new StackPanel();
            var previewColumns = "*," + Math.Ceiling(Math.Max(UiTypography.Measure("Платформа", type.HeaderFamily, type.ColumnHeaderSize).Width,
                UiTypography.Measure("8.3.27", type.ListFamily, type.ListSize).Width) + 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Grid Row(string name, string version, bool header, bool selected)
            {
                var row = new Grid { ColumnDefinitions = new(previewColumns), MinHeight = header ? type.HeaderHeight : type.RowHeight,
                    Background = selected ? new SolidColorBrush(UiColors.SelectionColor(dark, settings.SelectionIntensity)) : Brush.Parse(dark ? "#20262D" : "#FFFFFF") };
                for (var i = 0; i < 2; i++)
                {
                    var cell = new TextBlock { Text = i == 0 ? name : version, Margin = new Thickness(6, 2),
                        FontFamily = header ? type.HeaderFamily : type.ListFamily, FontSize = header ? type.ColumnHeaderSize : type.ListSize,
                        Foreground = Brush.Parse(selected ? AppearanceSettings.SelectedTextColor(dark, settings.SelectionIntensity) : dark ? "#E7EDF3" : "#252B31"),
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    if (settings.ListTooltips) ToolTip.SetTip(cell, cell.Text);
                    Grid.SetColumn(cell, i); row.Children.Add(cell);
                }
                return row;
            }
            table.Children.Add(Row("Наименование", "Платформа", true, false));
            table.Children.Add(Row("Бухгалтерия предприятия", "8.3.27", false, true));
            table.Children.Add(Row("Зарплата и управление персоналом", "8.3.27", false, false));
            table.Children.Add(Row("Документооборот — учебная база", "8.3.26", false, false)); sample.Children.Add(table);
            sample.Children.Add(new TextBlock { Text = "Добавить базу…   Свойства…", FontFamily = type.Family, FontSize = type.InterfaceSize, TextWrapping = TextWrapping.Wrap });
            sample.Children.Add(new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), BorderBrush = Brush.Parse(dark ? "#596675" : "#B7BFC7"),
                Child = new TextBlock { Text = "Пример диалога\nНастройки будут применены к списку, меню и окнам программы.", FontFamily = type.DialogFamily, FontSize = type.DialogSize, Foreground = Brush.Parse(dark ? "#E7EDF3" : "#252B31"), TextWrapping = TextWrapping.Wrap } });
            sample.Children.Add(new TextBlock { Text = $"Высота строки: {type.RowHeight:0} · размеры в логических пикселях", TextWrapping = TextWrapping.Wrap });
            foreach (var text in sample.Children.OfType<TextBlock>()) text.Foreground = Brush.Parse(dark ? "#E7EDF3" : "#252B31");
            preview.Child = sample; preview.BorderBrush = Brush.Parse(dark ? "#596675" : "#B7BFC7");
            message.Text = (original.LoadWarning is { } warning ? warning + "\n" : "") +
                "Предпросмотр. Нажмите «Применить», чтобы сохранить оформление.";
        }
        catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); }
    }
}
