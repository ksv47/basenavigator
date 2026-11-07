namespace StartManager.Core;

/// <summary>
/// Widths of columns and fields fitted to text measured in the current font. The UI passes its measurer (UiTypography.Measure);
/// here only the arithmetic, so a font wider than the one a constant was chosen for (DejaVu Sans of Linux against Segoe UI) still
/// fits: a wrapped caption breaks only between words, a date or a time field shows its whole value.
/// </summary>
public static class TextFit
{
    public static IEnumerable<string> Words(string? text) => (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The width a wrapped caption needs so that no word is broken: the widest of its words.</summary>
    public static double WidestWord(IEnumerable<string?> texts, Func<string, double> measure) =>
        texts.SelectMany(Words).Select(measure).DefaultIfEmpty(0).Max();

    /// <summary>At least the minimum and at least the text with the space around it (padding, borders, a button), in whole pixels.</summary>
    public static double Fit(double minimum, double text, double chrome) =>
        double.IsFinite(text) && double.IsFinite(chrome) ? Math.Max(minimum, Math.Ceiling(text + chrome)) : minimum;
}
