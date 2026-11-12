using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

public enum AppearanceTheme { System, Light, Dark }

public sealed record FontPreference
{
    // Empty: system default for the list, inheritance from the list for other roles.
    public string Family { get; init; } = "";
    public double Size { get; init; } = 14;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public void Validate()
    {
        if (Family is null || Family.Length > 256 || Family.Any(char.IsControl) ||
            !double.IsFinite(Size) || Size < 10 || Size > 24)
            throw new InvalidDataException("Некорректные настройки шрифта: размер должен быть от 10 до 24.");
    }
}

[JsonConverter(typeof(AppearanceJsonConverter))]
public sealed record AppearanceSettings
{
    public const double DefaultListSize = 13;
    public const double DefaultInterfaceSize = 11;
    public const double DefaultHeaderSize = 12;
    // Compact dialogs as the original's 8 pt forms (decision of the user 11.11.2026). Only the default: a size saved in the
    // profile, 14 of earlier builds included, is read as it is and never migrated.
    public const double DefaultDialogSize = 12;
    // The original main list: DefaultNodeHeight = 22 (smMainForm.dfm); its other trees use 20–21.
    public const double DefaultRowHeight = 22;
    // The original's «Интенсивность выделения» (SelectionBlendFactor 0–255, 128 by default): 128 is the colour used so far.
    public const double DefaultSelectionIntensity = 128;
    public AppearanceTheme Theme { get; init; }
    public FontPreference List { get; init; } = new() { Size = DefaultListSize };
    public FontPreference Interface { get; init; } = new() { Size = DefaultInterfaceSize };
    public FontPreference Headers { get; init; } = new() { Size = DefaultHeaderSize };
    public FontPreference Dialogs { get; init; } = new() { Size = DefaultDialogSize };
    public double MinimumRowHeight { get; init; } = DefaultRowHeight;
    public double SelectionIntensity { get; init; } = DefaultSelectionIntensity;
    /// <summary>«Отображать всплывающие подсказки» (ShowHint) of the rows of the list; the buttons keep theirs.</summary>
    public bool ListTooltips { get; init; } = true;
    /// <summary>«Отображать группы сверху» (GroupsShowTop): off mixes groups and bases of a level by name.</summary>
    public bool GroupsOnTop { get; init; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    [JsonIgnore] public JsonElement? UnrecognizedSource { get; init; }
    [JsonIgnore] public string? LoadWarning { get; init; }
    public void Validate()
    {
        if (!Enum.IsDefined(Theme) || List is null || Interface is null || Headers is null || Dialogs is null ||
            !double.IsFinite(MinimumRowHeight) || MinimumRowHeight < 20 || MinimumRowHeight > 64 ||
            !double.IsFinite(SelectionIntensity) || SelectionIntensity < 0 || SelectionIntensity > 255 || SelectionIntensity != Math.Floor(SelectionIntensity))
            throw new InvalidDataException("Повреждены настройки оформления.");
        foreach (var font in new[] { List, Interface, Headers, Dialogs }) font.Validate();
    }
    /// <summary>The colour of the selected row, #RRGGBB: the field mixed with the full selection colour by the intensity.
    /// 128 gives #FFF09B (light) and #665726 (dark), the colours used before the setting existed.</summary>
    public static string SelectionColor(bool dark, double intensity)
    {
        (int R, int G, int B) field = dark ? (0x20, 0x26, 0x2D) : (0xFF, 0xFF, 0xFF), full = dark ? (0xAB, 0x88, 0x1F) : (0xFF, 0xE1, 0x38);
        var k = Math.Clamp(intensity, 0, 255) / 255;
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * k, MidpointRounding.AwayFromZero);
        return $"#{Mix(field.R, full.R):X2}{Mix(field.G, full.G):X2}{Mix(field.B, full.B):X2}";
    }
    /// <summary>The text of the selected row: the theme's own colour while it keeps the contrast of ordinary text (4.5:1),
    /// else the other one — a bright selection of the dark theme gets dark text.</summary>
    public static string SelectedTextColor(bool dark, double intensity)
    {
        // Where the theme's colour falls short, white or black, whichever is stronger: one of them always reaches 4.5:1
        // (the tinted colours leave a gap in the middle of the dark theme's slider).
        var background = SelectionColor(dark, intensity);
        var own = dark ? "#FFF6D8" : "#242820";
        if (Contrast(own, background) >= 4.5) return own;
        return Contrast("#FFFFFF", background) >= Contrast("#000000", background) ? "#FFFFFF" : "#000000";
    }

    /// <summary>The contrast ratio of two #RRGGBB colours (WCAG relative luminance).</summary>
    public static double Contrast(string first, string second)
    {
        static double Luminance(string color)
        {
            double Channel(int offset)
            {
                var c = Convert.ToInt32(color.Substring(offset, 2), 16) / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        }
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    public AppearanceSettings Reset() => this with
    {
        Theme = AppearanceTheme.System, MinimumRowHeight = DefaultRowHeight, UnrecognizedSource = null, LoadWarning = null,
        SelectionIntensity = DefaultSelectionIntensity, ListTooltips = true, GroupsOnTop = true,
        List = List with { Family = "", Size = DefaultListSize }, Interface = Interface with { Family = "", Size = DefaultInterfaceSize },
        Headers = Headers with { Family = "", Size = DefaultHeaderSize }, Dialogs = Dialogs with { Family = "", Size = DefaultDialogSize }
    };
}

public static class FontSelection
{
    public static string Resolve(string requested, IEnumerable<string> installed, bool windows, string systemDefault)
    {
        var fonts = installed.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);
        var defaults = windows ? new[] { "Segoe UI", "DejaVu Sans", "Liberation Sans", "Arial" }
            : new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans", "Arial" };
        foreach (var name in new[] { requested }.Concat(defaults))
            if (fonts.TryGetValue(name, out var actual)) return actual;
        return systemDefault;
    }
}
