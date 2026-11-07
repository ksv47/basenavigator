using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

internal sealed class LaunchParametersWindow : Window
{
    /// <param name="editString">The window of «…» at «Дополнительные параметры» of the properties (the original's FormSelectParams
    /// opened by edtParamsRightButtonClick): nothing is launched, «Продолжить» gives back the string (<see cref="string"/>) for
    /// the field. The original shows its check boxes there too but takes only the string back; here they are hidden.</param>
    public LaunchParametersWindow(BaseEntry entry, bool designer, bool editString = false)
    {
        Title = designer ? "Параметры запуска конфигуратора" : "Параметры запуска 1С:Предприятие 8";
        // As the original FormSelectParams (543×464 at 8 pt): a compact form, one line per key of the catalog.
        Width = 620; Height = 540; MinWidth = 360; MinHeight = 320;
        // Without the launch check boxes the window is as tall as its content: no empty space above the buttons (Linux re-check 27.10).
        if (editString) SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var parameters = new TextBox { Text = entry.EffectiveParameters };
        AutomationProperties.SetName(parameters, "Строка параметров");
        body.Children.Add(DialogLayout.Field("Строка параметров", parameters));
        body.Children.Add(ParameterHint.For(parameters));
        body.Children.Add(DialogLayout.Label(editString
            ? "Значения с пробелами заключайте в двойные кавычки. «Продолжить» переносит строку в свойства базы; она сохраняется кнопкой «Сохранить»."
            : "Значения с пробелами заключайте в двойные кавычки. Настройки этого окна действуют только на один запуск."));

        var keys = new ListBox { ItemsSource = LaunchParameters.Catalog, SelectedIndex = 0, Height = 170,
            Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(keys, "Справочник параметров запуска");
        keys.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
            { Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)) } });
        // «Параметр | Описание» as the original grid; the key column follows the font, a long description ends with an ellipsis.
        // The width is taken from the font each key cell actually has (the dialog font is applied after this constructor) and in its
        // weight, and follows a change of it: /DebuggerURL at 22 pt no longer pushes its description out of line (Linux check 27.10).
        void FitKey(TextBlock cell)
        {
            void Fit() => cell.MinWidth = LaunchParameters.Catalog.Select(x => x.Key).Append("Параметр").Max(x => new FormattedText(x,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(cell.FontFamily, FontStyle.Normal, cell.FontWeight),
                cell.FontSize, Brushes.Black).Width) + 8;
            cell.AttachedToVisualTree += (_, _) => Fit();
            cell.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.FontSizeProperty || e.Property == TextBlock.FontFamilyProperty || e.Property == TextBlock.FontWeightProperty) Fit();
            };
        }
        keys.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LaunchParameters.Parameter>((item, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
            var key = new TextBlock { Text = item?.Key, FontWeight = FontWeight.SemiBold }; FitKey(key);
            var description = new TextBlock { Text = item?.Description, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(row, item?.Description);
            row.Children.Add(key); row.Children.Add(description); Grid.SetColumn(description, 1); return row;
        });
        // The original grid's headers «Параметр | Описание параметра»; the columns of the rows (6 px padding, the key's width).
        var keysHeader = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(7, 2, 7, 2) };
        var keyCaption = new TextBlock { Text = "Параметр", FontWeight = FontWeight.SemiBold }; FitKey(keyCaption);
        var descriptionCaption = new TextBlock { Text = "Описание параметра", FontWeight = FontWeight.SemiBold };
        keysHeader.Children.Add(keyCaption); Grid.SetColumn(descriptionCaption, 1); keysHeader.Children.Add(descriptionCaption);
        body.Children.Add(new Border { Child = keysHeader, Background = UiColors.Button, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0), Margin = new Thickness(0, 0, 0, -6) });
        body.Children.Add(keys);
        // No placeholder: it was cut in the narrow field beside «Добавить параметр» (Linux re-check 27.10); the caption says it.
        var value = new TextBox();
        AutomationProperties.SetName(value, "Значение параметра");
        ToolTip.SetTip(value, "Значение выбранного параметра: добавляется после ключа");
        var add = new Button { Content = "Добавить параметр" };
        keys.SelectionChanged += (_, _) => value.IsEnabled = keys.SelectedItem is LaunchParameters.Parameter { Value: true };
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        add.Click += (_, _) =>
        {
            if (keys.SelectedItem is not LaunchParameters.Parameter selected) return;
            var suffix = selected.Key + (selected.Value ? " " + LaunchParameters.Quote(value.Text ?? "") : "");
            parameters.Text = string.IsNullOrWhiteSpace(parameters.Text) ? suffix : parameters.Text.TrimEnd() + " " + suffix;
            error.Text = ""; parameters.Focus(); parameters.CaretIndex = parameters.Text.Length;
        };
        keys.DoubleTapped += (_, _) => add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var valueRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        valueRow.Children.Add(value); valueRow.Children.Add(add); Grid.SetColumn(add, 1);
        body.Children.Add(DialogLayout.Field("Значение:", valueRow));

        var admin = new CheckBox { Content = DialogLayout.Label("Запустить от имени администратора"),
            IsVisible = OperatingSystem.IsWindows(), IsEnabled = !Core.Launcher.IsElevated() };
        // A base started as administrator always is: the box is on and locked, as the original's FAppRunAsAdmin.
        if (entry.RunAsAdmin && OperatingSystem.IsWindows()) { admin.IsChecked = true; admin.IsEnabled = false; }
        var launchOnly = new StackPanel { Spacing = 6, IsVisible = !editString };
        body.Children.Add(launchOnly);
        launchOnly.Children.Add(admin);
        var useProcessing = new CheckBox { Content = DialogLayout.Label("Обработка:"),
            IsChecked = entry.ExternalProcessing.Length > 0, IsEnabled = !designer };
        AutomationProperties.SetName(useProcessing, "Запустить обработку");
        ToolTip.SetTip(useProcessing, "Запустить внешнюю обработку при открытии базы");
        var processing = new ProcessingEditor(this, entry.ExternalProcessing, error);
        processing.IsEnabled = !designer && useProcessing.IsChecked == true;
        useProcessing.IsCheckedChanged += (_, _) => processing.IsEnabled = !designer && useProcessing.IsChecked == true;
        launchOnly.Children.Add(DialogLayout.Field(useProcessing, processing));
        if (designer) launchOnly.Children.Add(DialogLayout.Label("Внешняя обработка выполняется только в режиме 1С:Предприятия."));
        var useVersion = new CheckBox { Content = DialogLayout.Label("Использовать версию:") };
        var version = new TextBox { Text = entry.PlatformRule?.Version ?? (PlatformCatalog.IsVersion(entry.PlatformVersion) ? entry.PlatformVersion : "8.3"), IsEnabled = false,
            MinWidth = 120 };
        AutomationProperties.SetName(version, "Версия платформы для одного запуска");
        var installed = new ComboBox { PlaceholderText = "Установленные версии", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        AutomationProperties.SetName(installed, "Установленные версии для одного запуска");
        installed.SelectionChanged += (_, _) => { if (installed.SelectedItem is string selected) version.Text = selected; };
        useVersion.IsCheckedChanged += (_, _) => version.IsEnabled = installed.IsEnabled = useVersion.IsChecked == true;
        // One line as in the original (chkUseVersion + cbVersions); it wraps on a narrow window or with a large font.
        installed.HorizontalAlignment = HorizontalAlignment.Left; installed.MinWidth = 200;
        var versionRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        useVersion.VerticalAlignment = version.VerticalAlignment = installed.VerticalAlignment = VerticalAlignment.Center;
        versionRow.Children.Add(useVersion); versionRow.Children.Add(version); versionRow.Children.Add(installed);
        launchOnly.Children.Add(versionRow);
        launchOnly.Children.Add(DialogLayout.Label("8.3 / 8.3.27 — новейшая подходящая версия; полный номер — точное совпадение. Разрядность — как в индивидуальном правиле базы, иначе автоматически."));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.Click += (_, _) => Close();
        var launch = new Button { Content = "Продолжить", IsDefault = true };
        var preparing = false;
        Closing += (_, e) => { if (preparing) e.Cancel = true; };
        launch.Click += async (_, _) =>
        {
            if (preparing) return;
            try
            {
                var text = parameters.Text ?? "";
                LaunchParameters.Validate(text);
                if (editString) { Close(text); return; }
                PlatformRule? platform = useVersion.IsChecked == true
                    ? (entry.PlatformRule ?? new PlatformRule()) with { Version = version.Text?.Trim() ?? "" } : null;
                platform?.Validate();
                var processingPath = useProcessing.IsChecked == true && !designer ? processing.PathText : "";
                if (useProcessing.IsChecked == true && !designer && processingPath.Length == 0)
                    throw new ArgumentException("Укажите файл внешней обработки или снимите флажок её запуска.");
                var options = new LaunchOptions(text, processingPath) { Platform = platform, Elevated = admin.IsChecked == true };
                preparing = true; body.IsEnabled = buttons.IsEnabled = false;
                await Task.Run(() => LaunchParameters.ValidateProcessing(processingPath));
                preparing = false; Close(options);
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
            finally { preparing = false; body.IsEnabled = buttons.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(launch);
        Content = DialogLayout.Frame(body, buttons, error);
        Opened += (_, _) => parameters.Focus();
        if (!editString) Opened += async (_, _) =>
        {
            try
            {
                var found = await Task.Run(() => PlatformCatalog.Scan(executables: [entry.Executable]));
                if (IsVisible) installed.ItemsSource = found.Installations.Select(x => x.Version).Distinct().OrderDescending().Select(x => x.ToString()).ToArray();
            }
            catch (Exception ex) { if (IsVisible) error.Text = "Поиск версий не выполнен: " + LegacyImport.ErrorMessage(ex); }
        };
    }
}

/// <summary>Shows under a parameters field that a switch has probably lost its value. Saving and launching stay allowed.</summary>
internal static class ParameterHint
{
    public static TextBlock For(TextBox parameters)
    {
        var hint = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        void Update()
        {
            var warning = parameters.IsEnabled ? LaunchParameters.MissingValueWarning(parameters.Text ?? "") : null;
            hint.Text = warning ?? ""; hint.IsVisible = warning is not null;
        }
        parameters.TextChanged += (_, _) => Update();
        parameters.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Input.InputElement.IsEnabledProperty) Update(); };
        Update();
        return hint;
    }
}

/// <summary>The same path picker in the base properties and the one-time launch dialog: «…» inside the field opens the choice
/// of a file, as the original's buttoned edit (edtEpfAddon, edtEpfFile).</summary>
internal sealed class ProcessingEditor : Decorator
{
    private readonly TextBox path;
    public string PathText => path.Text?.Trim() ?? "";
    public ProcessingEditor(Window owner, string initial, TextBlock error)
    {
        path = new TextBox { Text = initial, PlaceholderText = "Пусто — не запускать обработку" };
        AutomationProperties.SetName(path, "Внешняя обработка");
        path.InnerRightContent = DialogLayout.Ellipsis("Выбрать файл внешней обработки (*.epf, *.erf)", async () =>
        {
            try
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Внешняя обработка", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Обработка 1С") { Patterns = ["*.epf", "*.erf"] }, FilePickerFileTypes.All]
                });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } selected) path.Text = selected;
            }
            catch (Exception ex) { error.Text = LegacyImport.ErrorMessage(ex); }
        });
        Child = path;
    }
}
