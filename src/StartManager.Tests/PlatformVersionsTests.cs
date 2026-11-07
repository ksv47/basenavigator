using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Выбор версии платформы» (Shift+Ctrl+F8): the tree of installed versions and the version assigned to bases.</summary>
public sealed class PlatformVersionsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bn-set-version-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "ibases.v8i");
    public PlatformVersionsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private const string A = "11111111-1111-1111-1111-111111111111", B = "22222222-2222-2222-2222-222222222222", C = "33333333-3333-3333-3333-333333333333";
    private const string List = "[Бухгалтерия]\r\nConnect=File=\"C:\\buh\";\r\nID=" + A + "\r\nFolder=/Работа\r\nVersion=8.3\r\nCustom=a\r\n" +
        "[Склад]\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nID=" + B + "\r\nFolder=/Работа/Склады\r\n" +
        "[Зарплата]\r\nConnect=Srvr=\"srv\";Ref=\"zup\";\r\nID=" + C + "\r\nFolder=/\r\nVersion=8.3.24\r\n";

    private static PlatformInstallation Client(string version, PlatformArchitecture architecture, string file = "1cv8.exe") =>
        new(Path.Combine(@"C:\1cv8", version + (architecture == PlatformArchitecture.X64 ? "-64" : ""), "bin", file), Version.Parse(version), architecture);
    private static readonly PlatformInstallation[] Installed =
    [
        Client("8.3.24.1342", PlatformArchitecture.X86), Client("8.3.24.1342", PlatformArchitecture.X64),
        Client("8.3.25.1000", PlatformArchitecture.X86, "1cv8c.exe"), Client("8.3.25.1000", PlatformArchitecture.X86),
        Client("8.2.19.130", PlatformArchitecture.X86)
    ];
    private static string Text(PlatformVersionNode node) => node.Version + node.Suffix;
    private static BaseEntry Base(Library library, string id) => library.Bases.Single(x => x.Id == Guid.Parse(id));
    private (V8iStore Store, Library Library) Open()
    {
        File.WriteAllText(PathName, List, new UTF8Encoding(false));
        var store = new V8iStore(PathName); return (store, store.Load(new()));
    }

    [Fact] public void TreeHasFamiliesReleasesAndBuildsWithTheirBitness()
    {
        var tree = PlatformVersions.Build(Installed, PlatformBitFilter.All, descending: true, windows: true);
        Assert.Equal(["8.3", "8.2"], tree.Select(x => x.Version));
        Assert.Equal(["8.3.25", "8.3.24"], tree[0].Children.Select(x => x.Version));
        // As the original: x64 is always marked, x32 only beside its 64-bit twin; one row for a build with thick and thin clients.
        Assert.Equal(["8.3.24.1342 (x32)", "8.3.24.1342 (x64)"], tree[0].Children[1].Children.Select(Text));
        var single = Assert.Single(tree[0].Children[0].Children);
        Assert.Equal("8.3.25.1000", Text(single));
        Assert.EndsWith("1cv8.exe", single.Executable);
        Assert.Equal(["8.2.19.130"], tree[1].Children.SelectMany(x => x.Children).Select(Text));

        var ascending = PlatformVersions.Build(Installed, PlatformBitFilter.All, descending: false, windows: true);
        Assert.Equal(["8.3", "8.2"], ascending.Select(x => x.Version));
        Assert.Equal(["8.3.24", "8.3.25"], ascending[0].Children.Select(x => x.Version));

        // x64: only the 64-bit build; 8.3 stays even without one, the empty 8.2 goes.
        var x64 = PlatformVersions.Build(Installed, PlatformBitFilter.X64, descending: true, windows: true);
        Assert.Equal(["8.3"], x64.Select(x => x.Version));
        Assert.Equal(["8.3.24.1342 (x64)"], x64[0].Children.SelectMany(x => x.Children).Select(Text));
        var x86 = PlatformVersions.Build(Installed, PlatformBitFilter.X86, descending: true, windows: true);
        Assert.Equal(["8.3.25.1000", "8.3.24.1342 (x32)", "8.2.19.130"], x86.SelectMany(x => x.Children).SelectMany(x => x.Children).Select(Text));


        var none = Assert.Single(PlatformVersions.Build([], PlatformBitFilter.All, true, true));
        Assert.Equal("8.3", none.Version); Assert.Empty(none.Children);
    }

    [Fact] public void LinuxOffersOnlyBuildsOfItsOwnArchitecture()
    {
        // Linux starts a client of its own architecture only: a foreign build is not offered, a mixed install gives one row.
        var linux = PlatformVersions.Build(Installed, PlatformBitFilter.X86, descending: true, windows: false, PlatformArchitecture.X64);
        var builds = linux.SelectMany(x => x.Children).SelectMany(x => x.Children).ToList();
        var only = Assert.Single(builds);
        Assert.Equal(("8.3.24.1342", "", PlatformArchitecture.X64), (only.Version, only.Suffix, only.Architecture));
        Assert.Equal(["8.3"], linux.Select(x => x.Version));
        // What is offered also starts: the rule of the choice selects that very client.
        var choice = PlatformVersions.Choose(only, PlatformBitFilter.All, windows: false);
        Assert.Equal(only.Executable, PlatformCatalog.Select(new() { Version = choice.Version, Architecture = choice.Architecture }, Installed,
            PlatformArchitecture.X64, windows: false).Executable);
        var foreign = PlatformVersions.Build([Client("8.3.24.1342", PlatformArchitecture.X86)], PlatformBitFilter.All, true, false, PlatformArchitecture.X64);
        Assert.Empty(Assert.Single(foreign).Children);
    }

    [Fact] public void FirstRowIsTheVersionOfTheBase()
    {
        var tree = PlatformVersions.Build(Installed, PlatformBitFilter.All, true, true);
        Assert.Equal(["8.3", "8.3.24", "8.3.24.1342"], PlatformVersions.Find(tree, "8.3.24.1342", PlatformArchitecture.X64).Select(x => x.Version));
        Assert.Equal(PlatformArchitecture.X64, PlatformVersions.Find(tree, "8.3.24.1342", PlatformArchitecture.X64)[^1].Architecture);
        Assert.Equal(PlatformArchitecture.X86, PlatformVersions.Find(tree, "8.3.24.1342", PlatformArchitecture.Any)[^1].Architecture);
        Assert.Equal(["8.3", "8.3.25"], PlatformVersions.Find(tree, "8.3.25", PlatformArchitecture.Any).Select(x => x.Version));
        Assert.Equal(["8.2"], PlatformVersions.Find(tree, "8.2", PlatformArchitecture.Any).Select(x => x.Version));
        Assert.Equal(["8.3"], PlatformVersions.Find(tree, "8.3.99.1", PlatformArchitecture.Any).Select(x => x.Version));
        Assert.Equal(["8.3"], PlatformVersions.Find(tree, "", PlatformArchitecture.Any).Select(x => x.Version));
    }

    [Fact] public void ChoiceTakesTheBitnessOfTheFilterOrOfTheBuild()
    {
        var tree = PlatformVersions.Build(Installed, PlatformBitFilter.All, true, true);
        var x64 = PlatformVersions.Find(tree, "8.3.24.1342", PlatformArchitecture.X64)[^1];
        Assert.Equal(new PlatformVersionChoice("8.3.24.1342", PlatformArchitecture.X64), PlatformVersions.Choose(x64, PlatformBitFilter.All, true));
        Assert.Equal(new PlatformVersionChoice("8.3", PlatformArchitecture.Any), PlatformVersions.Choose(tree[0], PlatformBitFilter.All, true));
        Assert.Equal(new PlatformVersionChoice("8.3.25", PlatformArchitecture.X86), PlatformVersions.Choose(tree[0].Children[0], PlatformBitFilter.X86, true));
        Assert.Equal(new PlatformVersionChoice("8.3", PlatformArchitecture.X64), PlatformVersions.Choose(tree[0], PlatformBitFilter.X64, true));
        Assert.Equal(new PlatformVersionChoice("8.3.24.1342", PlatformArchitecture.Any), PlatformVersions.Choose(x64, PlatformBitFilter.X64, false));
    }

    [Fact] public void GroupAssignmentCoversSubgroupsAndKeepsTheRestOfTheRule()
    {
        var extra = new Dictionary<string, System.Text.Json.JsonElement> { ["Future"] = System.Text.Json.JsonDocument.Parse("1").RootElement };
        var library = new Library
        {
            Bases =
            [
                new() { Id = Guid.Parse(A), Name = "Бухгалтерия", Group = "Работа", Location = @"C:\buh", Executable = @"C:\manual\1cv8.exe" },
                new() { Id = Guid.Parse(B), Name = "Склад", Group = "Работа/Склады", Location = @"C:\stock", PlatformRule = new() { Version = "8.2", Architecture = PlatformArchitecture.X86, Extra = extra } },
                new() { Id = Guid.Parse(C), Name = "Зарплата", Group = "Работа2", Location = @"C:\zup" }
            ]
        };
        var ids = PlatformVersions.InGroup(library, "Работа");
        Assert.Equal([Guid.Parse(A), Guid.Parse(B)], ids);
        var (next, changed) = PlatformVersions.Assign(library, ids, new("8.3.24", PlatformArchitecture.X64));
        Assert.Equal(2, changed);
        foreach (var entry in next.Bases.Take(2))
        {
            Assert.Equal("8.3.24", entry.PlatformVersion);
            Assert.Equal(("8.3.24", PlatformArchitecture.X64), (entry.PlatformRule!.Version, entry.PlatformRule.Architecture));
        }
        Assert.Same(extra, Base(next, B).PlatformRule!.Extra);
        Assert.Equal(@"C:\manual\1cv8.exe", Base(next, A).Executable);
        // The rule of the base is taken before the manual client.
        Assert.Equal("8.3.24", next.Bases[0].PlatformRule!.Version);
        Assert.Same(library.Bases[2], next.Bases[2]);
        Assert.Equal(0, PlatformVersions.Assign(next, ids, new("8.3.24", PlatformArchitecture.X64)).Changed);
        Assert.Throws<ArgumentException>(() => PlatformVersions.Assign(library, ids, new("8.3.x", PlatformArchitecture.Any)));
    }

    [Fact] public void VersionGoesIntoTheSharedListInPlace()
    {
        var (store, library) = Open();
        var (next, _) = PlatformVersions.Assign(library, PlatformVersions.InGroup(library, "Работа"), new("8.3.24.1342", PlatformArchitecture.X86));
        store.Save(next);
        var text = File.ReadAllText(PathName);
        // The line is replaced where it stands; a record without Version gets one at its top, as any new key; the other base and unknown lines stay.
        Assert.Contains("Folder=/Работа\r\nVersion=8.3.24.1342\r\nCustom=a\r\n", text);
        Assert.Contains("[Склад]\r\nVersion=8.3.24.1342\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nID=" + B + "\r\nFolder=/Работа/Склады\r\n", text);
        Assert.Contains("Folder=/\r\nVersion=8.3.24\r\n", text);
        var reloaded = new V8iStore(PathName).Load(next);
        Assert.Equal("8.3.24.1342", Base(reloaded, A).PlatformVersion);
        Assert.Equal(PlatformArchitecture.X86, Base(reloaded, A).PlatformRule!.Architecture);
        Assert.DoesNotContain("PlatformRule", text);
    }

    [Fact] public void RuleAloneDoesNotWriteTheSharedList()
    {
        var (store, library) = Open();
        var before = File.ReadAllBytes(PathName);
        // Version=8.3 already: only the local rule changes.
        store.Save(PlatformVersions.Assign(library, [Guid.Parse(A)], new("8.3", PlatformArchitecture.X64)).Library);
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Fact] public void RuleAloneLeavesRecordsWithoutIdUntouched()
    {
        // Records without ID are known by computed IDs; a rule of their own is local and must not make the list get IDs.
        const string noIds = "[Склад]\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nVersion=8.3\r\n[Зарплата]\r\nConnect=Srvr=\"srv\";Ref=\"zup\";\r\nVersion=8.3\r\n";
        File.WriteAllText(PathName, noIds, new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        var (next, changed) = PlatformVersions.Assign(library, [library.Bases[0].Id], new("8.3", PlatformArchitecture.X64));
        Assert.Equal(1, changed);
        var saved = store.Save(next);
        Assert.Equal(noIds, File.ReadAllText(PathName));
        Assert.Equal(PlatformArchitecture.X64, saved.Bases[0].PlatformRule!.Architecture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RuleAloneForABaseRemovedThereIsAConflict(bool ids)
    {
        // Another program removed the base while the window was open: a local rule alone does not count as a change of the
        // record, so the base the assignment is about is required to be there, with a persistent ID or a computed one.
        var text = ids ? List : "[Склад]\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nVersion=8.3\r\n[Зарплата]\r\nConnect=Srvr=\"srv\";Ref=\"zup\";\r\nVersion=8.3\r\n";
        File.WriteAllText(PathName, text, new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        var target = library.Bases[0];
        var removed = text[text.IndexOf('[', 1)..];
        File.WriteAllText(PathName, removed, new UTF8Encoding(false));
        var (next, changed) = PlatformVersions.Assign(library, [target.Id], new(target.PlatformVersion, PlatformArchitecture.X64));
        Assert.Equal(1, changed);
        Assert.Throws<ListChangedException>(() => store.Save(next));
        Assert.Equal([target.Name], Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(next, required: [target.Id])).Names);
        Assert.Equal(removed, File.ReadAllText(PathName));
        // Without the requirement the same merge would drop the base silently (what the version command must not do).
        Assert.DoesNotContain(store.SaveOverExternalChange(next).Saved.Bases, x => x.Id == target.Id);
    }

    [Fact] public void VersionChangedWhileTheyChangedTheSameBaseIsAConflict()
    {
        var (store, library) = Open();
        File.WriteAllText(PathName, List.Replace("Custom=a", "Custom=theirs"), new UTF8Encoding(false));
        var theirs = File.ReadAllBytes(PathName);
        var next = PlatformVersions.Assign(library, [Guid.Parse(A)], new("8.3.24", PlatformArchitecture.Any)).Library;
        Assert.Throws<ListChangedException>(() => store.Save(next));
        Assert.Equal(["Бухгалтерия"], Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(next)).Names);
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
    }

    [Fact] public void VersionChangedWhileTheyChangedAnotherBaseGoesOverTheirs()
    {
        var (store, library) = Open();
        File.WriteAllText(PathName, List.Replace("Ref=\"zup\";", "Ref=\"zup2\";"), new UTF8Encoding(false));
        var next = PlatformVersions.Assign(library, [Guid.Parse(A)], new("8.3.24", PlatformArchitecture.Any)).Library;
        var (_, saved) = store.SaveOverExternalChange(next);
        var text = File.ReadAllText(PathName);
        Assert.Contains("Folder=/Работа\r\nVersion=8.3.24\r\nCustom=a", text);
        Assert.Contains("Ref=\"zup2\";", text);
        Assert.Equal("8.3.24", Base(saved, A).PlatformVersion);
        Assert.Equal("8.3.24", Base(saved, A).PlatformRule!.Version);
    }
}
