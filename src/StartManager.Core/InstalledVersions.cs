using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StartManager.Core;

/// <summary>A 1C entry of Windows «Programs and Features» (the original's TRegUninstall): what removes an installation. Architecture is
/// what the registry view proves: X86 for the 32-bit view, X64 for the 64-bit view of an x64 Windows, Any when unknown (the per-user
/// key is shared by both views; the 64-bit view of ARM64 Windows holds x64 and ARM64 products).</summary>
public sealed record PlatformUninstallEntry(string DisplayName, string DisplayVersion, string InstallLocation, string InstallSource,
    DateTime? InstallDate, string UninstallString, PlatformArchitecture Architecture);

public enum InstalledVersionLevel { Family, Build, File }

/// <summary>A row of «Список установленных версий»: a family (8.3), a build in its folder, or a client file of the build.</summary>
public sealed record InstalledVersionNode(InstalledVersionLevel Level, string Version, PlatformArchitecture Architecture, string Path,
    PlatformUninstallEntry? Uninstall, IReadOnlyList<InstalledVersionNode> Children)
{
    public string Text => Level switch
    {
        InstalledVersionLevel.Build => Version + Architecture switch
        {
            PlatformArchitecture.X86 => " (x86)", PlatformArchitecture.X64 => " (x64)", PlatformArchitecture.Arm64 => " (ARM64)", _ => ""
        },
        InstalledVersionLevel.File => System.IO.Path.GetFileName(Path),
        _ => Version
    };
}

/// <summary>The original's FormV8Apps («8.x»): the installed 1C:Enterprise 8 versions as families, builds and their clients, with the
/// installation date, source and uninstaller of a build from the Windows registry.</summary>
public static partial class InstalledVersions
{
    private static readonly string[] Clients = ["1cv8", "1cv8c", "1cv8s"];

    public static IReadOnlyList<InstalledVersionNode> Build(IEnumerable<PlatformInstallation> installations, IReadOnlyList<PlatformUninstallEntry> entries,
        bool windows, PlatformArchitecture native)
    {
        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // One build per installation folder: its thick and thin clients are files of the same build.
        var builds = installations.GroupBy(x => System.IO.Path.GetDirectoryName(x.Executable) ?? x.Executable, comparer)
            .Select(x => (Folder: x.Key, x.First().Version, x.First().Architecture)).ToList();
        // Equal builds in several folders (a copy) cannot be told apart by version: none of them takes a locationless entry.
        var twins = builds.GroupBy(x => (x.Version, x.Architecture)).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        var matches = builds.ToDictionary(x => x.Folder,
            x => Match(x.Folder, x.Version, x.Architecture, entries, windows, byVersion: !twins.Contains((x.Version, x.Architecture))), comparer);
        // An entry that fits several folders (a copy of a build, two equal locationless builds) belongs to none of them.
        var shared = matches.Values.OfType<PlatformUninstallEntry>().GroupBy(x => x, ReferenceEqualityComparer.Instance)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(ReferenceEqualityComparer.Instance);
        PlatformUninstallEntry? Owned(string folder) => matches[folder] is { } entry && !shared.Contains(entry) ? entry : null;
        return builds.GroupBy(x => $"{x.Version.Major}.{x.Version.Minor}")
            .OrderByDescending(x => x.First().Version.Major).ThenByDescending(x => x.First().Version.Minor)
            .Select(family => new InstalledVersionNode(InstalledVersionLevel.Family, family.Key, PlatformArchitecture.Any, "", null,
                family.OrderByDescending(x => x.Version).ThenBy(x => x.Architecture).ThenBy(x => x.Folder, comparer)
                    .Select(x => new InstalledVersionNode(InstalledVersionLevel.Build, x.Version.ToString(), x.Architecture, x.Folder,
                        Owned(x.Folder), Files(x.Folder, windows, x.Architecture)))
                    .ToList()))
            .ToList();
    }

    private static IReadOnlyList<InstalledVersionNode> Files(string folder, bool windows, PlatformArchitecture architecture) =>
        Clients.Select(x => System.IO.Path.Combine(folder, windows ? x + ".exe" : x)).Where(File.Exists)
            .Select(x => new InstalledVersionNode(InstalledVersionLevel.File, "", architecture, x, null, [])).ToList();

    // «1С:Предприятие 8 (x86-64) (8.3.25.1257)», «1C:Enterprise 8 (x86-64) Thin client (8.5.1.1343)» (the installed name of the official
    // 8.5 thin client MSI, its ProductNameLocal), «1С:Предприятие 8.2»: the platform itself, not another product of 1C and not another
    // «1C…» publisher. The thin client is named before or after the bitness.
    [GeneratedRegex(@"^1[CС]:\s*(Предприятие|Enterprise)\s+8(?<family>\.\d+)?(\s+(Тонкий клиент|Thin client))?(\s+\((?<bits>x86-64|x86|x64|64-bit|ARM64)\))?(\s+(Тонкий клиент|Thin client))?(\s+\((?<build>\d+(\.\d+){1,3})\))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlatformName();

    /// <summary>Whether a registry entry is a 1C:Enterprise 8 platform consistent with this build: its name, the family and build its name
    /// states, DisplayVersion, the bitness of its name and of its registry view.</summary>
    public static bool IsPlatform(PlatformUninstallEntry entry, Version version, PlatformArchitecture architecture)
    {
        if (PlatformName().Match(entry.DisplayName.Trim()) is not { Success: true } name) return false;
        if (entry.DisplayVersion.Trim() is { Length: > 0 } shown && (!Version.TryParse(shown, out var stated) || stated != version)) return false;
        if (entry.Architecture != PlatformArchitecture.Any && entry.Architecture != architecture) return false;
        if (version.Major != 8 || name.Groups["family"].Success && name.Groups["family"].Value != "." + version.Minor) return false;
        // «(8.3.24)» or «(8.3.24.1342)»: every stated part is the build's.
        int[] parts = [version.Major, version.Minor, version.Build, version.Revision];
        if (name.Groups["build"].Success && !name.Groups["build"].Value.Split('.').Select((x, i) => int.TryParse(x, out var n) && n == parts[i]).All(x => x))
            return false;
        var bits = name.Groups["bits"].Value.ToLowerInvariant();
        return bits switch
        {
            "x86-64" or "x64" or "64-bit" => architecture == PlatformArchitecture.X64,
            "arm64" => architecture == PlatformArchitecture.Arm64,
            "x86" => architecture == PlatformArchitecture.X86,
            _ => true
        };
    }

    /// <summary>The registry entry of a build: the platform installed into its folder (or the folder above bin); without a location only
    /// a platform entry of the same version whose registry view proves the same bitness. Anything ambiguous gets no entry: another
    /// build or product is never removed instead of this one.</summary>
    public static PlatformUninstallEntry? Match(string folder, Version version, PlatformArchitecture architecture, IReadOnlyList<PlatformUninstallEntry> entries,
        bool windows, bool byVersion = true)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Trim(string path) => path.Trim().Trim('"').TrimEnd('\\', '/');
        var own = Trim(folder);
        // Split by the rules of the system the paths come from, not of the one running: a Windows path keeps its backslashes on Linux.
        var cut = windows ? own.LastIndexOfAny(['\\', '/']) : own.LastIndexOf('/');
        var parent = cut > 0 && own[(cut + 1)..].Equals("bin", comparison) ? own[..cut] : null;
        var located = entries.Where(x => Trim(x.InstallLocation) is { Length: > 0 } location &&
            (location.Equals(own, comparison) || parent is not null && location.Equals(parent, comparison))).ToList();
        // Another product in the same folder is ignored; two platform entries for it are ambiguous.
        var platforms = located.Where(x => IsPlatform(x, version, architecture)).ToList();
        if (located.Count > 0) return platforms.Count == 1 ? platforms[0] : null;
        if (!byVersion) return null;
        var versioned = entries.Where(x => Trim(x.InstallLocation).Length == 0 && x.Architecture != PlatformArchitecture.Any &&
            x.Architecture == architecture && IsPlatform(x, version, architecture) && x.DisplayVersion.Trim().Length > 0).ToList();
        return versioned.Count == 1 ? versioned[0] : null;
    }

    /// <summary>InstallDate of the registry: yyyyMMdd.</summary>
    public static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value?.Trim(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    /// <summary>1C:Enterprise 8 entries of «Programs and Features»: both registry views of the machine and the user's own installations.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<PlatformUninstallEntry> ReadRegistry()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var result = new List<PlatformUninstallEntry>();
        var native = PlatformCatalog.NativeArchitecture;
        // What a view proves: the 32-bit one holds x86 products; the 64-bit one of x64 Windows x64 products; the per-user key is
        // shared by both views and the 64-bit view of ARM64 Windows holds x64 and ARM64 alike.
        foreach (var (hive, view, bits) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, native == PlatformArchitecture.X64 ? PlatformArchitecture.X64 : PlatformArchitecture.Any),
            (RegistryHive.LocalMachine, RegistryView.Registry32, PlatformArchitecture.X86),
            (RegistryHive.CurrentUser, RegistryView.Default, PlatformArchitecture.Any)
        })
        {
            if (view == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem) continue;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(key);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var item = uninstall.OpenSubKey(name);
                        if (item is null) continue;
                        string Value(string value) => item.GetValue(value) is string text ? text.Trim() : "";
                        var display = Value("DisplayName");
                        if (!PlatformName().IsMatch(display)) continue;
                        var command = Value("UninstallString");
                        if (command.Length == 0) command = Value("QuietUninstallString");
                        result.Add(new(display, Value("DisplayVersion"), Value("InstallLocation"), Value("InstallSource"), ParseDate(Value("InstallDate")),
                            command, bits));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return result;
    }

    [GeneratedRegex(@"\.(exe|cmd|bat)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramEnd();

    [GeneratedRegex(@"^/[IX]\s*(?<code>\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\})$", RegexOptions.IgnoreCase)]
    private static partial Regex MsiArguments();

    /// <summary>The program and arguments that remove an installation. An MSI entry («MsiExec.exe /I{code}» opens the maintenance of the
    /// product) becomes its removal «msiexec /x {code}»; any other command is run as the registry holds it. A command whose program
    /// cannot be told for sure is refused rather than run as something else.</summary>
    public static (string FileName, string Arguments) UninstallCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) throw new ArgumentException("Для этой версии в системе нет команды удаления.");
        string file, arguments;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) throw new ArgumentException("Команда удаления в системе повреждена: " + command);
            (file, arguments) = (command[1..end], command[(end + 1)..]);
            if (arguments.Length > 0 && !char.IsWhiteSpace(arguments[0])) throw new ArgumentException("Команда удаления в системе не распознана: " + command);
        }
        else if (!command.Any(char.IsWhiteSpace)) (file, arguments) = (command, "");
        else
        {
            // An unquoted path may contain spaces, so its program is told by a word ending with .exe, .cmd or .bat («C:\tools.exe\x.exe»
            // ends once). Only a single such word makes it certain: «C:\tools.exe backup\uninstall.exe /S» could be either and is refused.
            var ends = ProgramEnd().Matches(command).Select(x => x.Index + x.Length).ToList();
            // «msiexec /x {code}»: Windows Installer by its bare name.
            if (ends.Count == 0 && command.Split((char[]?)null, 2)[0].Equals("msiexec", StringComparison.OrdinalIgnoreCase)) ends.Add("msiexec".Length);
            if (ends.Count != 1) throw new ArgumentException("Команда удаления в системе не распознана: " + command);
            (file, arguments) = (command[..ends[0]], command[ends[0]..]);
        }
        arguments = arguments.Trim();
        // A Windows command: its program name follows the last backslash or slash whatever system reads it.
        var program = file[(file.LastIndexOfAny(['\\', '/']) + 1)..];
        if ((program.Equals("msiexec", StringComparison.OrdinalIgnoreCase) || program.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) &&
            MsiArguments().Match(arguments) is { Success: true } msi)
            return ("msiexec.exe", "/x " + msi.Groups["code"].Value);
        return (file, arguments);
    }
}
