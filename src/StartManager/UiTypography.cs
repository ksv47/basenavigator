using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.LogicalTree;
using StartManager.Core;

namespace StartManager;

internal sealed record UiTypography
{
    public static UiTypography Default => From(new());
    private static readonly Lazy<string[]> Fonts = new(() => FontManager.Current.SystemFonts.Select(x => x.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
    public static IReadOnlyList<string> InstalledFonts => Fonts.Value;
    public required FontFamily Family { get; init; }
    public required FontFamily ListFamily { get; init; }
    public required FontFamily HeaderFamily { get; init; }
    public required FontFamily DialogFamily { get; init; }
    public double InterfaceSize { get; init; }
    // 16 px up to 13 pt; larger interface fonts enlarge the toolbar and menu icons.
    public double IconSize => Math.Round(16 * Math.Max(1, InterfaceSize / 13));
    public double ListSize { get; init; }
    public double ColumnHeaderSize { get; init; }
    public double DialogSize { get; init; }
    // The compact dialogs (settings look 11.11.2026): a field, a list and a spin box about 1.75 × the dialog font and never lower than
    // a line of it with a small padding, as the original's 21–22 px fields at 8 pt; a check box row a little lower.
    public double ControlHeight => Math.Max(Math.Round(DialogSize * 1.75), DialogLine + 8);
    public double CheckRowHeight => Math.Max(Math.Round(DialogSize * 1.6), DialogLine + 4);
    // The box of a check box and the circle of a radio button: 13–14 px as the standard ones at the default font, growing with it.
    public double CheckSize => Math.Clamp(Math.Round(DialogSize * 1.15), 13, 20);
    // The icons inside dialogs (path fields, list and tool bar buttons) follow the dialog font, not the menu and panel font.
    public double DialogIconSize => Math.Round(16 * Math.Max(1, DialogSize / 13));
    private double DialogLine => Math.Ceiling(Measure("Йру", DialogFamily, DialogSize).Height);
    public double RowHeight { get; init; }
    public double HeaderHeight { get; init; }
    // Widths of the list columns; the name takes the rest and needs at least NameWidth.
    public double NameWidth { get; init; }
    // When the columns do not fit anyway, the name keeps this width and the others go to the horizontal scroll.
    public double ReadableNameWidth { get; init; }
    public double PlatformWidth { get; init; }
    public double DateWidth { get; init; }
    public double SizeWidth { get; init; }
    public static FormattedText Measure(string text, FontFamily family, double size) => new(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family), size, Brushes.Black);
    public static FontFamily Resolve(string requested)
    {
        var installed = InstalledFonts;
        var fallback = FontManager.Current.DefaultFontFamily;
        var selected = FontSelection.Resolve(requested, installed, OperatingSystem.IsWindows(), fallback.Name);
        // Keep the embedded fallback's source URI, not just its display name.
        return installed.Contains(selected, StringComparer.OrdinalIgnoreCase) ? new(selected) : fallback;
    }
    public static UiTypography From(AppearanceSettings settings)
    {
        settings.Validate();
        var list = Resolve(settings.List.Family);
        FontFamily Role(FontPreference font) => font.Family.Length == 0 ? list : Resolve(font.Family);
        var header = Role(settings.Headers);
        // The title leaves room for the sort arrow.
        double Width(string title, string sample) => Math.Ceiling(Math.Max(
            Measure(title, header, settings.Headers.Size).Width + 12, Measure(sample, list, settings.List.Size).Width)) + 18;
        var platform = Width("Платформа", "8.3.27.2214  (ARM64)"); var date = Width("Дата", "20.09.26"); var size = Width("Размер", "999,99 ГБ");
        return new()
        {
            Family = Role(settings.Interface), ListFamily = list, HeaderFamily = header, DialogFamily = Role(settings.Dialogs),
            PlatformWidth = platform, DateWidth = date, SizeWidth = size,
            HeaderHeight = Measure("Платформа", header, settings.Headers.Size).Height + 8,
            NameWidth = Measure("Наименование", header, settings.Headers.Size).Width + 82,
            // About 20 letters and the indent of a base in a group with its icon.
            ReadableNameWidth = Math.Ceiling(Measure("Бухгалтерия предприя", list, settings.List.Size).Width + 80),
            InterfaceSize = settings.Interface.Size, ListSize = settings.List.Size,
            ColumnHeaderSize = settings.Headers.Size, DialogSize = settings.Dialogs.Size,
            // 2 px above and below the text, as the original's 22 px rows with its list font; a larger font grows the row.
            RowHeight = Math.Max(settings.MinimumRowHeight, Math.Ceiling(Measure("Йру Ágj", list, settings.List.Size).Height) + 4)
        };
    }
    public void ApplyDialog(Window dialog)
    {
        dialog.FontFamily = DialogFamily; dialog.FontSize = DialogSize;
        dialog.Background = UiColors.Surface; dialog.Foreground = UiColors.Text;
        dialog.CanResize = true;
        dialog.Width *= DialogScale(DialogSize);
        if (!dialog.SizeToContent.HasFlag(SizeToContent.Height)) dialog.Height *= DialogScale(DialogSize);
        if (dialog.Content is StackPanel panel)
        {
            dialog.Content = null;
            dialog.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }
        foreach (var text in dialog.GetLogicalDescendants().OfType<TextBlock>()) text.TextWrapping = TextWrapping.Wrap;
        foreach (var row in dialog.GetLogicalDescendants().OfType<WrapPanel>())
            foreach (var child in row.Children) child.Margin = new Avalonia.Thickness(0, 0, 8, 6);
        DialogLayout.Compact(dialog, this);
        FitOnScreenChanges(dialog);
    }
    // A larger dialog font enlarges the window by part of the font's growth: fields stack under their captions
    // above 18 pt, so the full ratio (1.7 at 24 pt) only widens empty space and can exceed the screen.
    public static double DialogScale(double size) => 1 + Math.Max(0, size / 14 - 1) * .6;
    public static void FitOnScreenChanges(Window window)
    {
        (PixelRect Area, double Scale)? previous = null;
        var pending = false;
        void Fit()
        {
            if (!window.IsVisible || window.WindowState != WindowState.Normal) return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var current = (screen.WorkingArea, screen.Scaling);
            if (previous == current) return;
            previous = current;
            FitToScreen(window);
        }
        void Schedule()
        {
            if (pending) return;
            pending = true;
            // Let the native DPI change apply its suggested size before clamping it.
            Dispatcher.UIThread.Post(() => { pending = false; Fit(); }, DispatcherPriority.Background);
        }
        window.Opened += (_, _) => Fit();
        window.ScalingChanged += (_, _) => Schedule();
        window.PositionChanged += (_, _) => Schedule();
        // A dialog sized to its content grows when an error appears; keep its buttons on the screen.
        window.SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || !window.SizeToContent.HasFlag(SizeToContent.Height)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible && window.WindowState == WindowState.Normal) FitToScreen(window);
            }, DispatcherPriority.Background);
        };
    }
    public static void FitToScreen(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea; var scale = screen.Scaling;
        var maxWidth = area.Width / scale * .94; var maxHeight = area.Height / scale * .9;
        window.MinWidth = Math.Min(window.MinWidth, maxWidth); window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        window.Width = Math.Min(window.Width, maxWidth);
        // A dialog sized to its content has no Height: it is capped by MaxHeight and scrolls its body.
        if (window.SizeToContent.HasFlag(SizeToContent.Height)) window.MaxHeight = maxHeight;
        else window.Height = Math.Min(window.Height, maxHeight);
        var height = double.IsNaN(window.Height) ? Math.Min(window.Bounds.Height, maxHeight) : window.Height;
        // Opening may center the original, larger size first (notably X11 without a WM).
        // Clamp the position after resizing, leaving space for native window decorations.
        var right = area.X + Math.Max(0, area.Width - (int)Math.Ceiling((window.Width + 16) * scale));
        var bottom = area.Y + Math.Max(0, area.Height - (int)Math.Ceiling((height + 40) * scale));
        window.Position = new Avalonia.PixelPoint(Math.Clamp(window.Position.X, area.X, right), Math.Clamp(window.Position.Y, area.Y, bottom));
    }
}
