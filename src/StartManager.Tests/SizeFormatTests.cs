using System.Globalization;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class SizeFormatTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    [Theory]
    [InlineData(421_527_552L, "402 МБ")]
    [InlineData(2_168_958_484L, "2,02 ГБ")]
    [InlineData(3_554_673L, "3,39 МБ")]
    [InlineData(137_363_456L, "131 МБ")]
    [InlineData(50_000L, "48,8 КБ")]
    [InlineData(300_000L, "293 КБ")]
    [InlineData(500L, "0,49 КБ")]
    [InlineData(0L, "0 КБ")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5 ТБ")]
    public void ThreeSignificantDigitsAsTheOriginal(long bytes, string expected) => Assert.Equal(expected, SizeFormat.Short(bytes, Ru));
}
