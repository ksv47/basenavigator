using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

/// <summary>The original's «Настройки платформы» for one branch (8.5, 8.3, 8.2): the client of its bases — a file chosen by hand
/// (<see cref="Executable"/>), an installed version chosen by a rule (<see cref="PlatformRule"/>), or neither («По умолчанию»: the
/// general setting) — and <see cref="Parameters"/> put before the own parameters of every base of the branch.</summary>
public sealed record BranchSettings
{
    public string Branch { get; init; } = "";
    public string Executable { get; init; } = "";
    public PlatformRule? PlatformRule { get; init; }
    public string Parameters { get; init; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public bool IsDefault => string.IsNullOrWhiteSpace(Executable) && PlatformRule is null && string.IsNullOrWhiteSpace(Parameters);

    public void Validate()
    {
        if (!PlatformBranches.IsBranch(Branch) || Executable is null || Parameters is null || Parameters.Any(char.IsControl))
            throw new InvalidDataException("Повреждены настройки платформы по веткам.");
        PlatformRule?.Validate();
        if (PlatformRule is { } rule && PlatformBranches.Family(rule.Version) != Branch)
            throw new ArgumentException($"Версия {rule.Version} не относится к ветке {Branch}.");
    }
}

/// <summary>
/// The branch of a base and what its settings change at a start. The branch is the family of Version= of the record; a record without
/// it belongs to the «Основная версия платформы» of the settings, else to DefaultVersion of 1CEStart. As in the original
/// (V8SelectRelease, V8GetSettingAppFile): the base's own client or rule always wins; the branch's file applies to every other base of
/// the branch; the branch's version rule only to a record without a full release (none, or just the family such as 8.3).
/// </summary>
public static class PlatformBranches
{
    /// <summary>The branches the settings show, newest first (decision of the user 11.10.2026: 8.4 practically absent, 8.1 dead).</summary>
    public static readonly string[] Shown = ["8.5", "8.3", "8.2"];

    public static bool IsBranch(string? value) => value is not null && Shown.Contains(value);

    /// <summary>8.3 of 8.3, 8.3.27 or 8.3.27.1989; null for anything else.</summary>
    public static string? Family(string? version)
    {
        if (!PlatformCatalog.IsVersion(version ?? "")) return null;
        var parts = version!.Split('.');
        return parts[0] + "." + parts[1];
    }

    private static bool FullRelease(string version) => PlatformCatalog.FullRelease(version);

    public static string? Of(Library library, BaseEntry entry) =>
        Family(entry.PlatformVersion) ?? (IsBranch(library.MainVersion) ? library.MainVersion : Family(PlatformCatalog.StartDefaultVersion()?.Version));

    public static BranchSettings? SettingsOf(Library library, BaseEntry entry) =>
        Of(library, entry) is { } branch ? library.Branches.FirstOrDefault(x => x.Branch == branch) : null;

    /// <summary>The base with the client of its branch, when it has none of its own.</summary>
    public static BaseEntry WithPlatform(Library library, BaseEntry entry)
    {
        if (entry.PlatformRule is not null || !string.IsNullOrWhiteSpace(entry.Executable) || SettingsOf(library, entry) is not { } branch) return entry;
        if (!string.IsNullOrWhiteSpace(branch.Executable)) return entry with { Executable = branch.Executable };
        if (branch.PlatformRule is { } rule && !FullRelease(entry.PlatformVersion)) return entry with { PlatformRule = rule };
        return entry;
    }

    /// <summary>The base as it is started: the client of its branch and the branch's parameters before its own.</summary>
    public static BaseEntry ForStart(Library library, BaseEntry entry)
    {
        var result = WithPlatform(library, entry);
        if (SettingsOf(library, entry) is { Parameters: { Length: > 0 } parameters } && parameters.Trim().Length > 0)
            result = result with { AdditionalParameters = (parameters.Trim() + " " + entry.EffectiveParameters).Trim() };
        return result;
    }
}
