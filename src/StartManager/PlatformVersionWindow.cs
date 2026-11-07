using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

/// <summary>The original's «Выбор версии платформы» (FormSetVersion): the installed versions as a tree of families, releases
/// and builds, «Все / x32 / x64», sorting of the releases and builds, the client of the chosen build below the tree.</summary>
internal static class PlatformVersionWindow
{
    public static async Task<PlatformVersionChoice?> Show(Window owner, UiTypography typography, string version, PlatformArchitecture architecture,
        IEnumerable<string> executables)
    {
        var windows = OperatingSystem.IsWindows();
        var dialog = new Window { Title = "Выбор версии платформы", Width = 320, Height = 440, MinWidth = 270, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var filter = PlatformBitFilter.All;
        var descending = true;
        IReadOnlyList<PlatformInstallation>? installations = null;
        PlatformVersionNode? focused = null;

        // As the original: the bitness choice only where both exist, on 64-bit Windows.
        var bits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            IsVisible = windows && RuntimeInformation.OSArchitecture != Architecture.X86 };
        var radios = new[] { ("Все", PlatformBitFilter.All), ("x32", PlatformBitFilter.X86), ("x64", PlatformBitFilter.X64) }.Select(x =>
        {
            var radio = new RadioButton { Content = x.Item1, GroupName = "platform-bits", IsChecked = x.Item2 == PlatformBitFilter.All, Tag = x.Item2,
                MinHeight = 0, Padding = new Thickness(Math.Round(Math.Max(4, typography.DialogSize / 3)), 0, 0, 0), Classes = { "compact" } };
            AutomationProperties.SetName(radio, x.Item1 == "Все" ? "Все разрядности" : x.Item1);
            bits.Children.Add(radio); return radio;
        }).ToList();
        // Compact circles as the session group of the main window: Fluent draws 20 px circles in a 32 px row.
        var circle = Math.Round(Math.Clamp(typography.DialogSize + 2, 12, 20));
        foreach (var (part, size) in new[] { ("OuterEllipse", circle), ("CheckOuterEllipse", circle), ("CheckGlyph", Math.Round(circle / 2.3)) })
            bits.Styles.Add(new Style(s => s.OfType<RadioButton>().Class("compact").Template().Name(part))
            { Setters = { new Setter(Layoutable.WidthProperty, size), new Setter(Layoutable.HeightProperty, size) } });
        bits.Styles.Add(new Style(s => s.OfType<RadioButton>().Class("compact").Template().OfType<Grid>())
        { Setters = { new Setter(Layoutable.HeightProperty, Math.Ceiling(UiTypography.Measure("Йр", typography.DialogFamily, typography.DialogSize).Height) + 6) } });
        ToggleButton Sort(string icon, string tip, bool value)
        {
            var button = new ToggleButton { Content = UiIcons.Create(icon, typography.IconSize), IsChecked = descending == value, Padding = new Thickness(5, 3),
                MinHeight = 0, BorderThickness = new Thickness(1), BorderBrush = UiColors.Line };
            AutomationProperties.SetName(button, tip); ToolTip.SetTip(button, tip);
            return button;
        }
        var ascending = Sort("sort-asc", "По возрастанию", false);
        var descendingButton = Sort("sort-desc", "По убыванию", true);
        var sorting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        sorting.Children.Add(ascending); sorting.Children.Add(descendingButton);
        var top = new DockPanel();
        DockPanel.SetDock(sorting, Dock.Right); top.Children.Add(sorting); top.Children.Add(bits);

        var tree = new TreeView { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(tree, "Версия платформы");
        var info = new TextBlock { Foreground = UiColors.Muted, TextTrimming = TextTrimming.PrefixCharacterEllipsis, Text = "Поиск установленных платформ…" };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 6 };
        body.Children.Add(top);
        Grid.SetRow(tree, 1); body.Children.Add(tree);
        Grid.SetRow(info, 2); body.Children.Add(info);
        var headers = new List<TextBlock>();

        TreeViewItem Item(PlatformVersionNode node)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var text = new TextBlock { Text = node.Version, FontWeight = node.Level == 0 ? FontWeight.Bold : FontWeight.Normal,
                FontSize = typography.DialogSize + (node.Level == 0 ? 1 : 0) };
            header.Children.Add(text); headers.Add(text);
            if (node.Suffix.Length > 0) { var suffix = new TextBlock { Text = node.Suffix, Foreground = UiColors.Muted }; header.Children.Add(suffix); headers.Add(suffix); }
            // The whole tree is open: the builds are what is chosen most (the original opens the nodes on the way down).
            var item = new TreeViewItem { Header = header, Tag = node, IsExpanded = true, MinHeight = 0, Padding = new Thickness(0, 1),
                ItemsSource = node.Children.Select(Item).ToList() };
            AutomationProperties.SetName(item, node.Version + node.Suffix);
            return item;
        }
        IEnumerable<TreeViewItem> Items(IEnumerable<TreeViewItem> items) => items.SelectMany(x => Items(x.ItemsSource?.OfType<TreeViewItem>() ?? []).Prepend(x));
        void Rebuild()
        {
            if (installations is null) return;
            var nodes = PlatformVersions.Build(installations, filter, descending, windows, PlatformCatalog.NativeArchitecture);
            // The first time the version of the base; later the row chosen here, of the other bitness if this one is hidden.
            var path = focused is { } was ? PlatformVersions.Find(nodes, was.Version, was.Architecture) : PlatformVersions.Find(nodes, version, architecture);
            headers.Clear();
            var items = nodes.Select(Item).ToList();
            tree.ItemsSource = items;
            tree.SelectedItem = Items(items).FirstOrDefault(x => ReferenceEquals(x.Tag, path.LastOrDefault()));
            foreach (var text in headers) text.TextWrapping = TextWrapping.NoWrap;
        }
        tree.SelectionChanged += (_, _) =>
        {
            focused = (tree.SelectedItem as TreeViewItem)?.Tag as PlatformVersionNode ?? focused;
            info.Foreground = UiColors.Muted;
            // The client of a build is a path: shortened in the middle on one line; the rule of a family or release wraps.
            var build = focused?.Level == 2;
            info.TextWrapping = build ? TextWrapping.NoWrap : TextWrapping.Wrap;
            info.TextTrimming = build ? TextTrimming.PrefixCharacterEllipsis : TextTrimming.None;
            info.Text = focused is null ? "" : build ? focused.Executable : $"Запускается новейшая установленная {focused.Version}.x.";
            ToolTip.SetTip(info, info.Text);
        };
        foreach (var radio in radios)
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) { filter = (PlatformBitFilter)radio.Tag!; Rebuild(); } };
        void SetOrder(bool value)
        {
            descending = value; ascending.IsChecked = !value; descendingButton.IsChecked = value; Rebuild();
        }
        ascending.Click += (_, _) => SetOrder(false);
        descendingButton.Click += (_, _) => SetOrder(true);

        void Apply()
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is not PlatformVersionNode node)
            {
                info.Foreground = UiColors.Error; info.TextWrapping = TextWrapping.Wrap; info.Text = "Не выбрана версия."; return;
            }
            dialog.Close(PlatformVersions.Choose(node, filter, windows));
        }
        // As the original: a double click on a version applies it; on a family or release it opens or closes the row.
        tree.DoubleTapped += (_, e) =>
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is PlatformVersionNode { Level: 2 }) { e.Handled = true; Apply(); }
        };
        var buttons = new DockPanel();
        var apply = SettingsWindow.Command("Применить", Apply); apply.IsDefault = true; apply.FontWeight = FontWeight.Bold;
        var cancel = SettingsWindow.Command("Отмена", () => dialog.Close(null)); cancel.IsCancel = true;
        DockPanel.SetDock(cancel, Dock.Right); buttons.Children.Add(cancel); buttons.Children.Add(apply);
        dialog.Content = DialogLayout.Frame(body, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        info.TextWrapping = TextWrapping.NoWrap;
        dialog.Opened += async (_, _) =>
        {
            // The window is not narrowed past the bitness choice and the sort buttons side by side (large dialog fonts).
            top.Measure(Size.Infinity);
            dialog.MinWidth = Math.Max(dialog.MinWidth, Math.Ceiling(top.DesiredSize.Width + dialog.Bounds.Width - top.Bounds.Width + 12));
            tree.Focus();
            try
            {
                var scan = await Task.Run(() => PlatformCatalog.Scan(executables: executables));
                installations = scan.Installations;
                Rebuild();
                if (scan.Installations.Count == 0) info.Text = "Установленные версии не найдены.";
                (tree.SelectedItem as TreeViewItem)?.BringIntoView();
            }
            catch (Exception ex) { installations = []; Rebuild(); info.Foreground = UiColors.Error; info.Text = "Поиск не выполнен: " + LegacyImport.ErrorMessage(ex); }
        };
        return await dialog.ShowDialog<PlatformVersionChoice?>(owner);
    }
}
