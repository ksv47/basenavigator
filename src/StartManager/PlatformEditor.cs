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

    public PlatformEditor(Window owner, string executable, PlatformRule? rule, bool individual = false, string importedVersion = "", UiTypography? typography = null)
    {
        this.owner = owner; original = rule; Spacing = 8;
        TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        mode = new ComboBox { ItemsSource = new[] { individual ? "Ручной клиент / общая настройка" : "Ручной клиент", "Подбирать установленную версию" },
            SelectedIndex = rule is null ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(mode, "Способ выбора платформы");
        mode.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((value, _) => Label(value?.ToString() ?? ""));
        Children.Add(DialogLayout.Field("Выбор платформы:", mode));
        Client = new TextBox { Text = executable, PlaceholderText = individual ? "Пусто — общая настройка" : "Полный путь к 1cv8.exe / 1cv8" };
        AutomationProperties.SetName(Client, individual ? "Индивидуальный клиент 1С" : "Путь к клиенту 1С");
        var choose = SettingsWindow.Command("Выбрать клиент…", async () =>
        {
            try
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Клиент 1С", AllowMultiple = false });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { Client.Text = path; mode.SelectedIndex = 0; }
            }
            catch (Exception ex) { status!.Text = LegacyImport.ErrorMessage(ex); }
        });
        var file = new StackPanel { Spacing = 6 };
        file.Children.Add(Client); file.Children.Add(choose);
        Children.Add(DialogLayout.Field("Исполняемый файл:", file));
        var rules = new StackPanel { Spacing = 6 };
        version = new TextBox { Text = rule?.Version ?? (PlatformCatalog.IsVersion(importedVersion) ? importedVersion : "8.3") };
        AutomationProperties.SetName(version, "Версия для подбора");
        architecture = new ComboBox { ItemsSource = new[] { "Автоматически", "Только x86 · 32 бита", "Только x64 · 64 бита", "Только ARM64", "Предпочитать x86", "Предпочитать x64" },
            SelectedIndex = (int)(rule?.Architecture ?? PlatformArchitecture.Any), HorizontalAlignment = HorizontalAlignment.Stretch };
        // As the original's button of the version field: «Выбор версии платформы» fills the version and the bitness.
        var pick = SettingsWindow.Command("…", async () =>
        {
            var current = (PlatformArchitecture)architecture.SelectedIndex;
            if (await PlatformVersionWindow.Show(owner, typography ?? UiTypography.Default, version.Text?.Trim() ?? "", current, [Client.Text ?? ""]) is not { } choice) return;
            version.Text = choice.Version;
            // «Все» on a release keeps the bitness chosen here; a build or x32/x64 sets its own.
            if (choice.Architecture != PlatformArchitecture.Any) architecture.SelectedIndex = (int)choice.Architecture;
        });
        pick.Margin = new Thickness(6, 0, 0, 0);
        AutomationProperties.SetName(pick, "Выбор версии платформы"); ToolTip.SetTip(pick, "Выбор версии платформы");
        var versionRow = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right); versionRow.Children.Add(pick); versionRow.Children.Add(version);
        rules.Children.Add(DialogLayout.Field("Версия платформы:", versionRow));
        AutomationProperties.SetName(architecture, "Разрядность платформы"); rules.Children.Add(DialogLayout.Field("Разрядность:", architecture));
        architecture.ItemTemplate = mode.ItemTemplate;
        rules.Children.Add(Label(OperatingSystem.IsWindows() ? "При предпочтении сначала выбирается новейший релиз, затем разрядность. Автоматически — предпочтение x86, как в 1С." : "В Linux подбор ограничен архитектурой операционной системы."));
        rules.Children.Add(Label("8.3 / 8.3.27 — новейшая подходящая версия; полный номер — точное совпадение. При отсутствии совпадения запуск отменяется."));
        Children.Add(rules);
        var discovery = new StackPanel { Spacing = 8 };
        installed = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Выберите установленный клиент" };
        AutomationProperties.SetName(installed, "Установленные платформы"); discovery.Children.Add(installed);
        var useClient = SettingsWindow.Command("Использовать выбранный клиент", () =>
        {
            if (installed.SelectedItem is PlatformInstallation item) { Client.Text = item.Executable; mode.SelectedIndex = 0; }
        });
        useClient.IsEnabled = false;
        discovery.Children.Add(useClient);
        installed.SelectionChanged += (_, _) =>
        {
            useClient.IsEnabled = installed.SelectedItem is PlatformInstallation;
            if (installed.SelectedItem is PlatformInstallation item)
            {
                status!.Text = item.Executable;
                ToolTip.SetTip(installed, item.Executable);
            }
        };
        discovery.Children.Add(SettingsWindow.Command("Обновить установленные версии", async () => await Scan()));
        status = Label("Поиск в стандартных каталогах. Нестандартный клиент можно выбрать вручную."); discovery.Children.Add(status);
        var discovered = new Expander { Header = Label("Установленные версии платформы"), Content = discovery,
            IsExpanded = !individual, HorizontalAlignment = HorizontalAlignment.Stretch };
        Children.Add(discovered);
        if (individual) Children.Add(Label("Пустой ручной путь использует общую настройку. Индивидуальное правило имеет приоритет. Версию в самом списке .v8i меняет «Назначить версию» (Shift+Ctrl+F8)."));
        void Update()
        {
            rules.IsVisible = mode.SelectedIndex == 1; Client.IsEnabled = choose.IsEnabled = mode.SelectedIndex == 0;
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
        status.Text = "Поиск установленных платформ…";
        var path = Client.Text ?? "";
        try
        {
            var result = await Task.Run(() => PlatformCatalog.Scan(executables: [path]));
            installed.ItemsSource = result.Installations;
            status.Text = $"Найдено: {result.Installations.Count}. " + string.Join("\n", result.Warnings);
        }
        catch (Exception ex) { status.Text = "Поиск не выполнен: " + LegacyImport.ErrorMessage(ex); }
        finally { scanning = false; }
    }
}
