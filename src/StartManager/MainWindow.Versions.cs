using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // The arrow of «Свойства»: the original's pbActionIB; more of its actions come here as they are ported.
    private void ShowPropertiesMenu(Control anchor)
    {
        var menu = ClassicMenu("Свойства");
        var state = CurrentState();
        MenuItem Command(MainCommand command, System.Action action, string? caption = null) => CommandItem(command, state, action, "edit", caption);
        // The loads from a file are not in the table (no key): the availability of the backup commands.
        MenuItem Load(string label, BackupFormat format)
        {
            var item = new MenuItem { Header = MenuHeader(label, ""), Icon = UiIcons.Create("edit", typography.IconSize), Classes = { "scaled" },
                IsEnabled = MainCommands.CanExecute(MainCommand.Backup, state) };
            AutomationProperties.SetName(item, label);
            item.Click += (_, _) => LoadSelected(format); return item;
        }
        menu.ItemsSource = new Control[]
        {
            // Ours before the original's items: the properties of the base or, as the toolbar button and Alt+Enter, of the group.
            Command(MainCommand.Properties, EditSelected, state.Selection == SelectionKind.Group ? "Свойства группы…" : null), new Separator(),
            Command(MainCommand.AssignVersion, AssignVersion),
            new Separator(),
            // As the original: the lock items for a file base; setting opens the «Блокировка» page of the properties.
            Command(MainCommand.SetLock, SetSelectedLock),
            Command(MainCommand.RemoveLock, RemoveSelectedLock),
            new Separator(),
            // The original's backup items (pbActionIB): a file or a server base; a web base has no configurator to connect.
            Command(MainCommand.Backup, BackupSelected),
            Command(MainCommand.RestoreBackup, ShowBackupList),
            new Separator(),
            Load("Загрузить информационную базу из файла (*.dt)…", BackupFormat.Dt),
            Load("Загрузить конфигурацию из файла (*.cf)…", BackupFormat.Cf),
        };
        menu.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft;
        menu.Closed += (_, _) => RestoreListFocus();
        menu.Open(anchor);
    }

    // The original's «Установить блокировку информационной базы» (actSetLockFile, Ctrl+Alt+L): the properties on the «Блокировка» page.
    private async void SetSelectedLock()
    {
        if (loadFailed || reading || closed || bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) return;
        await Edit(entry, page: BaseDialog.LockPage);
    }

    // The original's «Разблокировать информационную базу» (actRemoveLockFile, Ctrl+Alt+U): the lock file of a file base is
    // deleted after a question. The list is not changed; a base of a common list is changed only there, as in its properties.
    private async void RemoveSelectedLock()
    {
        if (loadFailed || reading || closed || bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) return;
        if (entry.Kind != ConnectionKind.File) { status.Text = MainCommands.FileLockText; return; }
        if (CommonLocked(entry)) return;
        var folder = entry.Location; var path = InfobaseLock.PathIn(folder);
        string? raw;
        try { raw = await Task.Run(() => File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { status.Text = "Файл блокировки не прочитан: " + LegacyImport.ErrorMessage(ex); return; }
        if (closed) return;
        if (raw is null) { status.Text = "Файл блокировки подключения не найден: " + path; return; }
        string state;
        try { state = InfobaseLock.Parse(raw).Describe(DateTime.Now); }
        catch (InvalidDataException ex) { state = ex.Message; }
        if (!await ConfirmDanger("Будет удален файл блокировки подключения! Продолжать?", [entry.Name, path, state], "Удалить")) return;
        try
        {
            // The file is removed only as it was shown: a lock changed meanwhile by another program stays.
            await Task.Run(() => InfobaseLock.Remove(folder, raw));
            AppLog.Info("lock.removed");
            status.Text = "Файл блокировки подключения удалён: " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLog.Warning("lock.remove.failed", ex); status.Text = "Не удалось удалить файл блокировки подключения: " + LegacyImport.ErrorMessage(ex); }
    }

    // The original's «Назначить версию» (actSetVersion, Shift+Ctrl+F8): a base, or every base of a group and its subgroups.
    private async void AssignVersion()
    {
        if (loadFailed || reading || closed || bases.SelectedItem is not LibraryRow row) return;
        if (row is not ({ Entry: not null } or { Section: LibrarySection.None, Orphan: null })) return;
        if (ListLocked() || CommonLocked(row.Entry) || row.Entry is null && CommonGroupLocked(row.Group)) return;
        var entry = row.Entry;
        if (entry is null && PlatformVersions.InGroup(library, row.Group).Count == 0) { status.Text = $"В группе «{row.Group}» нет информационных баз."; return; }
        var source = library.ListPath;
        var choice = await PlatformVersionWindow.Show(this, typography, entry?.PlatformRule?.Version ?? entry?.PlatformVersion ?? "",
            entry?.PlatformRule?.Architecture ?? PlatformArchitecture.Any, library.Bases.Select(x => x.Executable).Append(library.Executable));
        if (choice is null || closed) { RestoreListFocus(); return; }
        // The list may have been reread or replaced while the window was open.
        if (loadFailed || reading || source != library.ListPath || ListLocked()) { RestoreListFocus(); return; }
        // The group as it is now: a reread may have moved bases into or out of it, or removed it.
        IReadOnlyList<Guid> present = entry is not null ? library.Bases.Where(x => x.Id == entry.Id).Select(x => x.Id).ToList()
            : PlatformVersions.InGroup(library, row.Group);
        if (present.Count == 0)
        {
            status.Text = entry is not null ? GoneMessage : $"Группа «{row.Group}» изменена другой программой: в ней нет информационных баз. Версия не назначена.";
            RestoreListFocus(); return;
        }
        try
        {
            var (next, changed) = PlatformVersions.Assign(library, present, choice);
            if (changed == 0) status.Text = $"Версия {choice.Version} уже назначена. " + SourceDescription();
            // No ID is fixed here: a base whose record has none keeps the list untouched when only its own rule changes. The
            // bases must still be there: removed by another program meanwhile, the save stops with a conflict.
            else if (await Persist(next, required: present))
            {
                Refresh();
                status.Text = $"Версия {choice.Version} ({PlatformCatalog.ArchitectureText(choice.Architecture)}) назначена. Обновлено информационных баз: {changed}.";
            }
        }
        catch (ArgumentException ex) { status.Text = ex.Message; }
        RestoreListFocus();
    }

    // A platform uninstaller started from «8.x» and not finished yet; every versions window follows it.
    private bool platformRemoval;
    private event System.Action? PlatformRemovalChanged;
    private void SetPlatformRemoval(bool value) { platformRemoval = value; PlatformRemovalChanged?.Invoke(); }

    // «8.x» of the original's toolbar (FormV8Apps): the installed 1C:Enterprise 8 versions as families, builds in their folders
    // and the clients of a build; the installation date, source and removal of a build come from the Windows registry.
    private async void OpenVersions()
    {
        if (closed) return;
        var windows = OperatingSystem.IsWindows();
        var dialog = new Window { Title = "Список установленных версий платформы 1С:Предприятие 8", Width = 720, Height = 464, MinWidth = 500, MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        IReadOnlyList<InstalledVersionNode> nodes = [];
        var rows = new List<(InstalledVersionNode Node, int Depth, InstalledVersionNode? Parent)>();
        // Rows are kept open by their keys: a new search makes new nodes.
        var expanded = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        static string Key(InstalledVersionNode node) => node.Level == InstalledVersionLevel.Family ? "f:" + node.Version : "b:" + node.Path;
        // Set when the window closes: a scan or a check finishing later does not fill it.
        var shut = false;
        // The source check of the row chosen now: a later click or another row makes an earlier answer stale.
        var sourceCheck = 0;
        const string SourceChecking = "Проверка источника установки…";

        var indent = Math.Round(typography.DialogSize * 1.3);
        double TextWidth(string text) => Math.Ceiling(UiTypography.Measure(text, typography.DialogFamily, typography.DialogSize).Width);
        // The header is semibold: a tenth wider than its regular measure.
        var dateWidth = windows ? Math.Ceiling(Math.Max(TextWidth("Дата установки") * 1.1, TextWidth("00.00.0000"))) + 16 : 0;
        var columns = new ColumnDefinitions($"200,*,{dateWidth}");
        Grid Cells(Control first, string path, string date, bool header)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns.ToString()), Margin = new Thickness(0, 1) };
            row.Children.Add(first);
            foreach (var (text, column) in new[] { (path, 1), (date, 2) })
            {
                var cell = new TextBlock { Text = text, Margin = new Thickness(5, 2), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, Foreground = header ? UiColors.Muted : UiColors.Text };
                if (text.Length > 0) ToolTip.SetTip(cell, text);
                Grid.SetColumn(cell, column); row.Children.Add(cell);
            }
            return row;
        }
        var headerText = new TextBlock { Text = "Версия", Margin = new Thickness(5, 2), FontWeight = FontWeight.SemiBold, Foreground = UiColors.Muted,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = Cells(headerText, "Каталог / Файл", windows ? "Дата установки" : "", true); header.Margin = new Thickness(1, 0);
        var list = new ListBox { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)) } });
        AutomationProperties.SetName(list, "Установленные версии");

        // «Источник установки» under the tree on the left, «Установлено версий всего» on the right (FormV8Apps): the distribution folder of
        // the chosen build, opened by a click.
        var sourceLink = new Button { Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0,
            Foreground = UiColors.SectionText, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { TextTrimming = TextTrimming.PrefixCharacterEllipsis, TextWrapping = TextWrapping.NoWrap, TextDecorations = TextDecorations.Underline } };
        AutomationProperties.SetName(sourceLink, "Источник установки");
        var source = new DockPanel { IsVisible = windows };
        var sourceCaption = new TextBlock { Text = "Источник установки: ", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        DockPanel.SetDock(sourceCaption, Dock.Left); source.Children.Add(sourceCaption); source.Children.Add(sourceLink);
        var count = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap, Text = "Поиск установленных версий…" };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        void Say(string text, bool error = false)
        {
            message.Text = text; message.IsVisible = text.Length > 0; message.Foreground = error ? UiColors.Error : UiColors.Muted;
        }

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 6 };
        Put(body, header, 1);
        Put(body, list, 2);
        var bottom = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        count.TextWrapping = TextWrapping.NoWrap; count.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(source); Grid.SetColumn(count, 1); bottom.Children.Add(count);
        Put(body, bottom, 3);
        Put(body, message, 4);

        var expandAll = Action("Развернуть все", () => All(true));
        var collapseAll = Action("Свернуть все", () => All(false));
        var open = Action("Открыть каталог", async () => await OpenFolder());
        ToolTip.SetTip(open, "Открыть выбранный каталог");
        // Uninstall refreshes this button: it is subscribed once the button exists.
        var remove = Action("Деинсталлировать", () => { });
        remove.Click += async (_, _) => await Uninstall();
        ToolTip.SetTip(expandAll, "Развернуть все элементы"); ToolTip.SetTip(collapseAll, "Свернуть все элементы");
        ToolTip.SetTip(open, "Открыть каталог расположения");
        ToolTip.SetShowOnDisabled(remove, true);

        InstalledVersionNode? Current() => list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count ? rows[list.SelectedIndex].Node : null;
        // A build opens its folder; a client file the folder of the file.
        string? Folder(InstalledVersionNode? node) => node?.Level switch
        {
            InstalledVersionLevel.Build => node.Path, InstalledVersionLevel.File => Path.GetDirectoryName(node.Path), _ => null
        };
        InstalledVersionNode? BuildOf(InstalledVersionNode? node) => node?.Level == InstalledVersionLevel.Build ? node
            : node?.Level == InstalledVersionLevel.File ? rows.FirstOrDefault(x => x.Node == node).Parent : null;

        void ShowRows(InstalledVersionNode? select = null)
        {
            var keep = select ?? Current();
            rows.Clear();
            void Add(InstalledVersionNode node, int depth, InstalledVersionNode? parent)
            {
                rows.Add((node, depth, parent));
                if (expanded.Contains(Key(node))) foreach (var child in node.Children) Add(child, depth + 1, node);
            }
            foreach (var node in nodes) Add(node, 0, null);
            // The version column holds the deepest row: its indent, the arrow and the longest text.
            var first = Math.Max(TextWidth("Версия") + 14, rows.Count == 0 ? 0 : rows.Max(x => x.Depth * indent + typography.IconSize + TextWidth(x.Node.Text) + 18));
            columns[0].Width = new GridLength(Math.Min(first, 420));
            foreach (var grid in new[] { header }) grid.ColumnDefinitions[0].Width = columns[0].Width;
            list.ItemsSource = rows.Select(x =>
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5 + x.Depth * indent, 2, 5, 2) };
                var arrow = x.Node.Children.Count == 0 ? new Border { Width = typography.IconSize } as Control
                    : UiIcons.Create(expanded.Contains(Key(x.Node)) ? "chevron-down" : "chevron-right", typography.IconSize);
                arrow.Margin = new Thickness(0, 0, 4, 0); arrow.VerticalAlignment = VerticalAlignment.Center;
                if (x.Node.Children.Count > 0)
                    arrow.PointerPressed += (_, e) => { e.Handled = true; Toggle(x.Node); };
                cell.Children.Add(arrow);
                cell.Children.Add(new TextBlock { Text = x.Node.Text, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = x.Node.Level == InstalledVersionLevel.Family ? FontWeight.Bold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
                var date = x.Node.Uninstall?.InstallDate is { } day ? day.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
                var row = Cells(cell, x.Node.Level == InstalledVersionLevel.Family ? "" : x.Node.Path, date, false);
                row.ColumnDefinitions[0].Width = columns[0].Width;
                AutomationProperties.SetName(row, x.Node.Text);
                return row;
            }).ToList();
            var index = keep is null ? -1 : rows.FindIndex(x => Key(x.Node) == Key(keep) && x.Node.Level == keep.Level && x.Node.Path == keep.Path);
            list.SelectedIndex = index >= 0 ? index : rows.Count > 0 ? 0 : -1;
        }
        void Toggle(InstalledVersionNode node, bool? open = null)
        {
            if (node.Children.Count == 0) return;
            var key = Key(node);
            if (open ?? !expanded.Contains(key)) expanded.Add(key); else expanded.Remove(key);
            ShowRows(node);
        }
        void All(bool open)
        {
            var keep = Current();
            expanded.Clear();
            if (open) foreach (var node in nodes.Concat(nodes.SelectMany(x => x.Children))) expanded.Add(Key(node));
            // A closed tree keeps the family of the chosen row.
            ShowRows(open ? keep : nodes.FirstOrDefault(x => keep is not null && (x == keep || x.Children.Contains(keep) || x.Children.Any(y => y.Children.Contains(keep)))));
        }

        async Task OpenFolder()
        {
            if (Folder(Current()) is not { } folder) return;
            try { if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) Say($"Не удалось открыть каталог {folder}.", true); }
            catch (Exception ex) { AppLog.Warning("folder.open.failed", ex); Say($"Не удалось открыть каталог {folder}: {ex.Message}", true); }
        }
        async Task CopyPath()
        {
            if (Current() is not { Level: not InstalledVersionLevel.Family } node) return;
            try
            {
                if (dialog.Clipboard is not { } clipboard) { Say("Буфер обмена недоступен.", true); return; }
                await clipboard.SetTextAsync(node.Path);
                Say("Скопировано в буфер обмена: " + node.Path);
            }
            catch (Exception ex) { Say("Не удалось скопировать в буфер обмена: " + ex.Message, true); }
        }

        async Task Scan(InstalledVersionNode? select = null)
        {
            try
            {
                var executables = library.Bases.Select(x => x.Executable).Append(library.Executable).ToList();
                var (scan, tree) = await Task.Run(() =>
                {
                    var found = PlatformCatalog.Scan(executables: executables);
                    var entries = OperatingSystem.IsWindows() ? InstalledVersions.ReadRegistry() : [];
                    return (found, InstalledVersions.Build(found.Installations, entries, OperatingSystem.IsWindows(), PlatformCatalog.NativeArchitecture));
                });
                if (shut) return;
                var first = nodes.Count == 0;
                nodes = tree;
                // As the original opens it: the families show their builds, the files of a build are opened on demand.
                if (first) foreach (var family in nodes) expanded.Add(Key(family));
                var builds = nodes.Sum(x => x.Children.Count);
                count.Text = builds == 0 ? "Установленные версии не найдены." : $"Установлено версий всего: {builds}.";
                if (scan.Warnings.Count > 0) Say(string.Join(" ", scan.Warnings));
                ShowRows(select);
            }
            catch (Exception ex) { count.Text = ""; Say("Версии не найдены: " + LegacyImport.ErrorMessage(ex), true); }
        }

        async Task Uninstall()
        {
            if (!OperatingSystem.IsWindows() || platformRemoval || BuildOf(Current()) is not { Uninstall: { } entry } build) return;
            string file, arguments;
            try { (file, arguments) = InstalledVersions.UninstallCommand(entry.UninstallString); }
            catch (ArgumentException ex) { Say(ex.Message, true); return; }
            // One removal at a time for the program, not for this window: a window opened again sees it until the uninstaller ends.
            SetPlatformRemoval(true);
            var untracked = false;
            try
            {
                if (!await ConfirmDanger($"Будет удалена из системы версия {build.Text} платформы «1С:Предприятие 8».",
                    [$"Каталог: {build.Path}.", $"Программа в системе: {(entry.DisplayName.Length > 0 ? entry.DisplayName : file)}.",
                     "Информационные базы, которым назначена эта версия, не запустятся, пока не будет назначена другая.",
                     "Удаление выполняет установщик Windows: он может запросить права администратора и своё подтверждение. Удалить версию?"],
                    "Да, удалить", dialog)) { list.Focus(); return; }
                Say($"Удаление версии {build.Text}…");
                try
                {
                    // The registry command is run as Windows runs it from «Programs and Features»; the shell asks for elevation.
                    using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
                    if (process is null)
                    {
                        // Windows started it without a process to follow: its end is unknown, so the next removal waits for a restart.
                        untracked = true;
                        Say($"Удаление версии {build.Text} запущено, но его завершение отследить нельзя. Откройте список снова после его окончания; " +
                            "следующее удаление станет доступно после перезапуска программы.");
                    }
                    else
                    {
                        await process.WaitForExitAsync();
                        // 1605: the product is not installed any more; 1602: cancelled in the installer; 3010: a restart is needed.
                        Say(process.ExitCode switch
                        {
                            0 or 1605 => $"Версия {build.Text} удалена.",
                            3010 or 1641 => $"Версия {build.Text} удалена. Для завершения удаления нужна перезагрузка компьютера.",
                            1602 => "Удаление отменено.",
                            var code => $"Удаление версии {build.Text} не выполнено: код завершения {code}."
                        }, process.ExitCode is not (0 or 1605 or 3010 or 1641 or 1602));
                        var said = message.Text;
                        if (!closed && !shut) { await Scan(build); Say(said ?? "", message.Foreground == UiColors.Error); }
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Say("Удаление отменено: права администратора не предоставлены."); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    AppLog.Warning("platform.uninstall.failed", ex); Say($"Не удалось запустить удаление {file}: {ex.Message}", true);
                }
            }
            finally { if (!untracked) SetPlatformRemoval(false); }
        }

        void UpdateActions()
        {
            var node = Current();
            var build = BuildOf(node);
            open.IsEnabled = Folder(node) is not null;
            remove.IsEnabled = windows && !platformRemoval && build?.Uninstall is not null;
            ToolTip.SetTip(remove, !windows ? "В Linux версии платформы удаляются менеджером пакетов."
                : platformRemoval ? "Идёт удаление версии платформы: дождитесь его завершения."
                : build is null ? "Удалить программу из системы: выберите версию."
                : build.Uninstall is null ? "Для этой версии в системе нет записи об установке: удалите её средствами Windows."
                : "Удалить программу из системы");
            var installSource = build?.Uninstall?.InstallSource ?? "";
            // Not disabled when empty: Fluent paints a disabled button as a grey bar; an empty source is plain text.
            var link = (TextBlock)sourceLink.Content!;
            link.Text = installSource.Length > 0 ? installSource : "—";
            link.TextDecorations = installSource.Length > 0 ? TextDecorations.Underline : null;
            sourceLink.Foreground = installSource.Length > 0 ? UiColors.SectionText : UiColors.Muted;
            sourceLink.Cursor = installSource.Length > 0 ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null;
            sourceLink.Focusable = installSource.Length > 0;
            ToolTip.SetTip(sourceLink, installSource.Length > 0 ? installSource : null);
        }
        list.SelectionChanged += (_, _) =>
        {
            sourceCheck++;
            if (message.Text == SourceChecking) Say("");
            UpdateActions();
        };
        sourceLink.Click += async (_, _) =>
        {
            var folder = BuildOf(Current())?.Uninstall?.InstallSource ?? "";
            if (folder.Length == 0) return;
            var ticket = ++sourceCheck;
            Say(SourceChecking);
            try
            {
                // A network source may not answer for long: the check is off the UI thread.
                var exists = await Task.Run(() => Directory.Exists(folder));
                if (shut || ticket != sourceCheck) return;
                Say("");
                var opened = exists && await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
                if (!opened && !shut && ticket == sourceCheck) Say($"Источник установки недоступен: {folder}.", true);
            }
            catch (Exception ex) { if (!shut && ticket == sourceCheck) Say($"Источник установки недоступен: {folder}: {ex.Message}", true); }
        };
        // As a tree: Right opens a row or goes to its first child, Left closes it or goes to its parent; Enter and a double click open or close.
        // Tunnelled: the list itself takes the arrows first.
        list.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, async (_, e) =>
        {
            if (Current() is not { } node) return;
            var index = list.SelectedIndex;
            var opened = expanded.Contains(Key(node)) && node.Children.Count > 0;
            switch (e.Key)
            {
                case Avalonia.Input.Key.Right when node.Children.Count > 0:
                    if (opened) list.SelectedIndex = index + 1; else Toggle(node, true);
                    e.Handled = true; break;
                case Avalonia.Input.Key.Left:
                    if (opened) Toggle(node, false);
                    else if (rows[index].Parent is { } parent) list.SelectedIndex = rows.FindIndex(x => x.Node == parent);
                    e.Handled = true; break;
                case Avalonia.Input.Key.Enter when node.Children.Count > 0:
                    Toggle(node); e.Handled = true; break;
                case Avalonia.Input.Key.C when e.KeyModifiers == Avalonia.Input.KeyModifiers.Control:
                    e.Handled = true; await CopyPath(); break;
            }
            if (!e.Handled || list.SelectedIndex < 0) return;
            list.ScrollIntoView(list.SelectedIndex);
            (list.ContainerFromIndex(list.SelectedIndex) as Control)?.Focus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.DoubleTapped += (_, e) => { if (Current() is { Children.Count: > 0 } node) { e.Handled = true; Toggle(node); } };
        // The original's context menu: the same actions and «Скопировать путь».
        list.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var node = Current();
            var menu = ClassicMenu("Установленные версии");
            MenuItem Item(string label, string icon, bool enabled, Func<Task> action, string gesture = "")
            {
                var item = new MenuItem { Header = MenuHeader(label, gesture), Icon = UiIcons.Create(icon, typography.IconSize), Classes = { "scaled" }, IsEnabled = enabled };
                AutomationProperties.SetName(item, label);
                item.Click += async (_, _) => await action(); return item;
            }
            menu.ItemsSource = new Control[]
            {
                Item("Развернуть все", "expand-all", nodes.Count > 0, () => { All(true); return Task.CompletedTask; }),
                Item("Свернуть все", "collapse-all", nodes.Count > 0, () => { All(false); return Task.CompletedTask; }), new Separator(),
                Item("Открыть каталог расположения", "folder", Folder(node) is not null, OpenFolder),
                Item("Скопировать путь", "copy", node is { Level: not InstalledVersionLevel.Family }, CopyPath, "Ctrl+C"), new Separator(),
                Item("Деинсталлировать", "delete", remove.IsEnabled, Uninstall)
            };
            menu.Closed += (_, _) => list.Focus();
            // From the keyboard the menu belongs to the chosen row.
            menu.Open(list.ContainerFromIndex(list.SelectedIndex) as Control ?? list);
        };

        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { expandAll, collapseAll, open, remove }) { button.Margin = new Thickness(0, 0, 6, 0); tools.Children.Add(button); }
        var close = Action("Закрыть", dialog.Close); close.IsCancel = true; close.MinWidth = 90;
        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Right); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        buttons.Children.Add(close); buttons.Children.Add(tools);
        dialog.Content = DialogLayout.Frame(body, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        // The minimum grows with the font as the size does: the tree keeps a few rows under large dialog fonts.
        var scale = dialog.Width / 720;
        dialog.MinWidth = Math.Round(dialog.MinWidth * scale); dialog.MinHeight = Math.Round(dialog.MinHeight * scale);
        // ApplyDialog wraps every text; the cells and the source are one line.
        foreach (var text in header.Children.OfType<TextBlock>().Append(headerText).Append(sourceCaption).Append((TextBlock)sourceLink.Content!))
            text.TextWrapping = TextWrapping.NoWrap;
        UpdateActions();
        PlatformRemovalChanged += UpdateActions;
        dialog.Closed += (_, _) => { shut = true; PlatformRemovalChanged -= UpdateActions; };
        dialog.Opened += async (_, _) => { list.Focus(); await Scan(); if (!shut) list.Focus(); };
        await dialog.ShowDialog(this);
    }
}
