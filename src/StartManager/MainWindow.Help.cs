using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private Window? shortHelp;
    private double shortHelpScale = 100;
    // The dialog font the open help was built with: a changed one rebuilds the window (Codex audit 15.10).
    private (string Family, double Size) shortHelpFont;

    /// <summary>After RebuildInterface: an open help whose dialog font changed is built again at its place with its scale.</summary>
    private void RefreshShortHelp()
    {
        if (shortHelp is not { } open || shortHelpFont == (typography.DialogFamily.Name, typography.DialogSize)) return;
        var position = open.Position;
        shortHelp = null; open.Close();
        ShowShortHelp(position);
    }

    private void FocusSearch(SearchMode? mode)
    {
        if (mode is { } chosen) SetSearchMode(chosen);
        search.Focus(); search.SelectAll();
    }

    /// <summary>The program's icon for the headings of «О программе» and the update window; a missing file leaves a glyph.</summary>
    private Control ProgramImage(double size)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "basenavigator.png");
            if (File.Exists(path)) return new Image { Source = new Bitmap(path), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top };
        }
        catch (Exception ex) { AppLog.Warning("about.icon.failed", ex); }
        return UiIcons.Create("database", size);
    }

    // The original's «Краткая справка» (FormShortHelp, Alt+F1): one modeless window above the main one, resizable, the text
    // on the field colour with a scale of 80–140 % and «OK». F1 and Alt+F1 both open it (there is no full help).
    private void ShowShortHelp() => ShowShortHelp(null);

    // The sections of the dialogs' explanations in the open help, by title: F1 in a modal dialog scrolls to its own.
    private readonly Dictionary<string, Control> shortHelpNotes = [];
    private ScrollViewer? shortHelpScroll;

    /// <summary>F1 and Alt+F1 in the modal «Настройки программы» (or another dialog with its own section, e.g. the base properties):
    /// the help above that window, scrolled to its section (the explanations moved from the pages are there; Codex audit 11.11).</summary>
    private void ShowShortHelpForSettings(Window settings, string section = "Настройки программы")
    {
        if (closed) return;
        // A help already open above the main window is given to the settings window, or it could stay behind the modal one.
        if (shortHelp is { } open && open.Owner != settings)
        {
            var position = open.Position;
            shortHelp = null; open.Close();
            ShowShortHelp(position, settings);
        }
        else if (shortHelp is null) ShowShortHelp(null, settings);
        else shortHelp.Activate();
        Dispatcher.UIThread.Post(() =>
        {
            if (shortHelpScroll is { } scroll && shortHelpNotes.GetValueOrDefault(section)?.TranslatePoint(default, (Visual)scroll.Content!) is { } at)
                scroll.Offset = new Vector(0, at.Y);
        }, DispatcherPriority.Loaded);
    }

    private void ShowShortHelp(PixelPoint? position, Window? owner = null)
    {
        if (closed) return;
        if (shortHelp is not null) { shortHelp.Activate(); return; }
        shortHelpNotes.Clear();
        var window = new Window { Title = "Краткая справка", Width = 455, Height = 401, MinWidth = 250, MinHeight = 200,
            WindowStartupLocation = position is null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.Manual, ShowInTaskbar = false };
        if (position is { } place) window.Position = place;
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(10, 8) };
        foreach (var section in ShortHelp.Sections)
        {
            text.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.Bold, Foreground = UiColors.SectionText,
                Margin = new Thickness(0, text.Children.Count == 0 ? 0 : 10, 0, 2) });
            foreach (var line in section.Lines)
            {
                // A bulleted line: a wrapped line keeps under the text, not under the bullet.
                var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(12, 0, 0, 0) };
                row.Children.Add(new TextBlock { Text = "•" });
                var words = new SelectableTextBlock();
                words.Inlines!.Add(new Run(line.Keys) { FontWeight = FontWeight.SemiBold });
                words.Inlines.Add(new Run(" = " + line.Action));
                Grid.SetColumn(words, 1); row.Children.Add(words);
                text.Children.Add(row);
            }
        }
        // The explanations of «Настройки программы» and of the base properties after the keys, one wrapped paragraph each.
        foreach (var note in ShortHelp.DialogNotes)
        {
            var heading = new TextBlock { Text = note.Title, FontWeight = FontWeight.Bold, Foreground = UiColors.SectionText, Margin = new Thickness(0, 10, 0, 2) };
            text.Children.Add(heading); shortHelpNotes.TryAdd(note.Title, heading);
            foreach (var line in note.Lines)
            {
                var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(12, 0, 0, 2) };
                row.Children.Add(new TextBlock { Text = "•" });
                var words = new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(words, 1); row.Children.Add(words);
                text.Children.Add(row);
            }
        }
        var field = new Border { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1),
            Child = shortHelpScroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        AutomationProperties.SetName(field, "Текст краткой справки");
        // As the original: the scale on the left, «OK» on the right.
        var buttons = new DockPanel();
        var scale = new Slider { Minimum = 80, Maximum = 140, Value = shortHelpScale, SmallChange = 5, LargeChange = 10, TickFrequency = 5,
            IsSnapToTickEnabled = true, Width = 140, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(scale, "Масштаб текста");
        var percent = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48 };
        // The slider gives way in a narrow window; the caption, the percent and «OK» keep their width.
        var caption = new TextBlock { Text = "Масштаб:", VerticalAlignment = VerticalAlignment.Center };
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        scaleRow.Children.Add(caption); scaleRow.Children.Add(scale); scaleRow.Children.Add(percent);
        var ok = SettingsWindow.Command("OK", window.Close); ok.IsDefault = ok.IsCancel = true;
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(ok); buttons.Children.Add(scaleRow);
        // A narrow window first shortens the slider, then drops the caption (the slider keeps its accessible name).
        buttons.SizeChanged += (_, e) =>
        {
            double Width(string sample) => Math.Ceiling(UiTypography.Measure(sample, typography.DialogFamily, typography.DialogSize).Width);
            percent.MinWidth = Width("140 %");
            var free = e.NewSize.Width - ok.Bounds.Width - percent.MinWidth - 24;
            caption.IsVisible = free - Width(caption.Text!) - 6 >= 60;
            scale.Width = Math.Clamp(free - (caption.IsVisible ? Width(caption.Text!) + 6 : 0), 40, 160);
        };
        window.Content = DialogLayout.Frame(field, buttons, scrollBody: false);
        typography.ApplyDialog(window);
        ok.Margin = new Thickness(0);
        window.MinWidth *= UiTypography.DialogScale(typography.DialogSize); window.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        void Scale()
        {
            shortHelpScale = Math.Round(scale.Value / 5) * 5;
            percent.Text = $"{shortHelpScale:0} %";
            text.SetValue(TextBlock.FontSizeProperty, typography.DialogSize * shortHelpScale / 100);
        }
        scale.ValueChanged += (_, _) => Scale();
        Scale();
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => ok.Focus(), DispatcherPriority.Input);
        // A window replaced by RefreshShortHelp is no longer the open one: its closing leaves the new one and the focus alone.
        // Above the modal settings it belongs to that window (the main one is disabled meanwhile) and leaves the focus there.
        window.Closed += (_, _) => { if (shortHelp != window) return; shortHelp = null; if (!closed && owner is null) RestoreListFocus(); };
        shortHelp = window; shortHelpFont = (typography.DialogFamily.Name, typography.DialogSize);
        window.Show(owner ?? this);
    }

    // The original's «О программе» (smFormAbout): the heading with the icon, the name and the version; «Сведения о программе»;
    // the system line and the rights; «Продолжительность работы»; one «OK». Our addition: the way the settings are stored,
    // the profile and the version of .NET. The original's site link (LabelLink1) is the repository. Not carried over: contacts,
    // the licence pages and System ID.
    private async void ShowAbout()
    {
        if (closed) return;
        var version = Program.CurrentVersion;
        var dialog = new Window { Title = "О программе", Width = 540, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var heading = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 14 };
        heading.Children.Add(ProgramImage(Math.Round(39 * Math.Max(1, typography.DialogSize / 13))));
        var name = new TextBlock { Text = "БазоНавигатор", FontWeight = FontWeight.Bold, FontSize = typography.DialogSize * 1.7,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); heading.Children.Add(name);
        var number = new TextBlock { Text = version.ToString(), Foreground = UiColors.Muted, FontSize = typography.DialogSize * 1.15,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(number, 2); heading.Children.Add(number);
        // The original's white heading between two blue lines.
        var header = new Border { Background = UiColors.Field, BorderBrush = UiColors.SectionText, BorderThickness = new Thickness(0, 2),
            Padding = new Thickness(12, 10), Child = heading };

        var self = Environment.ProcessPath ?? "";
        // A single-file build is the program itself; the light build keeps its code in BaseNavigator.dll beside a small starter.
        var codeFile = typeof(MainWindow).Assembly.Location is { Length: > 0 } location ? location : self;
        string Safe(Func<string> read) { try { return read(); } catch (Exception ex) { AppLog.Warning("about.read.failed", ex); return "—"; } }
        var system = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        var compiled = AboutInfo.CompiledFor(system, RuntimeInformation.ProcessArchitecture, Program.LightBuild);
        var named = string.Equals(codeFile, self, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? null : Path.GetFileName(codeFile);
        var built = Safe(() => AboutInfo.BuildDate(File.GetLastWriteTime(codeFile)));
        var size = Safe(() => AboutInfo.FileSize(new FileInfo(codeFile).Length, named));
        // As the original's lbAppFolder: the folder of the program.
        var folder = Path.GetDirectoryName(self) is { Length: > 0 } directory ? directory : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var storage = new SettingsStorage();
        var kind = AboutInfo.Storage(store.PathName, storage.PortableDirectory, storage.NormalDirectory, OperatingSystem.IsWindows());
        var keys = AboutInfo.LaunchKeys(Environment.GetCommandLineArgs().Skip(1).ToArray());

        var info = new StackPanel { Spacing = 8 };
        static TextBlock Value(string text) => new SelectableTextBlock { Text = text };
        void Add(string caption, Control value)
        {
            var label = DialogLayout.Label(caption); label.Foreground = UiColors.Muted;
            info.Children.Add(DialogLayout.Field(label, value));
        }
        // A long path keeps one line: the middle folders give way to «…», the whole path is in the tooltip (as the original).
        var paths = new List<TextBlock>();
        TextBlock PathText(string path)
        {
            var value = new TextBlock { Text = path };
            ToolTip.SetTip(value, path);
            paths.Add(value); return value;
        }
        Add("Скомпилировано для:", Value(compiled));
        Add("Дата сборки:", Value(built));
        Add("Размер файла:", Value(size));
        Add("Размещение:", PathText(folder));
        var storagePanel = new StackPanel { Spacing = 2 };
        storagePanel.Children.Add(Value(AboutInfo.StorageText(kind)));
        storagePanel.Children.Add(PathText(store.PathName));
        Add("Режим хранения настроек:", storagePanel);
        Add("Ключи запуска:", Value(keys));

        var systemName = Safe(() => OperatingSystem.IsWindows() ? WindowsName() :
            AboutInfo.LinuxName(File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : null, RuntimeInformation.OSDescription));
        var systemLine = new SelectableTextBlock { Text = AboutInfo.SystemLine(systemName, RuntimeInformation.OSArchitecture, RuntimeInformation.FrameworkDescription),
            FontWeight = FontWeight.Bold, Foreground = UiColors.Muted };
        var rights = new TextBlock { Text = AboutInfo.Privileges(OperatingSystem.IsWindows(), Environment.IsPrivilegedProcess), Foreground = UiColors.Muted };

        // «Продолжительность работы»: the system and the program, updated every second while the window is open.
        DateTime started;
        try { using var process = System.Diagnostics.Process.GetCurrentProcess(); started = process.StartTime; }
        catch (Exception ex) { AppLog.Warning("about.start_time.failed", ex); started = DateTime.Now; }
        var systemUptime = new TextBlock(); var programUptime = new TextBlock();
        void Tick()
        {
            systemUptime.Text = AboutInfo.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));
            programUptime.Text = AboutInfo.Duration(DateTime.Now - started);
        }
        Tick();
        var uptime = new StackPanel { Spacing = 6 };
        foreach (var (caption, value) in new[] { (system + ":", systemUptime), ("БазоНавигатор:", programUptime) })
        {
            var label = DialogLayout.Label(caption); label.Foreground = UiColors.Muted;
            uptime.Children.Add(DialogLayout.Field(label, value));
        }

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(DialogLayout.Section("Сведения о программе", info));
        var systemPanel = new StackPanel { Spacing = 4 }; systemPanel.Children.Add(systemLine); systemPanel.Children.Add(rights);
        body.Children.Add(systemPanel);
        body.Children.Add(DialogLayout.Section("Продолжительность работы", uptime));

        // As the original: the link on the left, the only button «OK» on the right; Enter and Escape close (Enter on the focused
        // link opens it: the focused button takes the key before the default one).
        var buttons = new DockPanel();
        var ok = SettingsWindow.Command("OK", dialog.Close); ok.IsDefault = ok.IsCancel = true;
        ok.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(ok, Dock.Right);
        var linkText = new TextBlock { Text = AboutInfo.RepositoryCaption, TextDecorations = TextDecorations.Underline };
        var link = new Button { Content = linkText, Padding = new Thickness(3, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            MinHeight = 0, Foreground = UiColors.SectionText, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(link, AboutInfo.RepositoryCaption);
        ToolTip.SetTip(link, AboutInfo.RepositoryUrl);
        link.Click += async (_, _) => await OpenRepository(dialog);
        buttons.Children.Add(ok); buttons.Children.Add(link);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(DialogLayout.Frame(body, buttons));
        dialog.Content = root;
        typography.ApplyDialog(dialog);
        ok.Margin = new Thickness(0);
        dialog.CanResize = false;
        foreach (var path in paths) { path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.PathSegmentEllipsis; }
        number.TextWrapping = TextWrapping.NoWrap;
        // Frame gives every button of the row the width of «OK»: the link keeps the width of its text. Its padding keeps the focus
        // frame off the letters; the negative margin keeps the text in line with the body.
        link.MinWidth = 0; link.Margin = new Thickness(-3, 0, 12, 0);
        linkText.TextWrapping = TextWrapping.NoWrap; linkText.TextTrimming = TextTrimming.CharacterEllipsis;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        // The menu that opened the window is still closing when it opens and takes the focus with it: OK gets it after that.
        dialog.Opened += (_, _) => { timer.Start(); Dispatcher.UIThread.Post(() => ok.Focus(), DispatcherPriority.Input); };
        dialog.Closed += (_, _) => timer.Stop();
        await dialog.ShowDialog(this);
        RestoreListFocus();
    }

    /// <summary>The repository in the default browser; a refusal of the system or a failure is told in a message, never thrown.</summary>
    private async Task OpenRepository(Window owner)
    {
        try { if (await owner.Launcher.LaunchUriAsync(new Uri(AboutInfo.RepositoryUrl))) return; AppLog.Warning("about.link.refused"); }
        catch (Exception ex) { AppLog.Warning("about.link.failed", ex); }
        if (owner.IsVisible) await BackupDialogs.Inform(owner, typography, "О программе", $"Не удалось открыть адрес {AboutInfo.RepositoryUrl}.");
    }

    private static string WindowsName()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.OSDescription;
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Microsoft.Win32.Registry.GetValue(key, "ProductName", null) as string;
        var display = Microsoft.Win32.Registry.GetValue(key, "DisplayVersion", null) as string;
        return AboutInfo.WindowsName(product, display, Environment.OSVersion.Version);
    }
}
