using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using StartManager.Core;

namespace StartManager;

internal static class UiColors
{
    public static readonly SolidColorBrush Surface = new(), Field = new(), Text = new(), Muted = new(),
        Line = new(), DisabledMark = new(), DisabledText = new(), Selection = new(), SelectedText = new(), BaseText = new(), Error = new(), Button = new(), SectionText = new(), Slate = new(),
        TabGlyph = new(), TabGlyphEdge = new();
    public static void Initialize(Application app)
    {
        NeutralToggles(app);
        Update(app.ActualThemeVariant == ThemeVariant.Dark);
        app.PropertyChanged += (_, e) =>
        { if (e.Property.Name == "ActualThemeVariant") Update(app.ActualThemeVariant == ThemeVariant.Dark); };
    }
    /// <summary>Check boxes and radio buttons of every window as the original's standard Windows ones (decision of the user 11.11.2026):
    /// a field-coloured box or circle with a grey edge and a mark of the text colour, no accent fill. The brushes of this palette
    /// follow the theme, so the dark theme gets its own colours. Fluent paints the checked box's edge with its fill brush, so the edge
    /// of a checked box is set by a style; the styles of the application win over the control theme's own.</summary>
    private static void NeutralToggles(Application app)
    {
        var resources = app.Resources;
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            foreach (var check in new[] { "Checked", "Unchecked", "Indeterminate" })
            {
                resources["CheckBoxCheckBackgroundFill" + check + state] = Field;
                resources["CheckBoxCheckGlyphForeground" + check + state] = Text;
                resources["CheckBoxCheckBackgroundStroke" + check + state] = state.Length == 0 ? Muted : Text;
            }
            resources["RadioButtonOuterEllipseFill" + state] = Field;
            resources["RadioButtonOuterEllipseCheckedFill" + state] = Field;
            resources["RadioButtonOuterEllipseStroke" + state] = state.Length == 0 ? Muted : Text;
            resources["RadioButtonOuterEllipseCheckedStroke" + state] = state.Length == 0 ? Muted : Text;
            resources["RadioButtonCheckGlyphFill" + state] = Text;
            resources["RadioButtonCheckGlyphStroke" + state] = Text;
        }
        // Disabled: a pale box and mark, as a disabled standard check box.
        foreach (var check in new[] { "Checked", "Unchecked", "Indeterminate" })
        {
            resources["CheckBoxCheckBackgroundFill" + check + "Disabled"] = Surface;
            resources["CheckBoxCheckBackgroundStroke" + check + "Disabled"] = DisabledMark;
            resources["CheckBoxCheckGlyphForeground" + check + "Disabled"] = DisabledMark;
        }
        resources["RadioButtonOuterEllipseFillDisabled"] = resources["RadioButtonOuterEllipseCheckedFillDisabled"] = Surface;
        resources["RadioButtonOuterEllipseStrokeDisabled"] = resources["RadioButtonOuterEllipseCheckedStrokeDisabled"] = DisabledMark;
        resources["RadioButtonCheckGlyphFillDisabled"] = resources["RadioButtonCheckGlyphStrokeDisabled"] = DisabledMark;
        foreach (var (state, brush) in new[] { (":checked", Muted), (":checked:pointerover", Text), (":checked:disabled", DisabledMark) })
            app.Styles.Add(new Style(s => Checked(s.OfType<CheckBox>(), state).Template().OfType<Border>().Name("NormalRectangle"))
            { Setters = { new Setter(Border.BorderBrushProperty, brush) } });
    }
    private static Avalonia.Styling.Selector Checked(Avalonia.Styling.Selector selector, string states)
    {
        foreach (var state in states.Split(':', StringSplitOptions.RemoveEmptyEntries)) selector = selector.Class(":" + state);
        return selector;
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
        // The edge and mark of a disabled check box or radio button: between Line and Muted, so a checked one is still seen
        // checked but quieter than an enabled one (Grok audit 11.11: Line on Surface was 1.6:1 in the light theme).
        DisabledMark.Color = Color.Parse(dark ? "#8592A0" : "#8A949E");
        // The captions of disabled fields and the titles of disabled buttons: at least 3:1 on the surface and on a button face
        // (light 3.6 and 3.4, dark 4.3 and 3.5), still quieter than the text and the notes (Grok audit 12.11, P3).
        DisabledText.Color = Color.Parse(dark ? "#8592A0" : "#737E89");
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
