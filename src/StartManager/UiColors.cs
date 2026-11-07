using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

internal static class UiColors
{
    public static readonly SolidColorBrush Surface = new(), Field = new(), Text = new(), Muted = new(),
        Line = new(), Selection = new(), SelectedText = new(), BaseText = new(), Error = new(), Button = new(), SectionText = new(), Slate = new(),
        TabGlyph = new(), TabGlyphEdge = new();
    public static void Initialize(Application app)
    {
        Update(app.ActualThemeVariant == ThemeVariant.Dark);
        app.PropertyChanged += (_, e) =>
        { if (e.Property.Name == "ActualThemeVariant") Update(app.ActualThemeVariant == ThemeVariant.Dark); };
    }
    public static void Apply(AppearanceTheme theme, double selectionIntensity = AppearanceSettings.DefaultSelectionIntensity)
    {
        var app = Application.Current!;
        intensity = selectionIntensity;
        app.RequestedThemeVariant = Variant(theme);
        Update(app.ActualThemeVariant == ThemeVariant.Dark);
    }
    private static double intensity = AppearanceSettings.DefaultSelectionIntensity;
    // «Интенсивность выделения» (AppearanceSettings.SelectionColor).
    public static Color SelectionColor(bool dark, double selectionIntensity) => Color.Parse(AppearanceSettings.SelectionColor(dark, selectionIntensity));
    public static ThemeVariant Variant(AppearanceTheme theme) => theme switch
    { AppearanceTheme.Dark => ThemeVariant.Dark, AppearanceTheme.Light => ThemeVariant.Light, _ => ThemeVariant.Default };
    // Neutral greys traced from the original's icons: exact in the light theme, the muted light grey on the dark surface.
    private static readonly Dictionary<string, SolidColorBrush> Greys = [];
    private static bool dark;
    public static IBrush Grey(string color)
    {
        if (!Greys.TryGetValue(color, out var brush)) Greys[color] = brush = new SolidColorBrush(Color.Parse(dark ? "#B2BECB" : color));
        return brush;
    }
    private static void Update(bool dark)
    {
        UiColors.dark = dark;
        foreach (var (color, brush) in Greys) brush.Color = Color.Parse(dark ? "#B2BECB" : color);
        Surface.Color = Color.Parse(dark ? "#282E36" : "#F0EFEB");
        Field.Color = Color.Parse(dark ? "#20262D" : "#FFFFFF");
        Text.Color = Color.Parse(dark ? "#E7EDF3" : "#252B31");
        Muted.Color = Color.Parse(dark ? "#B2BECB" : "#566370");
        Line.Color = Color.Parse(dark ? "#596675" : "#B7BFC7");
        Selection.Color = SelectionColor(dark, intensity);
        SelectedText.Color = Color.Parse(AppearanceSettings.SelectedTextColor(dark, intensity));
        BaseText.Color = Color.Parse(dark ? "#95D8A2" : "#23712F");
        SectionText.Color = Color.Parse(dark ? "#91C7F2" : "#367BB0");
        Error.Color = Color.Parse(dark ? "#FFB4AB" : "#AA2525");
        Button.Color = Color.Parse(dark ? "#323C48" : "#E5E8EC");
        // The dark slate of the original connection search icon; lighter on the dark field.
        Slate.Color = Color.Parse(dark ? "#A9BCCB" : "#3C5564");
        // The original's bottom tab glyphs: neutral grey #737373 with light #DADADA edge pixels; lighter on the dark surface.
        TabGlyph.Color = Color.Parse(dark ? "#B2BECB" : "#737373");
        TabGlyphEdge.Color = Color.Parse(dark ? "#6D7680" : "#DADADA");
    }
}
