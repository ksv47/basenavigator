using System.Globalization;

namespace StartManager.Core;

/// <summary>Sizes as the original shows them: three significant digits, «402 МБ», «48,8 КБ», «2,02 ГБ».</summary>
public static class SizeFormat
{
    public static string Short(long bytes, IFormatProvider? culture = null)
    {
        string[] units = ["КБ", "МБ", "ГБ", "ТБ"];
        var value = Math.Max(0, bytes) / 1024d; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var pattern = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(pattern, culture ?? CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
