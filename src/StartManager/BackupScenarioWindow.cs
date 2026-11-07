using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StartManager.Core;

namespace StartManager;

/// <summary>
/// The original's «Сценарий резервирования» (FormBackupScripts.dfm, _Unit186.pas:16707-17892): the sections on the left — «Описание
/// сценария», «Шаблон имени архива», «Место назначения», «Параметры выгрузки» — with the caption of the chosen one above the page;
/// «Закрыть» on the left, «OK» and «Применить» on the right. Not shown, as decided for this program: the tab «FTP» (a stub in the
/// original), RAR and the section «Дополнительные действия» (disabled there). Shown although the original hides it: the panel
/// «Параметры архивирования» (compression and volumes of a ZIP); added: «Хранить копий». «Применить» and «OK» save at once.
/// </summary>
internal sealed class BackupScenarioWindow : Window
{
    public const string Caption = "Сценарий резервирования";
    private const string FolderMessage = "Каталог содержит некорректные символы!";
    private static readonly string[] Sections = ["Описание сценария", "Шаблон имени архива", "Место назначения", "Параметры выгрузки"];
    // The original's list of compression methods and of volume presets (cbCompression, cbSplitVolumePreset).
    private static readonly string[] Compressions = ["SAVE", "FAST", "NORMAL", "MAXIMUM", "ULTRA (много памяти)"];
    private static readonly string[] Splits = ["Без разделения", "Определить", "50 МБ", "100 МБ", "200 МБ", "500 МБ"];
    // What each macro stands for; the original's MemoInfo, in the order of BackupName.Macros.
    private static readonly string[] MacroHelp =
    [
        "{IBName} - Наименование информационной базы", "{Prefix} - Префикс из свойств информационной базы", "{Comp_Name} - Имя компьютера",
        "{Date2} - Дата в формате ГГММДД", "{Date4} - Дата в формате ГГГГММДД", "{Y2} - Год (две цифры)", "{Y4} - Год (четыре цифры)",
        "{MM} - Месяц (две цифры)", "{M} - Месяц", "{DD} - День (две цифры)", "{D} - День", "{HH} - Час (две цифры)", "{H} - Час",
        "{NN} - Минуты (две цифры)", "{N} - Минуты", "{SS} - Секунды (две цифры)", "{S} - Секунды"
    ];

    private BackupScenario scenario;
    private bool isNew, saving;
    private readonly Func<BackupScenario, Task<string?>> save;
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Error };
    private readonly ListBox navigation;
    private readonly TextBlock pageTitle = new() { FontWeight = FontWeight.SemiBold, Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer page = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Control[] pages;
    private readonly TextBox name, description, template, folder1, folder2, folder3;
    private readonly CheckBox useFolder1, useFolder2, useFolder3;
    private readonly RadioButton[] formats;
    private readonly ComboBox compression, split;
    private readonly NumericUpDown volumeSize, keepCopies;
    private readonly Control archivePanel;
    private readonly Control[] firstFields;
    private readonly Dictionary<BackupScenarioField, Control> fields;
    private BackupScenarioField? problemField;
    private readonly UiTypography typography;

    /// <summary>Whether a save succeeded while the window was open (the list behind it is read again).</summary>
    public bool Saved { get; private set; }

    /// <param name="save">Writes the scenario; returns the error to show or null.</param>
    public BackupScenarioWindow(BackupScenario scenario, bool isNew, Func<BackupScenario, Task<string?>> save, UiTypography typography)
    {
        this.scenario = scenario; this.isNew = isNew; this.save = save; this.typography = typography;
        Width = 640; Height = 440; MinWidth = 380; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        UpdateTitle();
        Closing += (_, e) => { if (saving) e.Cancel = true; };

        // 1. «Описание сценария».
        name = Field("Наименование", scenario.Name);
        description = Field("Краткое описание", scenario.Description);
        var previousName = scenario.Name;
        name.TextChanged += (_, _) =>
        {
            var typed = name.Text ?? "";
            description.Text = BackupScenarioEdit.FollowName(description.Text ?? "", previousName, typed);
            previousName = typed;
        };
        var describe = Panel();
        describe.Children.Add(DialogLayout.Label("Наименование:")); describe.Children.Add(name);
        describe.Children.Add(DialogLayout.Label("Краткое описание:")); describe.Children.Add(description);
        describe.Children.Add(Muted("Пустое описание повторяет наименование."));

        // 2. «Шаблон имени архива».
        template = Field("Шаблон имени файла архива", scenario.Template);
        template.LostFocus += (_, _) => ShowTemplateProblem();
        var naming = Panel();
        naming.Children.Add(DialogLayout.Label("Шаблон имени файла архива:")); naming.Children.Add(template);
        naming.Children.Add(DialogLayout.Label("В имени файла можно использовать следующие переменные:"));
        var help = new SelectableTextBlock { Text = string.Join(Environment.NewLine, MacroHelp), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(help, "Переменные шаблона имени файла");
        naming.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Background = UiColors.Field, Padding = new Thickness(8, 6), Child = help });
        naming.Children.Add(Muted("Точки заменяются на «-», расширение добавляется по формату выгрузки; при пустом префиксе начальный «_» убирается."));

        // 3. «Место назначения»: the original's tab «Мой компьютер» (the tab «FTP» is not shown).
        useFolder1 = Check(" Выполнить выгрузку на компьютер или сетевой диск", scenario.UseFolder1, "Выполнить выгрузку на компьютер или сетевой диск");
        folder1 = FolderField("1. Каталог", scenario.Folder1);
        folder1.PlaceholderText = "Будет использован каталог по-умолчанию";
        ToolTip.SetTip(folder1, "Пусто — каталог из свойств базы, общий «Каталог по-умолчанию» или каталог Backup рядом с файловой базой");
        useFolder2 = Check(" 2. Каталог:", scenario.UseFolder2, "2. Каталог: использовать");
        folder2 = FolderField("2. Каталог", scenario.Folder2);
        useFolder3 = Check(" 3. Каталог:", scenario.UseFolder3, "3. Каталог: использовать");
        folder3 = FolderField("3. Каталог", scenario.Folder3);
        var otherCaption = DialogLayout.Label("Дополнительно выгрузить в другие каталоги:");
        var first = new StackPanel { Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
        first.Children.Add(DialogLayout.Label("1. Каталог:")); first.Children.Add(folder1);
        var others = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
        others.Children.Add(otherCaption);
        others.Children.Add(Indented(DialogLayout.Field(useFolder2, folder2)));
        others.Children.Add(Indented(DialogLayout.Field(useFolder3, folder3)));
        void Refresh()
        {
            // As the original's FormRefresh: the flag enables the folders; «2.» and «3.» also need their own flags.
            var on = useFolder1.IsChecked == true;
            folder1.IsEnabled = useFolder2.IsEnabled = useFolder3.IsEnabled = otherCaption.IsEnabled = on;
            otherCaption.Opacity = on ? 1 : .6;
            folder2.IsEnabled = on && useFolder2.IsChecked == true;
            folder3.IsEnabled = on && useFolder3.IsChecked == true;
        }
        useFolder1.IsCheckedChanged += (_, _) => Refresh(); useFolder2.IsCheckedChanged += (_, _) => Refresh(); useFolder3.IsCheckedChanged += (_, _) => Refresh();
        Refresh();
        foreach (var (box, field) in new[] { (folder1, BackupScenarioField.Folder1), (folder2, BackupScenarioField.Folder2), (folder3, BackupScenarioField.Folder3) })
            box.LostFocus += (_, _) => ShowFolderProblem(box, field);
        var target = Panel();
        target.Children.Add(useFolder1); target.Children.Add(first); target.Children.Add(others);
        target.Children.Add(Muted("Допускается использование всех переменных, применяемых для шаблона имени файла архива. " +
            "Готовый файл копируется в отмеченные каталоги «2.» и «3.» после выгрузки."));
        var destination = DialogLayout.Section("Мой компьютер", target);

        // 4. «Параметры выгрузки»: the original's rgFormat without RAR, its panel «Параметры архивирования» shown, and «Хранить копий».
        var formatCaptions = new[] { "Выгрузка информационной базы (*.dt)", "Выгрузка структуры конфигурации (*.cf)",
            "Архивирование данных ZIP (*.zip)  [только для файлового варианта]" };
        formats = [.. formatCaptions.Select((caption, index) =>
        {
            var radio = new RadioButton { Content = DialogLayout.Label(caption), GroupName = "backup-format", IsChecked = (int)scenario.Format == index };
            AutomationProperties.SetName(radio, caption); return radio;
        })];
        compression = Choice("Сжатие", Compressions, (int)scenario.Compression);
        split = Choice("Разделение", Splits, (int)scenario.Split);
        // No range on the controls: a typed value out of range is refused by the check with its message, never clamped or reverted
        // silently into another policy (Grok audit, part 3).
        volumeSize = new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Increment = 1, FormatString = "0", Value = scenario.VolumeSize,
            ClipValueToMinMax = false, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(volumeSize, "Размер тома, МБ");
        keepCopies = new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Increment = 1, FormatString = "0", Value = scenario.KeepCopies,
            ClipValueToMinMax = false, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(keepCopies, "Хранить копий");
        var volumeRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        volumeRow.Children.Add(new TextBlock { Text = "Размер тома:", VerticalAlignment = VerticalAlignment.Center });
        volumeRow.Children.Add(volumeSize);
        volumeRow.Children.Add(new TextBlock { Text = "МБ", VerticalAlignment = VerticalAlignment.Center });
        var archive = Panel();
        archive.Children.Add(DialogLayout.Field("Сжатие:", compression));
        archive.Children.Add(DialogLayout.Field("Разделение:", split));
        archive.Children.Add(DialogLayout.Field(new TextBlock(), volumeRow));
        archive.Children.Add(Muted("Тома — части «имя.zip.001», «.002»…; их открывает 7-Zip. Для форматов DT и CF эти параметры не применяются."));
        archivePanel = DialogLayout.Section("Параметры архивирования", archive);
        void Archive()
        {
            // The panel stays in view; its fields apply to a ZIP only. «Размер тома» is for «Определить», as in the original.
            var zip = formats[2].IsChecked == true;
            compression.IsEnabled = split.IsEnabled = zip;
            volumeSize.IsEnabled = zip && split.SelectedIndex == (int)BackupSplit.Custom;
        }
        foreach (var radio in formats) radio.IsCheckedChanged += (_, _) => Archive();
        split.SelectionChanged += (_, _) => Archive();
        Archive();
        var formatList = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var radio in formats) formatList.Children.Add(radio);
        // The arrows move between the formats as in the original's radio group.
        foreach (var radio in formats)
            radio.KeyDown += (_, e) =>
            {
                var step = e.Key switch { Key.Down or Key.Right => 1, Key.Up or Key.Left => -1, _ => 0 };
                if (step == 0 || e.KeyModifiers != KeyModifiers.None) return;
                var next = formats[(Array.IndexOf(formats, radio) + step + formats.Length) % formats.Length];
                next.IsChecked = true; next.Focus(); e.Handled = true;
            };
        var keep = Panel();
        keep.Children.Add(DialogLayout.Field("Хранить копий:", keepCopies));
        keep.Children.Add(Muted("0 — хранить все. После успешной выгрузки удаляются самые старые копии этой базы по этому сценарию сверх указанного числа " +
            "(только файлы из списка выгрузок, во всех каталогах сценария)."));
        var unload = new StackPanel { Spacing = 16 };
        unload.Children.Add(DialogLayout.Section("Варианты форматов выгрузки данных", formatList));
        unload.Children.Add(archivePanel);
        unload.Children.Add(DialogLayout.Section("Хранение копий", keep));

        pages = [describe, naming, destination, unload];
        // The original's FormSetFocus: «1. Каталог» on the destination page when it can take the focus, else the flag above it.
        firstFields = [name, template, folder1, formats[0]];
        fields = new()
        {
            [BackupScenarioField.Name] = name, [BackupScenarioField.Description] = description, [BackupScenarioField.Template] = template,
            [BackupScenarioField.Folder1] = folder1, [BackupScenarioField.Folder2] = folder2, [BackupScenarioField.Folder3] = folder3,
            [BackupScenarioField.Format] = formats[0], [BackupScenarioField.VolumeSize] = volumeSize, [BackupScenarioField.KeepCopies] = keepCopies
        };
        // A problem shown goes as soon as the field it is about (or a control that decides it) changes (Grok audit, part 3).
        name.TextChanged += (_, _) => Changed(BackupScenarioField.Name);
        description.TextChanged += (_, _) => Changed(BackupScenarioField.Description);
        template.TextChanged += (_, _) => Changed(BackupScenarioField.Template);
        folder1.TextChanged += (_, _) => Changed(BackupScenarioField.Folder1);
        folder2.TextChanged += (_, _) => Changed(BackupScenarioField.Folder2);
        folder3.TextChanged += (_, _) => Changed(BackupScenarioField.Folder3);
        useFolder1.IsCheckedChanged += (_, _) => Changed(BackupScenarioField.Folder1, BackupScenarioField.Folder2, BackupScenarioField.Folder3);
        useFolder2.IsCheckedChanged += (_, _) => Changed(BackupScenarioField.Folder2);
        useFolder3.IsCheckedChanged += (_, _) => Changed(BackupScenarioField.Folder3);
        foreach (var radio in formats) radio.IsCheckedChanged += (_, _) => Changed(BackupScenarioField.Format, BackupScenarioField.VolumeSize);
        split.SelectionChanged += (_, _) => Changed(BackupScenarioField.VolumeSize);
        volumeSize.ValueChanged += (_, _) => Changed(BackupScenarioField.VolumeSize);
        keepCopies.ValueChanged += (_, _) => Changed(BackupScenarioField.KeepCopies);

        // The original's button group has no numbers; Alt+1…Alt+4 still choose the sections.
        navigation = new ListBox { ItemsSource = Sections, SelectedIndex = 0,
            Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        navigation.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => DialogLayout.Label(value ?? ""));
        AutomationProperties.SetName(navigation, "Разделы сценария");
        ToolTip.SetTip(navigation, "Alt+1 — Описание сценария; Alt+2 — Шаблон имени архива; Alt+3 — Место назначения; Alt+4 — Параметры выгрузки");
        navigation.SelectionChanged += (_, _) => ShowSection(Math.Max(0, navigation.SelectedIndex), false);
        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        right.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 5), Child = pageTitle });
        Grid.SetRow(page, 1); right.Children.Add(page);
        var body = new Grid { ColumnDefinitions = new("190,*"), RowDefinitions = new("Auto,*"), ColumnSpacing = 12, RowSpacing = 10 };
        body.Children.Add(navigation); Grid.SetRowSpan(navigation, 2);
        Grid.SetColumn(right, 1); Grid.SetRowSpan(right, 2); body.Children.Add(right);
        // A narrow window puts the sections above the page, as the base properties do.
        body.SizeChanged += (_, _) =>
        {
            var narrow = body.Bounds.Width < 560;
            var width = Math.Max(170, Sections.Max(x => UiTypography.Measure(x, FontFamily, FontSize).Width) + 36);
            body.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(width);
            body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRowSpan(navigation, narrow ? 1 : 2); navigation.MaxHeight = narrow ? Math.Max(100, FontSize * 4 + 24) : double.PositiveInfinity;
            Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0); Grid.SetRowSpan(right, narrow ? 1 : 2);
        };

        // As the original: «Закрыть» on the left, «OK» and «Применить» on the right.
        var close = SettingsWindow.Command("Закрыть", () => Close()); close.IsCancel = true;
        var ok = SettingsWindow.Command("OK", async () => { if (await Commit()) Close(); });
        var apply = SettingsWindow.Command("Применить", async () => await Commit());
        ToolTip.SetTip(ok, "Сохранить сценарий и закрыть окно · Ctrl+Enter");
        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Left); buttons.Children.Add(close);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2 };
        actions.Children.Add(ok); actions.Children.Add(apply);
        buttons.Children.Add(actions);
        foreach (var button in new[] { close, ok, apply }) button.MinWidth = 90;
        // The original's TabOrder: «OK», «Применить», then «Закрыть» (on the left).
        KeyboardNavigation.SetTabIndex(actions, 0); KeyboardNavigation.SetTabIndex(close, 1);
        Content = DialogLayout.Frame(body, buttons, message, scrollBody: false);

        KeyDown += async (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.D1 and <= Key.D4) { navigation.SelectedIndex = e.Key - Key.D1; e.Handled = true; }
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.NumPad1 and <= Key.NumPad4) { navigation.SelectedIndex = e.Key - Key.NumPad1; e.Handled = true; }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; if (await Commit()) Close(); }
        };
        typography.ApplyDialog(this);
        MinWidth *= UiTypography.DialogScale(typography.DialogSize); MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        ShowSection(0, false);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => { name.Focus(); name.SelectAll(); }, DispatcherPriority.Input);
    }

    private void UpdateTitle() => Title = isNew ? Caption + " (Новый)" : Caption;

    private void ShowSection(int index, bool focus)
    {
        if (navigation.SelectedIndex != index) navigation.SelectedIndex = index;
        page.Content = pages[index]; pageTitle.Text = Sections[index];
        // The original's FormSetFocus: the first field of the section.
        if (focus) Focus(firstFields[index], index);
    }

    /// <summary>Focuses a field, or the first one of its section that can take the focus (a disabled «1. Каталог» gives it to its flag).</summary>
    private void Focus(Control target, int section) => Dispatcher.UIThread.Post(() =>
    {
        if (target.IsEffectivelyEnabled) target.Focus();
        else if (firstFields[section].IsEffectivelyEnabled) firstFields[section].Focus();
        else if (section == 2) useFolder1.Focus();
    }, DispatcherPriority.Input);

    private void ShowProblem(string text, BackupScenarioField field) { message.Foreground = UiColors.Error; message.Text = text; problemField = field; }

    private void Changed(params BackupScenarioField[] changed)
    {
        if (problemField is { } shown && changed.Contains(shown)) { message.Text = ""; problemField = null; }
    }

    /// <summary>The scenario as the fields give it; the ID and unknown fields stay.</summary>
    private BackupScenario Read() => scenario with
    {
        Name = Text(name), Description = Text(description), Template = Text(template),
        UseFolder1 = useFolder1.IsChecked == true, Folder1 = Text(folder1),
        UseFolder2 = useFolder2.IsChecked == true, Folder2 = Text(folder2),
        UseFolder3 = useFolder3.IsChecked == true, Folder3 = Text(folder3),
        Format = (BackupFormat)Math.Max(0, Array.FindIndex(formats, x => x.IsChecked == true)),
        Compression = (BackupCompression)Math.Max(0, compression.SelectedIndex), Split = (BackupSplit)Math.Max(0, split.SelectedIndex),
        // Commit refuses an empty or fractional number that applies (NumberProblem) before this; one that does not apply (the size of a
        // DT/CF or of an archive not split by «Определить») reads as an impossible value and keeps the saved one (Normalize, the model's check).
        VolumeSize = volumeSize.Value is { } volume && volume == decimal.Truncate(volume) && volume is >= 0 and <= BackupScenario.MaxVolumeSize ? (int)volume : -1,
        KeepCopies = keepCopies.Value is { } kept && kept == decimal.Truncate(kept) && kept is >= 0 and <= BackupScenario.MaxKeepCopies ? (int)kept : -1
    };

    // Also a stored text that came with control characters (an inactive folder included) is saved without them.
    private static string Text(TextBox box) => BackupScenarioEdit.WithoutControls(box.Text ?? "").Trim();

    private async Task<bool> Commit()
    {
        if (saving) return false;
        var zip = formats[2].IsChecked == true;
        var numbers = BackupScenarioEdit.NumberProblem(keepCopies.Value, volumeSize.Value, zip && split.SelectedIndex == (int)BackupSplit.Custom);
        var read = Read();
        // A size that does not apply and is not a valid number keeps the saved one, also for a ZIP not split by «Определить».
        if (read.VolumeSize < 0) read = read with { VolumeSize = scenario.VolumeSize };
        var next = BackupScenarioEdit.Normalize(read, scenario);
        if ((numbers ?? BackupScenarioEdit.Problem(next, OperatingSystem.IsWindows())) is { } problem)
        {
            ShowProblem(problem.Message, problem.Field);
            ShowSection(problem.Section, false);
            Focus(problem.Field == BackupScenarioField.Format ? formats.FirstOrDefault(x => x.IsChecked == true) ?? formats[0] : fields[problem.Field], problem.Section);
            return false;
        }
        saving = true; IsEnabledForSave(false);
        message.Foreground = UiColors.Muted; message.Text = "Сохранение сценария…"; problemField = null;
        try
        {
            var failure = await save(next);
            if (failure is not null) { message.Foreground = UiColors.Error; message.Text = failure; return false; }
            scenario = next; isNew = false; Saved = true; UpdateTitle();
            message.Foreground = UiColors.Muted; message.Text = "Сценарий сохранён.";
            return true;
        }
        finally { saving = false; IsEnabledForSave(true); }
    }

    private void IsEnabledForSave(bool enabled) { if (Content is Control content) content.IsEnabled = enabled; }

    private void ShowTemplateProblem()
    {
        if (BackupScenarioEdit.TemplateMessage(template.Text ?? "") is { } wrong) ShowProblem(wrong, BackupScenarioField.Template);
    }

    private void ShowFolderProblem(TextBox box, BackupScenarioField field)
    {
        if (box.IsEffectivelyEnabled && BackupScenarioEdit.InvalidFolder(box.Text ?? "", OperatingSystem.IsWindows())) ShowProblem(FolderMessage, field);
    }

    private static StackPanel Panel() => new() { Spacing = 8 };
    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted };
    private static Control Indented(Control control) { control.Margin = new Thickness(16, 0, 0, 0); return control; }

    private static TextBox Field(string automation, string value)
    {
        var box = new TextBox { Text = value };
        AutomationProperties.SetName(box, automation);
        // Control characters (a pasted tab or line break) are dropped as they come in: the model refuses them in every field.
        box.TextChanged += (_, _) =>
        {
            if (box.Text is not { } text || BackupScenarioEdit.WithoutControls(text) is var clean && clean == text) return;
            var caret = Math.Max(0, box.CaretIndex - (text.Length - clean.Length));
            box.Text = clean; box.CaretIndex = Math.Min(caret, clean.Length);
        };
        return box;
    }

    private static CheckBox Check(string caption, bool on, string automation)
    {
        var box = new CheckBox { Content = DialogLayout.Label(caption), IsChecked = on };
        AutomationProperties.SetName(box, automation); return box;
    }

    private static ComboBox Choice(string automation, string[] items, int selected)
    {
        var box = new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(selected, 0, items.Length - 1), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, automation); return box;
    }

    /// <summary>The original's TKAVButtonedEdit: a folder glyph on the left and «…» on the right that picks the folder.</summary>
    private TextBox FolderField(string automation, string value)
    {
        var box = Field(automation, value);
        var glyph = UiIcons.Create("folder", typography.IconSize); glyph.Margin = new Thickness(6, 0, 0, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        box.InnerLeftContent = glyph;
        var browse = new Button { Content = "…", Padding = new Thickness(8, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(browse, "Выбрать каталог"); AutomationProperties.SetName(browse, "Выбрать каталог: " + automation);
        browse.Click += async (_, _) =>
        {
            try
            {
                var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = automation, AllowMultiple = false });
                if (chosen.FirstOrDefault()?.TryGetLocalPath() is { } path) box.Text = path;
            }
            catch (Exception ex) { message.Foreground = UiColors.Error; message.Text = LegacyImport.ErrorMessage(ex); }
        };
        box.InnerRightContent = browse;
        return box;
    }

    /// <summary>A question of the scenario pages with «Да» and «Нет»; <paramref name="safeDefault"/> makes «Нет» the default answer.</summary>
    public static async Task<bool> Ask(Window owner, UiTypography typography, string title, string text, bool safeDefault)
    {
        var box = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var yes = SettingsWindow.Command("Да", () => box.Close(true));
        var no = SettingsWindow.Command("Нет", () => box.Close(false)); no.IsCancel = true;
        (safeDefault ? no : yes).IsDefault = true;
        buttons.Children.Add(yes); buttons.Children.Add(no);
        box.Content = DialogLayout.Frame(DialogLayout.Label(text), buttons);
        typography.ApplyDialog(box);
        box.CanResize = false;
        box.Opened += (_, _) => Dispatcher.UIThread.Post(() => (safeDefault ? no : yes).Focus(), DispatcherPriority.Input);
        return await box.ShowDialog<bool>(owner);
    }
}
