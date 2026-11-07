using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private Button? cacheButton;
    private Control? toolbarPanel;
    // The cache panel keeps its choice until the program closes; the original checks the user cache by default.
    private bool cacheUser = true, cacheProgram, cacheBusy;

    private async void OpenBaseFolder(BaseEntry entry, BaseCacheKind kind)
    {
        var persistent = PersistentIdentity(entry);
        var folders = await Task.Run(() => BaseCache.Find(entry, persistent, kind));
        if (closed) return;
        // 1C creates the folder at the first start of the base.
        if (folders.Count == 0) { status.Text = "Каталог базы ещё не создан: 1С создаёт его при первом запуске."; return; }
        try
        {
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folders[0]))) status.Text = $"Не удалось открыть каталог {folders[0]}.";
        }
        catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); status.Text = $"Не удалось открыть каталог {folders[0]}: {ex.Message}"; }
    }

    // As the original «Очистка временных файлов (Cache)»: a panel under the broom button, for the selected base
    // or for all bases of the list. Sizes are shown before anything is removed.
    private void ClearBaseCache()
    {
        if (closed || cacheButton is null) return;
        if (cacheBusy) { status.Text = "Очистка временных файлов ещё выполняется."; return; }
        if (loadFailed || reading) { status.Text = "Список занят или не загружен. Дождитесь завершения операции."; return; }
        var roots = BaseCacheRoots.Current;
        // Layout of FormCLearCache.dfm: bold title and a close button; on the left the accounts; on the right the tabs,
        // the platform version, two check boxes and two buttons with brushes; a hint line at the bottom.
        var panel = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, RowSpacing = 6, Margin = new Thickness(2) };
        // A plain popup: a Fluent flyout limits the width to 456 px and clips the panel at larger fonts.
        var popup = new Popup { Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            // Popup content does not inherit the window font: set the interface font explicitly.
            Child = new Border { Background = UiColors.Surface, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8),
                BoxShadow = BoxShadows.Parse("0 3 8 0 #40000000"), Child = panel } };
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(new TextBlock { Text = "Очистка временных файлов (Cache)", FontWeight = FontWeight.Bold, FontSize = typography.InterfaceSize + 1, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = UiIcons.Create("close", 12), Padding = new Thickness(6, 4), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(close, "Закрыть панель очистки"); close.Click += (_, _) => popup.IsOpen = false;
        Grid.SetColumn(close, 1); header.Children.Add(close); Grid.SetColumnSpan(header, 2); panel.Children.Add(header);

        // The original's «Учетные записи»: this user is checked; another account (Windows, from ProfileList) only for an administrator,
        // and only its program cache (Local) is cleared — never its settings (decision of the user 11.10.2026).
        var accounts = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };
        var elevated = Core.Launcher.IsElevated();
        var accountBoxes = new List<(CacheAccount Account, CheckBox Box)>();
        foreach (var account in CacheAccounts.List())
        {
            var box = new CheckBox { Content = account.Name, IsChecked = account.Current, IsEnabled = account.Current || elevated, MinHeight = 0 };
            AutomationProperties.SetName(box, "Учётная запись " + account.Name);
            ToolTip.SetTip(box, account.Current ? account.Profile
                : elevated ? account.Profile + "\nТолько программный кэш (Local); настройки пользователя не трогаются."
                : "Нужны права администратора: запустите БазоНавигатор от имени администратора.");
            accountBoxes.Add((account, box)); accounts.Children.Add(box);
        }
        var accountsGroup = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Width = Math.Max(160, typography.InterfaceSize * 14),
            Background = UiColors.Field, Child = new ScrollViewer { Content = accounts, MaxHeight = Math.Max(150, typography.RowHeight * 8) } };
        var accountsBox = new StackPanel { Spacing = 3 };
        accountsBox.Children.Add(new TextBlock { Text = "Учетные записи", FontWeight = FontWeight.Bold, Foreground = UiColors.Muted });
        accountsBox.Children.Add(accountsGroup); accountsGroup.MinHeight = 150;
        Grid.SetRow(accountsBox, 1); panel.Children.Add(accountsBox);

        // The right column fits its longest captions: the two tabs and a button with its brush.
        double Text(string value) => UiTypography.Measure(value, typography.Family, typography.InterfaceSize).Width;
        var right = new StackPanel { Spacing = 8, Width = Math.Ceiling(Math.Max(Text("Для текущей базы") + Text("Для всех баз") + 48,
            Text("Выполнить выборочную очистку") + typography.IconSize + 44)) };
        Grid.SetRow(right, 1); Grid.SetColumn(right, 1); panel.Children.Add(right);
        var scopeIndex = 0;
        var scope = new StackPanel { Orientation = Orientation.Horizontal };
        var tabs = new List<Button>();
        foreach (var (caption, index) in new[] { ("Для текущей базы", 0), ("Для всех баз", 1) })
        {
            var tab = new Button { Content = caption, Padding = new Thickness(8, 2), MinHeight = 0, CornerRadius = new CornerRadius(0),
                BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0) };
            AutomationProperties.SetName(tab, caption);
            tabs.Add(tab); scope.Children.Add(tab);
        }
        right.Children.Add(scope);
        // The original's «Версия»: all families or one; 8.3 and newer live in 1cv8, 8.2 in 1cv82, 8.1 in 1cv81. Every family is offered:
        // another account may have a folder this one has not. As in the original, the choice is for «Для всех баз» only.
        var families = BaseCacheRoots.Families.ToList();
        var version = new ComboBox { ItemsSource = new[] { "Для всех версий платформы 8.x" }.Concat(families.Select(x => "1С:Предприятие " + (x == "8.3" ? "8.3 и новее" : x))).ToList(),
            SelectedIndex = 0, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(version, "Версия платформы");
        right.Children.Add(version);
        var target = new TextBlock { Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        right.Children.Add(target);
        var user = new CheckBox { Content = "Настройки пользователя (Roaming)", IsChecked = cacheUser, MinHeight = 0 };
        var program = new CheckBox { Content = "Программный Cache (Local)", IsChecked = cacheProgram, MinHeight = 0 };
        AutomationProperties.SetName(user, "Настройки пользователя"); AutomationProperties.SetName(program, "Программный кэш");
        right.Children.Add(user); right.Children.Add(program);
        Button Brush(string icon, string caption)
        {
            var button = new Button { Padding = new Thickness(8, 5), HorizontalAlignment = HorizontalAlignment.Stretch, Background = UiColors.Button,
                BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(UiIcons.Create(icon, typography.IconSize));
            content.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content; AutomationProperties.SetName(button, caption); return button;
        }
        var run = Brush("clear", "Выполнить выборочную очистку");
        // As the original: both buttons take the check boxes; «выборочная» keeps the settings files (*.pfl, *.reg) of 1C,
        // «грубая» removes the folders with everything in them.
        ToolTip.SetTip(run, "Удаление файлов с сохранением свойств профилей");
        var hard = Brush("clear-hard", "Выполнить грубую очистку");
        ToolTip.SetTip(hard, "Удаление каталогов со всем содержимым");
        right.Children.Add(run); right.Children.Add(hard);
        var note = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap,
            // Honest about the platforms: Windows refuses to remove a folder held by 1C; Linux checks the open files of the
            // processes it can see, which misses 1C of another user or computer on a shared folder.
            Text = (OperatingSystem.IsWindows() ? "Закройте 1С с этими базами: занятые файлы не удаляются."
                : "Закройте 1С с этими базами: каталоги с файлами, открытыми вашими процессами, не удаляются.") +
                " Выборочная очистка сохраняет файлы настроек 1С (*.pfl), грубая удаляет каталоги целиком." +
                (roots.Shared ? " Программный кэш и в грубой очистке удаляется без файлов настроек." : "") };
        var sizes = new TextBlock { Foreground = UiColors.Text, TextWrapping = TextWrapping.Wrap };
        var hintText = new StackPanel { Spacing = 2 }; hintText.Children.Add(sizes); hintText.Children.Add(note);
        var hint = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 4, 0, 0), Child = hintText };
        // A popup measures without a width limit: the hint wraps to the two columns, not to its longest line.
        hint.MaxWidth = accountsGroup.Width + right.Width + panel.ColumnSpacing;
        Grid.SetRow(hint, 2); Grid.SetColumnSpan(hint, 2); panel.Children.Add(hint);

        IReadOnlyList<BaseEntry> Bases() => scopeIndex == 1 ? library.Bases.ToList()
            : bases.SelectedItem is LibraryRow { Entry: { } entry } ? [entry] : [];
        // A folder with the roots it lies under: every account and family has its own.
        IReadOnlyList<(string Folder, BaseCacheRoots Roots)> programFolders = [], userFolders = [];
        // Sizes of what each button removes: «выборочная» keeps the settings files, «грубая» takes the whole folders.
        long programSize = 0, userSize = 0, programSelective = 0, userSelective = 0;
        var generation = 0; var busy = false;
        void Update()
        {
            var any = user.IsChecked == true && userFolders.Count > 0 || program.IsChecked == true && programFolders.Count > 0;
            run.IsEnabled = hard.IsEnabled = !busy && any;
        }
        // The roots of the checked accounts in the chosen family; the user settings only of this user.
        List<(BaseCacheRoots Roots, bool Own)> Targets()
        {
            var chosen = scopeIndex == 0 || version.SelectedIndex <= 0 ? families : [families[version.SelectedIndex - 1]];
            var result = new List<(BaseCacheRoots, bool)>();
            foreach (var (account, box) in accountBoxes.Where(x => x.Box.IsChecked == true))
                foreach (var family in chosen)
                    if (BaseCacheRoots.For(family, account.Current ? null : account.Profile) is { } r) result.Add((r, account.Current));
            return result;
        }
        async void Measure()
        {
            var current = ++generation; var items = Bases();
            var persistent = items.ToDictionary(x => x.Id, PersistentIdentity);
            target.Text = scopeIndex == 1 ? $"Все базы списка: {items.Count}" : items.Count == 1 ? items[0].Name : "Выберите базу в списке.";
            programFolders = userFolders = []; Update();
            sizes.Text = "Поиск каталогов…";
            var targets = Targets();
            var found = await Task.Run(() =>
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var p = targets.SelectMany(t => items.SelectMany(x => BaseCache.Find(x, persistent[x.Id], BaseCacheKind.Program, t.Roots)).Select(f => (f, t.Roots)))
                    .DistinctBy(x => x.f, comparer).ToList();
                var u = targets.Where(t => t.Own).SelectMany(t => items.SelectMany(x => BaseCache.Find(x, persistent[x.Id], BaseCacheKind.UserSettings, t.Roots)).Select(f => (f, t.Roots)))
                    .DistinctBy(x => x.f, comparer).ToList();
                return (p, u, P: p.Sum(x => BaseCache.Size(x.f, BaseCacheKind.Program, x.Roots)), U: u.Sum(x => BaseCache.Size(x.f, BaseCacheKind.UserSettings, x.Roots)),
                    Ps: p.Sum(x => BaseCache.Size(x.f, BaseCacheKind.Program, x.Roots, true)), Us: u.Sum(x => BaseCache.Size(x.f, BaseCacheKind.UserSettings, x.Roots, true)));
            });
            if (current != generation) return;
            (programFolders, userFolders) = (found.p, found.u); (programSize, userSize, programSelective, userSelective) = (found.P, found.U, found.Ps, found.Us);
            string Describe(int count, long size, long kept) => count == 0 ? "нет" : $"каталогов: {count}, {FormatSize(size)} (выборочно {FormatSize(kept)})";
            var others = accountBoxes.Count(x => x.Box.IsChecked == true && !x.Account.Current);
            sizes.Text = $"Roaming: {Describe(userFolders.Count, found.U, found.Us)} · Local: {Describe(programFolders.Count, found.P, found.Ps)}" +
                (others > 0 ? $" · других учётных записей: {others} (только Local)" : "");
            ToolTip.SetTip(user, string.Join("\n", userFolders.Select(x => x.Folder))); ToolTip.SetTip(program, string.Join("\n", programFolders.Select(x => x.Folder)));
            Update();
        }
        user.IsCheckedChanged += (_, _) => { cacheUser = user.IsChecked == true; Update(); };
        program.IsCheckedChanged += (_, _) => { cacheProgram = program.IsChecked == true; Update(); };
        version.SelectionChanged += (_, _) => Measure();
        foreach (var (_, box) in accountBoxes) box.IsCheckedChanged += (_, _) => Measure();
        void ScopeChanged() => version.IsEnabled = scopeIndex == 1 && !busy;
        run.Click += (_, _) => Clear(user.IsChecked == true, program.IsChecked == true, selective: true);
        hard.Click += (_, _) => Clear(user.IsChecked == true, program.IsChecked == true, selective: false);
        async void Clear(bool removeUser, bool removeProgram, bool selective)
        {
            if (busy || cacheBusy) return;
            var (u, p) = (userFolders, programFolders);
            // The user settings (*.pfl: forms, reports, passwords remembered by 1C) are not recreated: the rough clearing asks first.
            // The selective one keeps them; the program cache is rebuilt by 1C and goes without a question.
            if (removeUser && !selective && u.Count > 0)
            {
                popup.IsLightDismissEnabled = false;
                bool confirmed;
                try
                {
                    confirmed = await ConfirmDanger("Будут удалены настройки пользователя 1С (Roaming) для " +
                        (scopeIndex == 1 ? $"всех баз списка ({Bases().Count})." : $"базы «{target.Text}»."),
                        [$"Каталогов: {u.Count}, {FormatSize(userSize)}" + (removeProgram && p.Count > 0 ? $"; вместе с программным кэшем: {p.Count}, {FormatSize(programSize)}." : "."),
                         "Сохранённые настройки форм, отчётов и списков 1С будут потеряны; восстановить их нельзя.",
                         "Выполнить очистку?"], "Да, удалить");
                }
                finally { popup.IsLightDismissEnabled = true; }
                if (!confirmed) { status.Text = "Очистка отменена."; return; }
            }
            // While removing, the panel stays open and a second clearing cannot start; the result always reaches the status line.
            busy = cacheBusy = true; popup.IsLightDismissEnabled = false;
            scope.IsEnabled = close.IsEnabled = user.IsEnabled = program.IsEnabled = version.IsEnabled = accounts.IsEnabled = false; Update();
            try
            {
                var outcome = await Task.Run(() =>
                {
                    var results = new List<BaseCacheResult>();
                    if (removeProgram)
                        foreach (var group in p.GroupBy(x => x.Roots))
                            results.Add(BaseCache.Clear(group.Select(x => x.Folder), BaseCacheKind.Program, group.Key, selective));
                    // With a shared folder the user settings removal takes the whole folder, the cache included.
                    if (removeUser)
                        foreach (var group in u.GroupBy(x => x.Roots))
                            results.Add(BaseCache.Clear(group.Select(x => x.Folder).Where(Directory.Exists), BaseCacheKind.UserSettings, group.Key, selective));
                    return (Removed: results.Sum(x => x.Removed), Failed: results.SelectMany(x => x.Failed).ToList());
                });
                AppLog.Info("cache.clear");
                note.Foreground = outcome.Failed.Count == 0 ? UiColors.Muted : UiColors.Error;
                var summary = outcome.Failed.Count == 0 ? $"Очищено каталогов: {outcome.Removed}."
                    : $"Очищено каталогов: {outcome.Removed}; не удалось: {outcome.Failed.Count} — вероятно, база открыта в 1С.";
                note.Text = outcome.Failed.Count == 0 ? summary : summary + "\n" + string.Join("\n", outcome.Failed);
                status.Text = summary;
            }
            catch (Exception ex)
            {
                AppLog.Error("cache.clear.failed", ex);
                note.Foreground = UiColors.Error; note.Text = status.Text = "Очистка прервана: " + LegacyImport.ErrorMessage(ex);
            }
            finally
            {
                busy = cacheBusy = false; popup.IsLightDismissEnabled = true;
                scope.IsEnabled = close.IsEnabled = user.IsEnabled = program.IsEnabled = accounts.IsEnabled = true; ScopeChanged();
            }
            Measure();
        }
        // Classic tabs: the active one merges with the panel, the other is shaded.
        void ShowTabs()
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                tabs[i].Background = i == scopeIndex ? UiColors.Surface : UiColors.Button;
                tabs[i].FontWeight = i == scopeIndex ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }
        ShowTabs();
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index]; var value = index;
            tab.Click += (_, _) => { scopeIndex = value; ShowTabs(); ScopeChanged(); Measure(); };
        }
        popup.Opened += (_, _) => Measure();
        panel.SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, typography.Family);
        panel.SetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty, typography.InterfaceSize);
        panel.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, UiColors.Text);
        var host = (Panel)(toolbarPanel ?? cacheButton.Parent!);
        popup.PlacementTarget = host; host.Children.Add(popup);
        popup.Closed += (_, _) => host.Children.Remove(popup);
        popup.IsOpen = true;
    }
}
