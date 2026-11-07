namespace StartManager.Core;

/// <summary>The original's «Все / x32 / x64» of «Выбор версии платформы» (FormSetVersion).</summary>
public enum PlatformBitFilter { All, X86, X64 }

/// <summary>A row of the version tree: the family 8.3 (level 0), the release 8.3.24 (level 1) or the build 8.3.24.1342 of one
/// bitness (level 2). Only a build has an architecture and a client.</summary>
public sealed record PlatformVersionNode(string Version, int Level, IReadOnlyList<PlatformVersionNode> Children,
    PlatformArchitecture Architecture = PlatformArchitecture.Any, string Executable = "", string Suffix = "");

/// <summary>What «Применить» assigns: the version as chosen (8.3, 8.3.24 or a full number) and the bitness of the rule.</summary>
public sealed record PlatformVersionChoice(string Version, PlatformArchitecture Architecture);

/// <summary>«Назначить версию» of the original (actSetVersion, Shift+Ctrl+F8): the tree of installed versions and the
/// assignment to bases.</summary>
public static class PlatformVersions
{
    /// <summary>Families, releases and builds of the installed platforms. A build installed twice (thick and thin client, two
    /// folders) is one row; the thick client is shown. 8.3 is always offered, so a version can be assigned without an
    /// installation, as the original offers the families of its settings. Releases and builds follow <paramref name="descending"/>;
    /// families always go from the newest. On Windows a 64-bit build is marked (x64), a 32-bit one (x32) when its 64-bit twin is
    /// there too, as in the original. Linux starts a client of its own architecture only (<see cref="PlatformCatalog.Select"/>):
    /// the others are not offered there.</summary>
    public static IReadOnlyList<PlatformVersionNode> Build(IEnumerable<PlatformInstallation> installations, PlatformBitFilter filter, bool descending, bool windows,
        PlatformArchitecture native = PlatformArchitecture.X64)
    {
        var builds = installations.Where(x => x.Version.Major == 8 && x.Version.Revision >= 0 && (windows || x.Architecture == native))
            .GroupBy(x => (x.Version, x.Architecture)).Select(x => x.OrderBy(y => y.Thin).First()).ToList();
        var shown = builds.Where(x => !windows || filter == PlatformBitFilter.All ||
            x.Architecture == (filter == PlatformBitFilter.X86 ? PlatformArchitecture.X86 : PlatformArchitecture.X64)).ToList();
        string Suffix(PlatformInstallation build) => !windows ? "" : build.Architecture switch
        {
            PlatformArchitecture.X64 => " (x64)",
            PlatformArchitecture.Arm64 => " (ARM64)",
            PlatformArchitecture.X86 when builds.Any(x => x.Version == build.Version && x.Architecture != PlatformArchitecture.X86) => " (x32)",
            _ => ""
        };
        IEnumerable<T> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> key) => descending ? items.OrderByDescending(key) : items.OrderBy(key);
        var families = shown.Select(x => x.Version.Minor).Append(3).Distinct().OrderDescending();
        return families.Select(family => new PlatformVersionNode($"8.{family}", 0,
            Order(shown.Where(x => x.Version.Minor == family).GroupBy(x => x.Version.Build), x => x.Key).Select(release =>
                new PlatformVersionNode($"8.{family}.{release.Key}", 1,
                    (descending ? release.OrderByDescending(x => x.Version) : release.OrderBy(x => x.Version)).ThenBy(x => x.Architecture).Select(x =>
                        new PlatformVersionNode(x.Version.ToString(), 2, [], x.Architecture, x.Executable, Suffix(x))).ToList())).ToList())).ToList();
    }

    /// <summary>The row to select first: the build of that version and bitness, the build of that version, the family or
    /// release of that text, otherwise the first family (the original's FindNode). Returns the path from the family down.</summary>
    public static IReadOnlyList<PlatformVersionNode> Find(IReadOnlyList<PlatformVersionNode> tree, string version, PlatformArchitecture architecture)
    {
        var all = new List<IReadOnlyList<PlatformVersionNode>>();
        void Walk(IReadOnlyList<PlatformVersionNode> nodes, PlatformVersionNode[] above)
        {
            foreach (var node in nodes) { PlatformVersionNode[] path = [.. above, node]; all.Add(path); Walk(node.Children, path); }
        }
        Walk(tree, []);
        return all.FirstOrDefault(x => x[^1].Level == 2 && x[^1].Version == version && x[^1].Architecture == architecture)
            ?? all.FirstOrDefault(x => x[^1].Version == version)
            ?? (tree.Count > 0 ? [tree[0]] : []);
    }

    /// <summary>The rule for the chosen row. «x32»/«x64» ask for that bitness; with «Все» a build keeps its own, a family or
    /// release any. Linux starts only its own architecture: no bitness there.</summary>
    public static PlatformVersionChoice Choose(PlatformVersionNode node, PlatformBitFilter filter, bool windows) => new(node.Version, !windows
        ? PlatformArchitecture.Any
        : filter switch
        {
            PlatformBitFilter.X86 => PlatformArchitecture.X86,
            PlatformBitFilter.X64 => PlatformArchitecture.X64,
            _ => node.Level == 2 && node.Architecture is PlatformArchitecture.X86 or PlatformArchitecture.X64 or PlatformArchitecture.Arm64 ? node.Architecture : PlatformArchitecture.Any
        });

    /// <summary>The bases of a group and of its subgroups, as the original's SetVersionForNodeData.</summary>
    public static IReadOnlyList<Guid> InGroup(Library library, string group) =>
        library.Bases.Where(x => Groups.Contains(group, x.Group)).Select(x => x.Id).ToList();

    /// <summary>The version goes into the list (Version= of the .v8i, seen by 1C's own starter) and into the base's own rule,
    /// so that this program starts the same version whatever the common setting is; the rule keeps its unknown fields. A
    /// manually chosen client stays stored but no longer used. Returns the library and the number of bases changed.</summary>
    public static (Library Library, int Changed) Assign(Library library, IReadOnlyCollection<Guid> ids, PlatformVersionChoice choice)
    {
        var rule = new PlatformRule { Version = choice.Version, Architecture = choice.Architecture };
        rule.Validate();
        var changed = 0;
        var bases = library.Bases.Select(entry =>
        {
            if (!ids.Contains(entry.Id)) return entry;
            var next = entry with { PlatformVersion = choice.Version, PlatformRule = (entry.PlatformRule ?? new()) with { Version = rule.Version, Architecture = rule.Architecture } };
            if (next.PlatformVersion != entry.PlatformVersion || next.PlatformRule != entry.PlatformRule) changed++;
            return next;
        }).ToList();
        return (library with { Bases = bases }, changed);
    }
}
