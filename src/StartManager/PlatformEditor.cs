using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

internal sealed class PlatformEditor : StackPanel
{
    private readonly ComboBox mode, architecture, installed;
    private readonly TextBox version;
    private readonly TextBlock status;
    private readonly PlatformRule? original;
    private readonly Window owner;
    private bool scanning;
    public TextBox Client { get; }
    public event Action? Changed;

    /// <remarks>The compact layout of the dialogs (settings look 11.11.2026, the base properties since step 3a 12.11): one row per field
    /// as the original's «Настройки платформы», «…» inside the path and version fields, the installed versions in one row with icon
    /// buttons and the explanations in tooltips that the keyboard opens too.</remarks>
    /// <param name="caption">The caption of the first row, e.g. «Платформа 1С 8.3:» of a branch (bold grey, as
    /// the original's lbRunMode83); none: «Выбор платформы:».</param>
    public PlatformEditor(Window owner, string executable, PlatformRule? rule, bool individual = false, string importedVersion = "", UiTypography? typography = null,
        string? caption = null, string? modeTip = null)
    {
        this.owner = owner; original = rule; Spacing = 6;
        TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        mode = new ComboBox { ItemsSource = new[] { individual ? "Ручной клиент / общая настройка" : "Ручной клиент", "Подбирать установленную версию" },
            SelectedIndex = rule is null ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(mode, "Способ выбора платформы");
        Client = new TextBox { Text = executable, PlaceholderText = individual ? "Пусто — общая настройка" : "Полный путь к 1cv8.exe / 1cv8" };
        AutomationProperties.SetName(Client, individual ? "Индивидуальный клиент 1С" : "Путь к клиенту 1С");
        async Task ChooseClient()
        {
            try
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Клиент 1С", AllowMultiple = false });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { Client.Text = path; mode.SelectedIndex = 0; }
            }
            catch (Exception ex) { status!.Text = LegacyImport.ErrorMessage(ex); status.IsVisible = true; }
        }
        version = new TextBox { Text = rule?.Version ?? (PlatformCatalog.IsVersion(importedVersion) ? importedVersion : "8.3") };
        AutomationProperties.SetName(version, "Версия для подбора");
        architecture = new ComboBox { ItemsSource = new[] { "Автоматически", "Только x86 · 32 бита", "Только x64 · 64 бита", "Только ARM64", "Предпочитать x86", "Предпочитать x64" },
            SelectedIndex = (int)(rule?.Architecture ?? PlatformArchitecture.Any), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(architecture, "Разрядность платформы");
        // As the original's button of the version field: «Выбор версии платформы» fills the version and the bitness.
        async Task PickVersion()
        {
            var current = (PlatformArchitecture)architecture.SelectedIndex;
            if (await PlatformVersionWindow.Show(owner, typography ?? UiTypography.Default, version.Text?.Trim() ?? "", current, [Client.Text ?? ""]) is not { } choice) return;
            version.Text = choice.Version;
            // «Все» on a release keeps the bitness chosen here; a build or x32/x64 sets its own.
            if (choice.Architecture != PlatformArchitecture.Any) architecture.SelectedIndex = (int)choice.Architecture;
        }
        var bitnessNote = OperatingSystem.IsWindows() ? "При предпочтении сначала выбирается новейший релиз, затем разрядность. Автоматически — предпочтение x86, как в 1С." : "В Linux подбор ограничен архитектурой операционной системы.";
        const string VersionNote = "8.3 / 8.3.27 — новейшая подходящая версия; полный номер — точное совпадение. При отсутствии совпадения запуск отменяется.";
        installed = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Выберите установленный клиент" };
        AutomationProperties.SetName(installed, "Установленные платформы");
        var useClient = SettingsWindow.Command("Использовать выбранный клиент", () =>
        {
            if (installed.SelectedItem is PlatformInstallation item) { Client.Text = item.Executable; mode.SelectedIndex = 0; }
        });
        useClient.IsEnabled = false;
        installed.SelectionChanged += (_, _) =>
        {
            useClient.IsEnabled = installed.SelectedItem is PlatformInstallation;
            if (installed.SelectedItem is PlatformInstallation item)
            {
                // The path in the tooltip only; the status line is for warnings.
                ToolTip.SetTip(installed, item.Executable);
            }
        };
        var refresh = SettingsWindow.Command("Обновить установленные версии", async () => await Scan());
        status = Label("Поиск в стандартных каталогах. Нестандартный клиент можно выбрать вручную.");
        const string IndividualNote = "Пустой ручной путь использует общую настройку. Индивидуальное правило имеет приоритет. Версию в самом списке .v8i меняет «Назначить версию» (Shift+Ctrl+F8).";
        Control rules;
        var type = typography ?? UiTypography.Default;
        Control first = caption is null ? Label("Выбор платформы:")
            : new TextBlock { Text = caption, FontWeight = FontWeight.Bold, Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };
        // One line per item, as the drop-down lists of the original.
        mode.ItemTemplate = architecture.ItemTemplate = DialogLayout.OneLine;
        DialogLayout.FitChoices(mode, type); DialogLayout.FitChoices(architecture, type);
        if (modeTip is not null) { ToolTip.SetTip(mode, modeTip); DialogLayout.TipOnFocus(mode); }
        Children.Add(DialogLayout.Field(first, mode));
        DialogLayout.PathBox(Client, "file", type.DialogIconSize, "Выбрать клиент…", ChooseClient);
        ToolTip.SetTip(Client, individual ? IndividualNote : "Этот клиент используется по умолчанию. Индивидуальный клиент базы задаётся в её свойствах.");
        DialogLayout.TipOnFocus(Client);
        Children.Add(DialogLayout.Field("Исполняемый файл:", Client));
        version.InnerRightContent = DialogLayout.Ellipsis("Выбор версии платформы", PickVersion);
        ToolTip.SetTip(version, individual ? VersionNote : VersionNote + " Полный номер сборки из списка базы (например, 8.3.27.1989) общая версия не заменяет."); ToolTip.SetTip(architecture, bitnessNote);
        DialogLayout.TipOnFocus(version); DialogLayout.TipOnFocus(architecture);
        var compactRules = DialogLayout.Rows();
        compactRules.Children.Add(DialogLayout.Field("Версия платформы:", version));
        compactRules.Children.Add(DialogLayout.Field("Разрядность:", architecture));
        Children.Add(rules = compactRules);
        // «Установленные версии платформы»: the list and its two commands in one row, as the original's list buttons.
        var row = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 4, RowSpacing = 0 };
        row.Children.Add(installed);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        icons.Children.Add(DialogLayout.Icon(useClient, "way", "Использовать выбранный клиент", type.DialogIconSize));
        icons.Children.Add(DialogLayout.Icon(refresh, "refresh", "Обновить установленные версии", type.DialogIconSize));
        Grid.SetColumn(icons, 1); row.Children.Add(icons);
        // A narrow row gives the list its whole width and puts the two buttons under it (Linux check 11.11, font 22).
        row.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < type.DialogSize * 20;
            Grid.SetColumn(icons, stacked ? 0 : 1); Grid.SetRow(icons, stacked ? 1 : 0);
            icons.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
            row.RowSpacing = stacked ? 4 : 0; row.ColumnSpacing = stacked ? 0 : 4;
        };
        // The count is the content of the empty list: the row goes under its caption before «Выберите (найдено: N)» is cut
        // (Linux check 12.11, Л3: DejaVu Sans is wider).
        row.SetValue(DialogLayout.PreferredWidthProperty, Math.Ceiling(UiTypography.Measure("Выберите (найдено: 99)", type.DialogFamily, type.DialogSize).Width)
            + 48 + 2 * (type.DialogIconSize + 2 * Math.Round(Math.Max(3, type.DialogIconSize / 4)) + 2) + 8);
        Children.Add(DialogLayout.Field("Установленные версии:", row));
        status.Foreground = UiColors.Muted; status.FontSize = Math.Max(10, type.DialogSize - 1); status.IsVisible = false;
        Children.Add(status);
        void Update()
        {
            rules.IsVisible = mode.SelectedIndex == 1; Client.IsEnabled = mode.SelectedIndex == 0;
            Changed?.Invoke();
        }
        mode.SelectionChanged += (_, _) => Update();
        Client.TextChanged += (_, _) => Changed?.Invoke(); version.TextChanged += (_, _) => Changed?.Invoke(); architecture.SelectionChanged += (_, _) => Changed?.Invoke();
        Update();
        AttachedToVisualTree += async (_, _) => await Scan();
    }

    public PlatformRule? ReadRule()
    {
        if (mode.SelectedIndex == 0) return null;
        var rule = (original ?? new PlatformRule()) with { Version = version.Text?.Trim() ?? "", Architecture = (PlatformArchitecture)architecture.SelectedIndex };
        rule.Validate(); return rule;
    }

    private async Task Scan()
    {
        if (scanning) return; scanning = true;
        const string Searching = "Поиск установленных платформ…";
        installed.PlaceholderText = Searching;
        var path = Client.Text ?? "";
        try
        {
            var result = await Task.Run(() => PlatformCatalog.Scan(executables: [path]));
            installed.ItemsSource = result.Installations;
            // The count in the empty list itself; the status line only for the warnings of the search.
            installed.PlaceholderText = $"Выберите (найдено: {result.Installations.Count})";
            status.Text = string.Join("\n", result.Warnings); status.IsVisible = result.Warnings.Count > 0;
        }
        catch (Exception ex)
        {
            status.Text = "Поиск не выполнен: " + LegacyImport.ErrorMessage(ex); status.IsVisible = true;
            installed.PlaceholderText = "Выберите установленный клиент";
        }
        finally { scanning = false; }
    }
}
