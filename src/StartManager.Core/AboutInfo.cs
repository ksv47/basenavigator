using System.Globalization;
using System.Runtime.InteropServices;

namespace StartManager.Core;

public enum StorageKind { Portable, Normal, Explicit }

/// <summary>The values of «О программе» (the original's smFormAbout) as text; the window only reads the facts.</summary>
public static class AboutInfo
{
    public const string NoArguments = "<не передавались>";
    // The link of «О программе» names the repository the updater reads releases from: one value, never a second literal.
    public const string RepositoryCaption = "github.com/" + GitHubReleaseSource.DefaultRepository;
    public const string RepositoryUrl = "https://" + RepositoryCaption;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>«Ключи запуска»: the arguments as they were passed; one with a space or a quote is quoted.</summary>
    public static string LaunchKeys(IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ? NoArguments : string.Join(" ", arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";

    /// <summary>«Скомпилировано для»: the system and architecture of the build and its delivery variant.</summary>
    public static string CompiledFor(string system, Architecture architecture, bool light) =>
        $"{system} {Architecture(architecture)} ({(light ? "лёгкий вариант, нужен .NET 10" : "автономный вариант")})";

    public static string Architecture(Architecture architecture) => architecture.ToString().ToLowerInvariant();

    /// <summary>«Дата сборки», as the original: dd.mm.yyyy.</summary>
    public static string BuildDate(DateTime time) => time.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>«Размер файла», as the original: «47,3 МБ (49 612 345 байт)». A file other than the program itself (the code of
    /// the light build beside its starter) is named: «752 КБ (770 048 байт), BaseNavigator.dll».</summary>
    public static string FileSize(long bytes, string? otherFile = null) =>
        $"{SizeFormat.Short(bytes, Russian)} ({bytes.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = [3] })} байт)" +
        (string.IsNullOrEmpty(otherFile) ? "" : ", " + otherFile);

    /// <summary>«Продолжительность работы»: «04:05:06», from a day on «3 дн. 04:05:06».</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var clock = $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
        return span.Days > 0 ? $"{span.Days} дн. {clock}" : clock;
    }

    /// <summary>Where the profile is: the portable folder next to the program, the user's folder, or a file given by --settings.</summary>
    public static StorageKind Storage(string settingsPath, string portableDirectory, string normalDirectory, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? "");
        bool Same(string other) => string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)), comparison);
        var named = string.Equals(Path.GetFileName(settingsPath), "library.json", comparison);
        return named && Same(portableDirectory) ? StorageKind.Portable : named && Same(normalDirectory) ? StorageKind.Normal : StorageKind.Explicit;
    }

    public static string StorageText(StorageKind kind) => kind switch
    {
        StorageKind.Portable => "Портативный (папка Settings рядом с программой)",
        StorageKind.Normal => "Обычный (папка пользователя)",
        _ => "Отдельный файл настроек (ключ --settings)"
    };

    /// <summary>The name of Windows from the registry (CurrentVersion: ProductName, DisplayVersion) and the version of the
    /// system. Windows 11 keeps «Windows 10» in ProductName: build 22000 and later is named 11.</summary>
    public static string WindowsName(string? productName, string? displayVersion, Version version)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (version.Build >= 22000 && name.StartsWith("Windows 10", StringComparison.Ordinal)) name = "Windows 11" + name["Windows 10".Length..];
        var release = string.IsNullOrWhiteSpace(displayVersion) ? "" : " " + displayVersion.Trim();
        return $"{name}{release} ({version.Major}.{version.Minor}.{version.Build})";
    }

    /// <summary>The name of a Linux distribution: PRETTY_NAME of /etc/os-release, else the kernel description.</summary>
    public static string LinuxName(string? osRelease, string description)
    {
        foreach (var line in (osRelease ?? "").Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
            var value = text["PRETTY_NAME=".Length..].Trim().Trim('"', '\'');
            if (value.Length > 0) return value;
        }
        return description.Trim();
    }

    /// <summary>The bold line of the system: «Windows 10 Pro 22H2 (10.0.19045), x64 · .NET 10.0.0».</summary>
    public static string SystemLine(string system, Architecture architecture, string framework) =>
        $"{system}, {Architecture(architecture)} · {framework.Trim()}";

    public static string Privileges(bool windows, bool privileged) => windows
        ? privileged ? "Запущено от имени администратора" : "Запущено без прав администратора"
        : privileged ? "Запущено от имени root" : "Запущено без прав root";
}
