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
    private readonly CheckBox groupsOnTop = new() { Content = new TextBlock { Text = "Отображать группы сверху", TextWrapping = TextWrapping.Wrap } };
    private readonly Border preview = new() { Padding = new Thickness(8), BorderThickness = new Thickness(1) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private bool updating;

    public AppearanceEditor(AppearanceSettings settings)
    {
        original = settings;
        var type = UiTypography.From(settings);
        FontFamily = type.DialogFamily; FontSize = type.DialogSize;
        var size = type.DialogSize;
        // The original's style panel (smFormInterface): «Стиль» with «Шрифт:» and «Размер:» on one line, «Выделение активной строки»,
        // «Настройка дерева списка», «Сортировка списка»; the theme and the fonts of the other roles are ours.
        var style = DialogLayout.Rows();
        style.Children.Add(FontEditor("list", "Шрифт списка", "Шрифт:", false));
        theme.HorizontalAlignment = HorizontalAlignment.Left; theme.ItemTemplate = DialogLayout.OneLine;
        theme.MinWidth = Math.Ceiling(UiTypography.Measure("Системная", type.DialogFamily, size).Width) + 48;
        // «По умолчанию» at the right of the theme: it resets every field of the page.
        var reset = SettingsWindow.Command("По умолчанию", () => { resetRequested = true; Load(original.Reset()); });
        ToolTip.SetTip(reset, "Вернуть шрифты, тему и оформление списка по умолчанию");
        reset.Margin = default;
        // A narrow page puts the button under the theme instead of cutting its caption (Codex audit 11.11).
        var themeRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        themeRow.Children.Add(theme); themeRow.Children.Add(reset);
        style.Children.Add(DialogLayout.Field("Тема:", themeRow));
        var extra = DialogLayout.Rows();
        extra.Children.Add(FontEditor("ui", "Меню и панели", "Меню и панели:", true));
        extra.Children.Add(FontEditor("headers", "Заголовки колонок", "Заголовки колонок:", true));
        extra.Children.Add(FontEditor("dialogs", "Диалоги", "Диалоги:", true));
        var selection = DialogLayout.Rows();
        selection.Children.Add(DialogLayout.Field("Интенсивность выделения:", intensity));
        rowHeight.MinWidth = Math.Round(FontSize * 5); rowHeight.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(rowHeight, "Минимальная высота строки: строка автоматически увеличится, если выбранному шрифту нужно больше места.");
        var tree = DialogLayout.Rows();
        // As the original: the check box on the line of the row height; under it when the page is narrow.
        var heightRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 6 };
        heightRow.Children.Add(rowHeight); heightRow.Children.Add(tooltips);
        var heightField = (Grid)DialogLayout.Field("Высота строки:", heightRow);
        // When the check box goes under the field, the caption stays at the field's line.
        heightField.Children[0].VerticalAlignment = VerticalAlignment.Top;
        heightField.Children[0].Margin = new Thickness(0, Math.Max(0, Math.Floor((type.ControlHeight - UiTypography.Measure("Йр", type.DialogFamily, size).Height) / 2)), 0, 0);
        tree.Children.Add(heightField);
        var sorting = DialogLayout.Rows();
        ToolTip.SetTip(groupsOnTop, "При сортировке по наименованию");
        sorting.Children.Add(groupsOnTop);
        AutomationProperties.SetName(intensity, "Интенсивность выделения");
        AutomationProperties.SetName(tooltips, "Отображать всплывающие подсказки"); AutomationProperties.SetName(groupsOnTop, "Отображать группы сверху");
        AutomationProperties.SetName(theme, "Тема оформления");
        AutomationProperties.SetName(rowHeight, "Минимальная высота строки");
        var end = DialogLayout.Rows();
        end.Children.Add(preview);
        // Only a warning of the saved profile or an error of a field: the page says nothing by default.
        message.Foreground = UiColors.Error;
        end.Children.Add(message);
        var panel = DialogLayout.Groups(DialogLayout.Group("Стиль", style, size), DialogLayout.Group("Шрифты интерфейса и диалогов", extra, size),
            DialogLayout.Group("Выделение активной строки", selection, size), DialogLayout.Group("Настройка дерева списка", tree, size),
            DialogLayout.Group("Сортировка списка", sorting, size), end);
        DialogLayout.AlignCaptions(panel);
        Content = panel;
        theme.SelectionChanged += (_, _) => RefreshPreview(); rowHeight.ValueChanged += (_, _) => RefreshPreview();
        intensity.ValueChanged += (_, _) => RefreshPreview();
        tooltips.IsCheckedChanged += (_, _) => RefreshPreview(); groupsOnTop.IsCheckedChanged += (_, _) => RefreshPreview();
        rowHeight.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) RefreshPreview(); };
        Load(settings);
    }
    /// <summary>One row of a font: the caption, the family and «Размер:» with the size, as the original's «Шрифт: [] Размер: []».
    /// The font actually used is in the tooltip of the family; a line under the row says when the chosen one is not installed.</summary>
    private Control FontEditor(string key, string name, string caption, bool inherits)
    {
        var family = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, MaxDropDownHeight = 320, ItemTemplate = DialogLayout.OneLine };
        var choices = new[] { new FamilyChoice("", inherits ? "Как у списка" : "Системный шрифт") }
            .Concat(UiTypography.InstalledFonts.Select(x => new FamilyChoice(x, x))).ToList();
        family.ItemsSource = choices;
        var size = new NumericUpDown { Minimum = 10, Maximum = 24, Increment = 1, FormatString = "G", MinWidth = Math.Round(FontSize * 5) };
        AutomationProperties.SetName(family, name + ": семейство"); AutomationProperties.SetName(size, name + ": размер");
        var resolved = DialogLayout.Note("", FontSize);
        fonts.Add(key, (family, size, resolved));
        var fields = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6 };
        var sizeCaption = new TextBlock { Text = "Размер:", VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(family); Grid.SetColumn(sizeCaption, 1); fields.Children.Add(sizeCaption); Grid.SetColumn(size, 2); fields.Children.Add(size);
        // A narrow row keeps the family readable: «Размер:» gives way first (the size keeps its accessible name).
        fields.SizeChanged += (_, e) => sizeCaption.IsVisible = e.NewSize.Width >= FontSize * 22;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(DialogLayout.Field(caption, fields)); panel.Children.Add(resolved);
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
            {
                var available = requested.Length == 0 || requested.Equals(actual.Name, StringComparison.OrdinalIgnoreCase);
                var editor = fonts[key];
                editor.Resolved.Text = available ? "Используется " + actual.Name : requested + " недоступен → " + actual.Name;
                editor.Resolved.IsVisible = !available;
                ToolTip.SetTip(editor.Family, editor.Resolved.Text);
            }
            var sample = new StackPanel { Spacing = 6 };
            sample.Children.Add(new TextBlock { Text = $"Предпросмотр · высота строки {type.RowHeight:0}", FontFamily = type.Family, FontSize = type.InterfaceSize, FontWeight = FontWeight.SemiBold });
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
            sample.Children.Add(new TextBlock { Text = "Пример текста диалога", FontFamily = type.DialogFamily, FontSize = type.DialogSize, TextWrapping = TextWrapping.Wrap });
            foreach (var text in sample.Children.OfType<TextBlock>()) text.Foreground = Brush.Parse(dark ? "#E7EDF3" : "#252B31");
            preview.Child = sample; preview.BorderBrush = Brush.Parse(dark ? "#596675" : "#B7BFC7");
            message.Text = original.LoadWarning ?? "";
            message.IsVisible = message.Text.Length > 0;
        }
        catch (Exception ex) { message.Text = LegacyImport.ErrorMessage(ex); message.IsVisible = true; }
    }
}
