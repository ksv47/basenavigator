using System.Diagnostics;

namespace StartManager.Core;

/// <summary>A starter of 1C: <see cref="Executable"/> is common/1cestart of an installation, or, where the installation has none,
/// its 1cv8s or 1cv8 (as the original falls back). <see cref="Newest"/> is the newest version it serves.</summary>
public sealed record PlatformStarter(string Executable, PlatformArchitecture Architecture, Version Newest)
{
    public bool Common => Path.GetFileNameWithoutExtension(Executable).Equals("1cestart", StringComparison.OrdinalIgnoreCase);
    public string Family => $"{Newest.Major}.{Newest.Minor}";

    public override string ToString() => $"1С:Предприятие {Family}" +
        (Architecture == PlatformArchitecture.X64 ? " (x64)" : Architecture == PlatformArchitecture.Arm64 ? " (ARM64)" : "") +
        $" · {Newest}" + (Common ? "" : " · " + Path.GetFileName(Executable));
}

/// <summary>
/// The original's «1С:Стартер» (ButRunStarter, V8RunStarter): the standard window of 1C with its own list of bases. It is started
/// without any argument and nothing is recorded. The starter lies in the folder common next to the version folders
/// (C:\Program Files\1cv8\common\1cestart.exe, /opt/1cv8/common/1cestart); an installation without one gives its 1cv8s, then 1cv8.
/// </summary>
public static class Starters
{
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;

    /// <summary>The starter of an installation by the path of its client, or null when there is none.</summary>
    public static string? For(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return null;
        var own = Path.GetDirectoryName(executable)!;
        // Only the layout of an installation of 1C owns the folder common: <root>\<version>\bin\1cv8.exe on Windows,
        // <root>/<x86_64|i386|aarch64>/<version>/1cv8 on Linux. A client elsewhere never takes a common folder of another tree.
        if (InstallationRoot(own) is { } root && Path.Combine(root, "common", Name("1cestart")) is var common && File.Exists(common)) return common;
        foreach (var client in new[] { "1cv8s", "1cv8" })
            if (Path.Combine(own, Name(client)) is var path && File.Exists(path)) return path;
        return null;
    }

    private static bool IsVersion(string? name) => name is not null && Version.TryParse(name, out var version) && version.Major == 8;

    private static string? InstallationRoot(string clientFolder)
    {
        var name = Path.GetFileName(clientFolder);
        var parent = Path.GetDirectoryName(clientFolder);
        if (parent is null) return null;
        if (OperatingSystem.IsWindows())
            return name.Equals("bin", StringComparison.OrdinalIgnoreCase) && IsVersion(Path.GetFileName(parent)) ? Path.GetDirectoryName(parent) : null;
        return IsVersion(name) && Path.GetFileName(parent) is "x86_64" or "i386" or "aarch64" ? Path.GetDirectoryName(parent) : null;
    }

    /// <summary>The starters of the installations, one per file, the newest version first.</summary>
    public static IReadOnlyList<PlatformStarter> Find(IEnumerable<PlatformInstallation> installations)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return [.. installations
            .Select(x => (Installation: x, Starter: For(x.Executable)))
            .Where(x => x.Starter is not null)
            .GroupBy(x => Path.GetFullPath(x.Starter!), comparer)
            .Select(group =>
            {
                // The newest version; of equal ones x64, as the ordering of the list prefers it.
                var newest = group.OrderByDescending(x => x.Installation.Version)
                    .ThenBy(x => x.Installation.Architecture != PlatformArchitecture.X64).First().Installation;
                return new PlatformStarter(group.Key, newest.Architecture, newest.Version);
            })
            .OrderByDescending(x => x.Newest).ThenBy(x => x.Architecture != PlatformArchitecture.X64)];
    }

    /// <summary>The starter for the main button: of the platform the selected base starts with, else the newest one.</summary>
    public static PlatformStarter? Choose(IReadOnlyList<PlatformStarter> starters, string? baseExecutable)
    {
        if (starters.Count == 0) return null;
        if (baseExecutable is not null && For(baseExecutable) is { } own)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (starters.FirstOrDefault(x => string.Equals(x.Executable, Path.GetFullPath(own), comparer)) is { } found) return found;
        }
        return starters[0];
    }

    /// <summary>No arguments: the starter opens its own list, as in the original.</summary>
    public static ProcessStartInfo StartInfo(PlatformStarter starter)
    {
        if (!File.Exists(starter.Executable)) throw new FileNotFoundException($"Стартер 1С не найден: {starter.Executable}. Обновите список платформ.");
        return new ProcessStartInfo(starter.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(starter.Executable)! };
    }

    public static void Start(PlatformStarter starter)
    {
        using var process = Process.Start(StartInfo(starter));
    }
}
