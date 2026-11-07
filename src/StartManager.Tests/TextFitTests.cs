using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Д2/Д3 of the Linux run: widths come from the text measured in the current font, not from a constant chosen for Segoe UI.
public sealed class TextFitTests
{
    // A stand-in for the font: every letter 13 px wide (about DejaVu Sans 22), so «Информационные» is 182 px.
    private static double Wide(string word) => word.Length * 13;

    [Fact] public void TheWidestWordOfTheCaptionsIsMeasured()
    {
        Assert.Equal(14 * 13, TextFit.WidestWord(["Общее", "Информационные базы", "Параметры  запуска", null, ""], Wide));
        Assert.Equal(0, TextFit.WidestWord([], Wide));
        Assert.Equal(["Информационные", "базы"], TextFit.Words(" Информационные\tбазы "));
        Assert.Empty(TextFit.Words(null));
    }

    [Fact] public void AColumnGrowsOnlyWhenAWordDoesNotFit()
    {
        // Segoe UI 22: the word fits the old column, nothing changes on Windows.
        Assert.Equal(242, TextFit.Fit(242, 160.2, 40));
        // DejaVu Sans 22: the word with its place in the tree is wider; the column takes it, rounded up.
        Assert.Equal(250, TextFit.Fit(242, 14 * 13 + 0.4, 51.6 + 16));
    }

    [Fact] public void AFieldTakesItsWholeValue()
    {
        // «00:00:00» at 22: 8 characters of about 14 px with the paddings of the box, wider than the old 100.
        Assert.Equal(135, TextFit.Fit(100, 8 * 14, 11 + 12));
        Assert.Equal(100, TextFit.Fit(100, 60, 23));
        Assert.Equal(140, TextFit.Fit(140, double.NaN, 30));
        Assert.Equal(140, TextFit.Fit(140, 120, double.PositiveInfinity));
    }
}
