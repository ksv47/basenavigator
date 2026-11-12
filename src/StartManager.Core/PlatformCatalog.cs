using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

public enum PlatformArchitecture { Any, X86, X64, Arm64, PreferX86, PreferX64 }

// Null means legacy/manual selection; existing profiles keep their launch behaviour.
public sealed record PlatformRule
{
    public string Version { get; init; } = "8.3";
    public PlatformArchitecture Architecture { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public void Validate()
    {
        if (!Enum.IsDefined(Architecture))
            throw new ArgumentException("Настройка разрядности создана неподдерживаемой версией программы. Откройте профиль в более новой сборке.");
        if (!PlatformCatalog.IsVersion(Version))
            throw new ArgumentException("Версия платформы: 8.3, 8.3.27 или полный номер 8.3.27.2214. Укажите разрядность из списка.");
    }
}

public sealed record PlatformInstallation(string Executable, Version Version, PlatformArchitecture Architecture)
{
    // The thin client starts only the enterprise mode; the designer needs 1cv8.
    public bool Thin => PlatformCatalog.IsThinClient(Executable);
    public override string ToString() => $"{Version} · {Architecture switch { PlatformArchitecture.X86 => "x86", PlatformArchitecture.X64 => "x64", PlatformArchitecture.Arm64 => "ARM64", _ => "разрядность неизвестна" }}" + (Thin ? " · тонкий клиент" : "");
}

public sealed record PlatformScan(IReadOnlyList<PlatformInstallation> Installations, IReadOnlyList<string> Warnings);

public static class PlatformCatalog
{
    public static bool IsThinClient(string executable) => Path.GetFileNameWithoutExtension(executable).Equals("1cv8c", StringComparison.OrdinalIgnoreCase);

    public static bool IsVersion(string? value) => value is not null && value.Split('.') is var parts &&
        parts.Length is >= 2 and <= 4 && parts[0] == "8" &&
        parts.All(x => x.Length > 0 && (x.Length == 1 || x[0] != '0') && x.All(char.IsAsciiDigit) && int.TryParse(x, out _));

    public static string ArchitectureText(PlatformArchitecture architecture) => architecture switch
    {
        PlatformArchitecture.Any => "автоматически", PlatformArchitecture.X86 => "только x86", PlatformArchitecture.X64 => "только x64",
        PlatformArchitecture.Arm64 => "только ARM64", PlatformArchitecture.PreferX86 => "предпочитать x86", PlatformArchitecture.PreferX64 => "предпочитать x64",
        _ => "неподдерживаемая разрядность"
    };

    /// <summary>The folders of the settings (<see cref="Library.PlatformFolders"/>) searched after the standard ones; set when the
    /// settings are read or saved, so every search of the program sees them.</summary>
    public static IReadOnlyList<string> UserRoots { get; private set; } = [];

    public static void UseFolders(Library library) => UserRoots = [.. library.PlatformFolders.Where(Path.IsPathFullyQualified)];

    /// <summary>The text of the settings field (one folder per line) as the list to keep: trimmed, full paths only, repeats
    /// dropped (case-insensitive on Windows). An empty line is ignored; a relative path is refused with its text.</summary>
    public static List<string> NormalizeFolders(IEnumerable<string> lines)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0) continue;
            if (!Path.IsPathFullyQualified(line) || line.Any(char.IsControl))
                throw new ArgumentException($"Каталог поиска платформ «{line}»: укажите полный путь, по одному каталогу в строке.");
            var folder = Path.GetFullPath(line);
            // A drive or filesystem root keeps its separator; any other folder loses the trailing one.
            if (Path.GetPathRoot(folder) != folder) folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!result.Contains(folder, comparer)) result.Add(folder);
        }
        return result;
    }

    /// <summary>The standard folders, then those of the active profile's settings.</summary>
    public static IEnumerable<string> DefaultRoots() => StandardRoots().Concat(UserRoots);

    /// <summary>The standard folders and the given library's own search folders, independent of the active profile.</summary>
    public static IEnumerable<string> RootsOf(Library library) =>
        StandardRoots().Concat(library.PlatformFolders.Where(Path.IsPathFullyQualified));

    public static IEnumerable<string> StandardRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root, "1cv8");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) foreach (var name in new[] { "1cv8", "1cv8_x86", "1cv8_x64" }) yield return Path.Combine(local, "Programs", name);
        }
        else { yield return "/opt/1cv8"; yield return "/opt/1C"; }
        // Installation folders chosen in the 1C installer; Scan skips repeated and missing folders.
        foreach (var root in InstalledLocations(StartConfigFiles())) yield return root;
    }

    /// <summary>InstalledLocation= lines of 1CEStart settings (several are allowed), fully qualified paths only.</summary>
    public static IEnumerable<string> InstalledLocations(IEnumerable<string> files) =>
        StartConfigValues(files, "InstalledLocation").Where(Path.IsPathFullyQualified);

    internal static IEnumerable<string> StartConfigValues(IEnumerable<string> files, string key)
    {
        foreach (var file in files)
        {
            string text;
            // ReadAllText detects the BOM: the user's file is UTF-8, the common one written by the installer UTF-16.
            try { if (!File.Exists(file)) continue; text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { continue; }
            foreach (var line in text.Split('\n'))
            {
                var index = line.IndexOf('=');
                if (index > 0 && line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) yield return line[(index + 1)..].Trim();
            }
        }
    }

    /// <param name="configured">The search folders of the settings that must exist (a warning each when they do not); by default
    /// those of the active profile when the roots are the default ones.</param>
    public static PlatformScan Scan(IEnumerable<string>? roots = null, IEnumerable<string>? executables = null, IEnumerable<string>? configured = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var found = new Dictionary<string, PlatformInstallation>(comparer);
        var visited = new HashSet<string>(comparer);
        var warnings = new List<string>();
        // A file is read once: a client chosen by hand inside a search folder is met in the walk and again in the explicit paths,
        // and the warning about its unknown version was shown twice (Linux 4, Н2).
        var inspected = new HashSet<string>(comparer);
        void Inspect(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A spelling that is no path (a NUL character) is said once too; it never equals a full path (Codex audit, P3).
                if (inspected.Add(path)) warnings.Add($"Не удалось прочитать {path}: {ex.Message}");
                return;
            }
            if (!inspected.Add(full)) return;
            // The full path is read, not the given spelling: the version from the folder names walks the parents of the text, and
            // «bin/./.» must find what «bin» finds whichever comes first (Codex audit, P2). Links are not resolved.
            try
            {
                if (Read(full) is { } installation) found.TryAdd(full, installation);
                else if (File.Exists(full)) warnings.Add($"Версия клиента не определена: {full}. Для этого файла доступен ручной выбор.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add($"Не удалось прочитать {full}: {ex.Message}"); }
        }
        void Visit(string path, int depth)
        {
            try
            {
                if (!Directory.Exists(path) || !visited.Add(Path.GetFullPath(path))) return;
                // A distribution may contain only the thin client; it still serves the enterprise mode.
                var thick = Path.Combine(path, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
                Inspect(File.Exists(thick) ? thick : Path.Combine(path, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"));
                if (depth == 0) return;
                foreach (var child in Directory.GetDirectories(path))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Visit(child, depth - 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add($"Каталог недоступен {path}: {ex.Message}"); }
        }
        foreach (var root in roots ?? DefaultRoots()) Visit(root, 4);
        // A folder of the settings that is gone or is a file: said once (the standard places are often absent and stay silent).
        foreach (var root in configured ?? (roots is null ? UserRoots : []))
            if (!Directory.Exists(root)) warnings.Add($"Каталог поиска платформ не найден или недоступен: {root}.");
        foreach (var path in executables ?? []) if (!string.IsNullOrWhiteSpace(path)) Inspect(path);
        return new(found.Values.OrderByDescending(x => x.Version).ThenBy(x => x.Architecture).ThenBy(x => x.Thin).ThenBy(x => x.Executable, comparer).ToArray(), warnings);
    }

    public static PlatformInstallation? Read(string executable)
    {
        if (!File.Exists(executable)) return null;
        string? version = null;
        if (OperatingSystem.IsWindows())
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            if (IsVersion(info.FileVersion)) version = info.FileVersion;
        }
        var directory = Path.GetDirectoryName(executable);
        for (var i = 0; version is null && directory is not null && i < 3; i++, directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (IsVersion(name) && name.Split('.').Length == 4) version = name;
        }
        if (version is null) return null;
        return new(Path.GetFullPath(executable), Version.Parse(version), ReadArchitecture(executable));
    }

    public static PlatformArchitecture ReadArchitecture(string executable)
    {
        using var stream = File.OpenRead(executable);
        Span<byte> header = stackalloc byte[64];
        if (stream.ReadAtLeast(header, 64, throwOnEndOfStream: false) < 64) return PlatformArchitecture.Any;
        if (header[0] == 0x7f && header[1] == 'E' && header[2] == 'L' && header[3] == 'F')
        {
            var machine = header[5] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) : BinaryPrimitives.ReadUInt16BigEndian(header[18..]);
            return machine switch { 3 => PlatformArchitecture.X86, 62 => PlatformArchitecture.X64, 183 => PlatformArchitecture.Arm64, _ => PlatformArchitecture.Any };
        }
        if (header[0] != 'M' || header[1] != 'Z') return PlatformArchitecture.Any;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > stream.Length - 6) return PlatformArchitecture.Any;
        stream.Position = offset;
        if (stream.ReadAtLeast(header[..6], 6, throwOnEndOfStream: false) != 6 || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return PlatformArchitecture.Any;
        return BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) switch
        { 0x14c => PlatformArchitecture.X86, 0x8664 => PlatformArchitecture.X64, 0xaa64 => PlatformArchitecture.Arm64, _ => PlatformArchitecture.Any };
    }

    private static readonly Lazy<PlatformRule?> DefaultFromConfig = new(() => ReadDefaultVersion(StartConfigFiles()));
    /// <summary>DefaultVersion of 1CEStart for a record without Version; read once. Replaced by tests.</summary>
    public static Func<PlatformRule?> StartDefaultVersion { get; set; } = () => DefaultFromConfig.Value;

    /// <summary>1CEStart settings: the user's file first, then the common one.</summary>
    public static IEnumerable<string> StartConfigFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData })
                if (Environment.GetFolderPath(folder) is { Length: > 0 } root) yield return Path.Combine(root, "1C", "1CEStart", "1CEStart.cfg");
        }
        else
        {
            if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home)
                yield return Path.Combine(home, ".1C", "1cestart", "1cestart.cfg");
            yield return "/opt/1cv8/common/1cestart.cfg";
        }
    }

    /// <summary>First valid DefaultVersion: version[;bitness], e.g. 8.3;x86_64_prt. Unreadable files are skipped.
    /// Bitness is taken as a preference, like AppArch of the list: a missing x86 build does not block the start.</summary>
    public static PlatformRule? ReadDefaultVersion(IEnumerable<string> files)
    {
        foreach (var value in StartConfigValues(files, "DefaultVersion"))
        {
            var parts = value.Split(';', StringSplitOptions.TrimEntries);
            if (IsVersion(parts[0])) return new() { Version = parts[0], Architecture = ParseBitness(parts.Length > 1 ? parts[1] : "") };
        }
        return null;
    }

    /// <summary>Bitness of 1CEStart (x86, x86_64, with an optional _prt suffix) as a preference; anything else is Any.</summary>
    public static PlatformArchitecture ParseBitness(string value)
    {
        var bitness = value.Trim().ToLowerInvariant();
        return bitness.StartsWith("x86_64", StringComparison.Ordinal) ? PlatformArchitecture.PreferX64
            : bitness.StartsWith("x86", StringComparison.Ordinal) ? PlatformArchitecture.PreferX86 : PlatformArchitecture.Any;
    }

    /// <summary>A release of the record (8.3.27 or 8.3.27.1989), not just its family (8.3).</summary>
    public static bool FullRelease(string? version) => IsVersion(version) && version!.Split('.').Length >= 3;

    /// <summary>The general rule of the settings for this base. As the rule of a branch and as 1CEStart, it does not replace a full
    /// release of the record: a base with Version=8.3.27.1989 starts that release, not the newest 8.3 (decision of the user 12.11.2026:
    /// a thin client of the newest release did not connect to an older server).</summary>
    public static PlatformRule? CommonRule(Library library, BaseEntry entry) => FullRelease(entry.PlatformVersion) ? null : library.PlatformRule;

    /// <summary>The version rule a start of the base follows: its own (or its branch's, after <see cref="PlatformBranches.WithPlatform"/>),
    /// else the general one when no client is chosen for it by hand; null: a file chosen by hand or the version of the record.</summary>
    public static PlatformRule? RuleOf(Library library, BaseEntry entry) =>
        entry.PlatformRule ?? (string.IsNullOrWhiteSpace(entry.Executable) ? CommonRule(library, entry) : null);

    /// <summary>Whether the start keeps the chosen release (/AppAutoCheckVersion-): only for a rule naming one build. A rule of a
    /// family or of a release (8.3, 8.3.27) leaves 1C its own choice of the client by the server's version, which a newest client
    /// of the rule may not match (decision of the user 12.11.2026).</summary>
    public static bool PinsRelease(PlatformRule? rule) => rule is not null && IsVersion(rule.Version) && rule.Version.Split('.').Length == 4;

    /// <summary>No client or rule anywhere: choose like 1CEStart by the .v8i Version, then by DefaultVersion of 1CEStart, or the newest 8.x.
    /// The general client file counts only in the mode «Ручной клиент»: with a general rule the settings keep the path of the field,
    /// disabled, and a record with a full release must not start it (Codex audit 4, F1).</summary>
    public static bool IsImplicit(Library library, BaseEntry entry) => entry.PlatformRule is null && CommonRule(library, entry) is null &&
        string.IsNullOrWhiteSpace(entry.Executable) && (library.PlatformRule is not null || string.IsNullOrWhiteSpace(library.Executable));

    /// <summary>The architecture of the operating system: on Linux the only one a client may have.</summary>
    public static PlatformArchitecture NativeArchitecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    { System.Runtime.InteropServices.Architecture.X64 => PlatformArchitecture.X64,
      System.Runtime.InteropServices.Architecture.Arm64 => PlatformArchitecture.Arm64, _ => PlatformArchitecture.X86 };

    /// <summary>The settings of the list that decide the client of a base in <see cref="Resolve"/> and
    /// <see cref="PlatformBranches.WithPlatform"/> are the same in both: the common client and rule, the folders searched, the main
    /// version and the clients of the branches. Compared by value, without the disk; the branch parameters do not choose the client.</summary>
    public static bool SameClientSettings(Library before, Library now)
    {
        static bool Same(PlatformRule? a, PlatformRule? b) => a?.Version == b?.Version && a?.Architecture == b?.Architecture;
        return before.Executable == now.Executable && Same(before.PlatformRule, now.PlatformRule) && before.MainVersion == now.MainVersion &&
            before.PlatformFolders.SequenceEqual(now.PlatformFolders) && before.Branches.Count == now.Branches.Count &&
            before.Branches.Zip(now.Branches).All(x => x.First.Branch == x.Second.Branch && x.First.Executable == x.Second.Executable &&
                Same(x.First.PlatformRule, x.Second.PlatformRule));
    }

    public static string Resolve(Library library, BaseEntry entry, IReadOnlyList<PlatformInstallation>? installations = null, bool designer = false)
    {
        entry = PlatformBranches.WithPlatform(library, entry);
        var rule = entry.PlatformRule;
        if (rule is null && !string.IsNullOrWhiteSpace(entry.Executable)) return entry.Executable;
        rule ??= CommonRule(library, entry);
        var implicitVersion = IsImplicit(library, entry);
        if (rule is null && !implicitVersion) return library.Executable;
        rule?.Validate();
        // The folders of this library, not of whichever profile was read last.
        var candidates = installations ?? Scan(RootsOf(library), library.Bases.Select(x => x.Executable).Append(library.Executable), library.PlatformFolders).Installations;
        var native = NativeArchitecture;
        rule ??= IsVersion(entry.PlatformVersion) ? new PlatformRule { Version = entry.PlatformVersion }
            : PlatformBranches.IsBranch(library.MainVersion) ? new PlatformRule { Version = library.MainVersion } : StartDefaultVersion() ?? new() { Version = "8" };
        // AppArch of the record is a preference of this base: it replaces an automatic bitness and the preference of a common
        // rule or of 1CEStart, but not a strict bitness nor a preference chosen in the base's own rule.
        var ownPreference = ReferenceEquals(rule, entry.PlatformRule) && rule.Architecture != PlatformArchitecture.Any;
        if (entry.ListArchitecture != PlatformArchitecture.Any && !ownPreference &&
            rule.Architecture is PlatformArchitecture.Any or PlatformArchitecture.PreferX86 or PlatformArchitecture.PreferX64)
            rule = rule with { Architecture = entry.ListArchitecture };
        return Select(rule, candidates, native, OperatingSystem.IsWindows(), designer).Executable;
    }

    public static PlatformInstallation Select(PlatformRule rule, IReadOnlyList<PlatformInstallation> candidates, PlatformArchitecture native, bool windows, bool designer = false)
    {
        // "8" is only used internally for an entry without Version: any installed 8.x.
        if (rule.Version != "8") rule.Validate();
        var prefix = rule.Version.Split('.').Select(int.Parse).ToArray();
        var preferred = rule.Architecture switch { PlatformArchitecture.PreferX64 => PlatformArchitecture.X64,
            PlatformArchitecture.PreferX86 => PlatformArchitecture.X86, _ => windows ? PlatformArchitecture.X86 : native };
        var match = candidates.Where(x =>
        {
            var parts = new[] { x.Version.Major, x.Version.Minor, x.Version.Build, x.Version.Revision };
            return prefix.Select((value, i) => parts[i] == value).All(x => x) &&
                (windows ? x.Architecture == native || x.Architecture == PlatformArchitecture.X86 || native == PlatformArchitecture.Arm64 && x.Architecture == PlatformArchitecture.X64 : x.Architecture == native) &&
                (rule.Architecture is PlatformArchitecture.Any or PlatformArchitecture.PreferX86 or PlatformArchitecture.PreferX64 || rule.Architecture == x.Architecture);
        }).OrderByDescending(x => x.Version).ThenByDescending(x => x.Architecture == preferred).ThenBy(x => x.Thin).ThenBy(x => x.Executable, StringComparer.Ordinal).ToArray();
        var version = rule.Version == "8" ? "8.x" : rule.Version;
        if (designer && match.Length > 0 && match.All(x => x.Thin))
            throw new InvalidOperationException($"Для конфигуратора нужен толстый клиент 1cv8 версии {version}; установлен только тонкий клиент 1cv8c.");
        return (designer ? match.FirstOrDefault(x => !x.Thin) : match.FirstOrDefault()) ?? throw new InvalidOperationException(
            $"Установленная совместимая платформа {version} ({ArchitectureText(rule.Architecture)}) не найдена. {Found(candidates)} Измените правило версии или выберите клиент вручную.");
    }

    /// <summary>What the search found, for the message about a missing platform: the versions with their bitness, or where to add a folder.</summary>
    public static string Found(IReadOnlyList<PlatformInstallation> candidates)
    {
        if (candidates.Count == 0)
            return "Платформы 1С не найдены ни в стандартных каталогах, ни в каталогах поиска из настроек. Установите платформу или добавьте её каталог " +
                "в «Настройки» → «Используемая платформа» → «Пути поиска платформы».";
        static string Bits(PlatformArchitecture a) => a switch { PlatformArchitecture.X86 => "x86", PlatformArchitecture.X64 => "x64", PlatformArchitecture.Arm64 => "ARM64", _ => "разрядность неизвестна" };
        var versions = candidates.Select(x => $"{x.Version} ({Bits(x.Architecture)})").Distinct().ToArray();
        return "Найдено: " + string.Join("; ", versions.Take(8)) + (versions.Length > 8 ? $" и ещё {versions.Length - 8}" : "") + ".";
    }
}
