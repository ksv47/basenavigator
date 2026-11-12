using System.Diagnostics;

namespace StartManager.Core;

public sealed record BaseInfo(string Platform, DateTime? Modified, long? Size, string Note)
{
    public bool PlatformMissing { get; init; }
    public string ArchitectureLabel { get; init; } = "";
    /// <summary>The exact release the base starts with (8.3.27.1989), as the original's status bar shows; empty when no client is found.</summary>
    public string Release { get; init; } = "";
    /// <summary>A file base whose folder has no 1Cv8.1CD: the original shows it red with a question mark.</summary>
    public bool BaseMissing { get; init; }
    public static BaseInfo Read(BaseEntry entry, Library library, IReadOnlyList<PlatformInstallation> installations)
    {
        try
        {
            // The client of the base's branch as the start chooses it (Настройки платформы), so the column shows that version.
            entry = PlatformBranches.WithPlatform(library, entry);
            var executable = PlatformCatalog.Resolve(library, entry, installations);
            var implicitClient = PlatformCatalog.IsImplicit(library, entry);
            // A client chosen by hand is not described by Version= of the record even as a guess: its release is unknown ("—") unless
            // the scan found it (Grok audit, P3).
            var result = Read(entry with { Executable = executable, PlatformVersion = implicitClient ? entry.PlatformVersion : "" }, executable);
            var installation = installations.FirstOrDefault(x => string.Equals(x.Executable, executable,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (result.Platform == "—" && installation is not null) result = result with { Platform = installation.Version.ToString() };
            var rule = PlatformCatalog.RuleOf(library, entry);
            // Version= of the record names the platform only when it chooses the client; a client chosen by hand shows its own release
            // (Codex audit 4, F4).
            var byRecord = implicitClient && entry.PlatformVersion.Length > 0;
            return result with { Platform = rule?.Version ?? (byRecord ? entry.PlatformVersion : result.Platform),
                ArchitectureLabel = installation?.Architecture switch { PlatformArchitecture.X86 => "x86", PlatformArchitecture.X64 => "x64", PlatformArchitecture.Arm64 => "ARM64", _ => "" },
                PlatformMissing = !File.Exists(executable),
                Release = File.Exists(executable) && Version.TryParse(result.Platform, out _) ? result.Platform : "",
                Note = result.Note + "\nНайденная платформа: " + (installation?.ToString() ?? result.Platform) + "\nКлиент: " + executable };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            var result = Read(entry with { Executable = "" }, "");
            var rule = entry.PlatformRule ?? PlatformCatalog.CommonRule(library, entry);
            return result with { Platform = rule?.Version ?? (entry.PlatformVersion.Length > 0 ? entry.PlatformVersion : "Не найдена"), PlatformMissing = true,
                ArchitectureLabel = rule?.Architecture switch { PlatformArchitecture.X86 => "x86", PlatformArchitecture.X64 => "x64", PlatformArchitecture.Arm64 => "ARM64", _ => "" },
                Note = ex.Message + "\n" + result.Note };
        }
    }

    public static BaseInfo Read(BaseEntry entry, string defaultExecutable)
    {
        var executable = entry.Executable.Length > 0 ? entry.Executable : defaultExecutable;
        string platform = "—";
        if (File.Exists(executable))
        {
            var directory = Path.GetDirectoryName(executable)!;
            var folder = Path.GetFileName(directory);
            if (folder.Equals("bin", StringComparison.OrdinalIgnoreCase)) folder = Path.GetFileName(Path.GetDirectoryName(directory));
            if (Version.TryParse(folder, out _)) platform = folder;
            else if (OperatingSystem.IsWindows())
            {
                try { platform = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "—"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { platform = "—"; }
            }
        }
        if (platform == "—" && entry.PlatformVersion.Length > 0) platform = entry.PlatformVersion + " *";
        if (entry.Kind == ConnectionKind.Server) return new(platform, null, null, "Серверная база: размер и дата локального файла неприменимы.");
        if (entry.Kind == ConnectionKind.Web) return new(platform, null, null, "База на веб-сервере: размер и дата локального файла неприменимы.");
        try
        {
            var file = new FileInfo(Path.Combine(entry.Location, "1Cv8.1CD"));
            if (file.Exists) return new(platform, file.LastWriteTime, file.Length, "");
            // Only a proven absence is marked: a path of another OS or a folder that may not be read stays unmarked.
            return ListMaintenance.Check(entry.Location) switch
            {
                ListMaintenance.Presence.Missing => new(platform, null, null, "Информационная база не найдена: в каталоге нет файла 1Cv8.1CD или каталога нет.") { BaseMissing = true },
                _ when ListMaintenance.ForeignPath(entry.Location) => new(platform, null, null, "Путь другой операционной системы: база этого списка открывается на другом компьютере."),
                _ => new(platform, null, null, "Файл 1Cv8.1CD недоступен: нет прав на каталог или путь неполный.")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(platform, null, null, "Не удалось прочитать сведения о файле."); }
    }
}
