using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Список установленных версий» (кнопка «8.x»): the tree of families, builds and clients, the registry entry of a build and
/// the command that removes it.</summary>
public sealed class InstalledVersionsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bn-installed-" + Guid.NewGuid().ToString("N"));
    public InstalledVersionsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private const string Folder = @"C:\Program Files\1cv8\8.3.25.1257\bin";
    private static readonly Version Build825 = Version.Parse("8.3.25.1257");

    private static PlatformUninstallEntry Entry(string version, string location, PlatformArchitecture view = PlatformArchitecture.X64,
        string command = "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", string? name = null) =>
        new(name ?? "1С:Предприятие 8 (" + version + ")", version, location, @"\\srv\dist\" + version, InstalledVersions.ParseDate("20260915"), command, view);

    private string Install(string version, string bits, params string[] clients)
    {
        var bin = Path.Combine(directory, bits, version, "bin");
        Directory.CreateDirectory(bin);
        foreach (var client in clients) File.WriteAllText(Path.Combine(bin, client), "");
        return bin;
    }
    private static PlatformInstallation Client(string folder, string file, string version, PlatformArchitecture architecture) =>
        new(Path.Combine(folder, file), Version.Parse(version), architecture);

    [Fact] public void TreeHasFamiliesNewestFirstBuildsWithBitnessAndTheirClients()
    {
        var a = Install("8.3.25.1257", "x64", "1cv8.exe", "1cv8c.exe");
        var b = Install("8.3.25.1257", "x86", "1cv8c.exe");
        var c = Install("8.3.24.1342", "x64", "1cv8.exe");
        var d = Install("8.2.19.130", "x86", "1cv8.exe");
        var tree = InstalledVersions.Build(
        [
            Client(d, "1cv8.exe", "8.2.19.130", PlatformArchitecture.X86), Client(c, "1cv8.exe", "8.3.24.1342", PlatformArchitecture.X64),
            Client(a, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64), Client(a, "1cv8c.exe", "8.3.25.1257", PlatformArchitecture.X64),
            Client(b, "1cv8c.exe", "8.3.25.1257", PlatformArchitecture.X86)
        ], [], windows: true, PlatformArchitecture.X64);

        Assert.Equal(["8.3", "8.2"], tree.Select(x => x.Text));
        Assert.Equal(["8.3.25.1257 (x86)", "8.3.25.1257 (x64)", "8.3.24.1342 (x64)"], tree[0].Children.Select(x => x.Text));
        // One build per folder: the thick and thin clients of a folder are its files.
        var x64 = tree[0].Children[1];
        Assert.Equal(a, x64.Path);
        Assert.Equal(["1cv8.exe", "1cv8c.exe"], x64.Children.Select(x => x.Text));
        Assert.Equal(Path.Combine(a, "1cv8c.exe"), x64.Children[1].Path);
        Assert.All(tree.SelectMany(x => x.Children), x => Assert.Null(x.Uninstall));
    }

    [Fact] public void BuildTakesTheEntryInstalledIntoItsFolderOrTheFolderAboveBin()
    {
        var a = Install("8.3.25.1257", "x64", "1cv8.exe");
        var b = Install("8.3.24.1342", "x64", "1cv8.exe");
        var own = Entry("8.3.25.1257", Path.GetDirectoryName(a) + "\\");
        var other = Entry("8.3.24.1342", "\"" + b + "\"");
        var tree = InstalledVersions.Build(
            [Client(a, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64), Client(b, "1cv8.exe", "8.3.24.1342", PlatformArchitecture.X64)],
            [other, own], windows: true, PlatformArchitecture.X64);

        Assert.Same(own, tree[0].Children[0].Uninstall);
        Assert.Same(other, tree[0].Children[1].Uninstall);
        Assert.Equal(new DateTime(2026, 9, 15), tree[0].Children[0].Uninstall!.InstallDate);
    }

    [Fact] public void AnotherBuildIsNeverRemovedInsteadOfThisOne()
    {
        // An entry of a different folder with the same version is not this build.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", @"D:\1cv8\8.3.25.1257\")], true));
        // Two platform entries for one folder: neither is chosen.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64,
            [Entry("8.3.25.1257", @"C:\Program Files\1cv8\8.3.25.1257\"), Entry("8.3.25.1257", @"c:\program files\1cv8\8.3.25.1257")], true));
        // A prefix of the folder is not its location.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", @"C:\Program Files\1cv8\")], true));
        // Linux paths differ by case.
        Assert.Null(InstalledVersions.Match("/opt/1cv8/x86_64/8.3.25.1257", Build825, PlatformArchitecture.X64,
            [Entry("8.3.25.1257", "/opt/1CV8/x86_64/8.3.25.1257")], false));
    }

    [Fact] public void ACopiedBuildDoesNotTakeTheUninstallerOfTheInstalledOne()
    {
        var installed = Install("8.3.25.1257", "installed", "1cv8.exe");
        var copied = Install("8.3.25.1257", "copied", "1cv8.exe");
        PlatformInstallation[] both =
            [Client(installed, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64), Client(copied, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64)];
        // Without a location the entry fits both folders: it belongs to neither.
        var tree = InstalledVersions.Build(both, [Entry("8.3.25.1257", "")], windows: true, PlatformArchitecture.X64);
        Assert.Equal(2, tree[0].Children.Count);
        Assert.All(tree[0].Children, x => Assert.Null(x.Uninstall));
        // With its location the installed build keeps its entry; the copy still gets none.
        var located = Entry("8.3.25.1257", Path.GetDirectoryName(installed)!);
        tree = InstalledVersions.Build(both, [located], windows: true, PlatformArchitecture.X64);
        Assert.Same(located, tree[0].Children.Single(x => x.Path == installed).Uninstall);
        Assert.Null(tree[0].Children.Single(x => x.Path == copied).Uninstall);
    }

    [Fact] public void ACopyDoesNotTakeALocationlessEntryWhileTheInstalledBuildHasItsOwn()
    {
        var installed = Install("8.3.25.1257", "installed", "1cv8.exe");
        var copied = Install("8.3.25.1257", "copied", "1cv8.exe");
        var located = Entry("8.3.25.1257", Path.GetDirectoryName(installed)!);
        var locationless = Entry("8.3.25.1257", "");
        var tree = InstalledVersions.Build(
            [Client(installed, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64), Client(copied, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64)],
            [located, locationless], windows: true, PlatformArchitecture.X64);
        Assert.Same(located, tree[0].Children.Single(x => x.Path == installed).Uninstall);
        // The locationless entry may be another registration of the installed build: the copy is not removed with it.
        Assert.Null(tree[0].Children.Single(x => x.Path == copied).Uninstall);
        // Another bitness of the same version is not a twin.
        var x86 = Install("8.3.25.1257", "x86", "1cv8.exe");
        var own = Entry("8.3.25.1257", "", PlatformArchitecture.X86);
        tree = InstalledVersions.Build(
            [Client(installed, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X64), Client(x86, "1cv8.exe", "8.3.25.1257", PlatformArchitecture.X86)],
            [located, own], windows: true, PlatformArchitecture.X64);
        Assert.Same(own, tree[0].Children.Single(x => x.Path == x86).Uninstall);
    }

    [Fact] public void ALocatedEntryContradictingTheBuildIsNotTaken()
    {
        const string location = @"C:\Program Files\1cv8\8.3.25.1257";
        PlatformUninstallEntry Named(string name, string shown = "", PlatformArchitecture view = PlatformArchitecture.Any) =>
            new(name, shown, location, "", null, "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", view);
        // Another build or family in the name, or a registry view of the other bitness.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Named("1С:Предприятие 8 (x86-64) (8.3.24.1342)")], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Named("1С:Предприятие 8.2")], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Named("1С:Предприятие 8 (8.3.25.1257)", view: PlatformArchitecture.X86)], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Named("1С:Предприятие 8 (8.3.25.1257)", "8.3.24.1342")], true));
        // Consistent, without DisplayVersion and with an unknown view: still this build's.
        var consistent = Named("1С:Предприятие 8 (x86-64) (8.3.25.1257)");
        Assert.Same(consistent, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [consistent], true));
        var family = Named("1С:Предприятие 8.3");
        Assert.Same(family, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [family], true));
    }

    [Fact] public void WithoutALocationTheVersionAndRegistryViewDecide()
    {
        var x86 = Entry("8.3.25.1257", "", PlatformArchitecture.X86);
        var x64 = Entry("8.3.25.1257", "");
        Assert.Same(x86, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X86, [x64, x86], true));
        Assert.Same(x64, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [x64, x86], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X86, [Entry("8.3.25.1258", "", PlatformArchitecture.X86)], true));
        // A located entry of another folder is not taken by its version.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X86, [Entry("8.3.25.1257", @"D:\1cv8", PlatformArchitecture.X86)], true));
        // A locationless entry without a version proves nothing.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("", "")], true));
    }

    [Fact] public void AnEntryOfUnknownBitnessIsNotTakenWithoutALocation()
    {
        // The per-user key and the 64-bit view of ARM64 Windows do not prove the bitness.
        var unknown = Entry("8.3.25.1257", "", PlatformArchitecture.Any);
        foreach (var architecture in new[] { PlatformArchitecture.X86, PlatformArchitecture.X64, PlatformArchitecture.Arm64 })
            Assert.Null(InstalledVersions.Match(Folder, Build825, architecture, [unknown], true));
        // An x86 entry of the 32-bit view is not an x64 build, nor an x64 one an x86 build.
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", "", PlatformArchitecture.X86)], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X86, [Entry("8.3.25.1257", "", PlatformArchitecture.X64)], true));
        // A location decides whatever the view.
        var located = Entry("8.3.25.1257", @"C:\Program Files\1cv8\8.3.25.1257", PlatformArchitecture.Any);
        Assert.Same(located, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.Arm64, [located], true));
    }

    [Theory]
    [InlineData("1C:Other product")]
    [InlineData("1Click Tools")]
    [InlineData("1С:Предприятие 8. Сервер хранилища")]
    [InlineData("1С:Бухгалтерия 8")]
    [InlineData("1С:Предприятие 7.7")]
    public void AnotherProductIsNotThePlatform(string name)
    {
        const string location = @"C:\Program Files\1cv8\8.3.25.1257";
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", location, name: name)], true));
        Assert.Null(InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", "", name: name)], true));
        // Beside the platform in the same folder another product is ignored, not ambiguous.
        var platform = Entry("8.3.25.1257", location);
        Assert.Same(platform, InstalledVersions.Match(Folder, Build825, PlatformArchitecture.X64, [Entry("8.3.25.1257", location, name: name), platform], true));
    }

    [Theory]
    [InlineData("1С:Предприятие 8 (x86-64) (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X64, true)]
    [InlineData("1C:Enterprise 8 (x86-64) (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X64, true)]
    [InlineData("1С:Предприятие 8 Тонкий клиент (x86-64) (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X64, true)]
    [InlineData("1С:Предприятие 8 (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X86, true)]
    [InlineData("1С:Предприятие 8.2", "", "8.2.19.130", PlatformArchitecture.X86, true)]
    // The installed name of the official 8.5 x64 thin client MSI (ProductNameLocal): the client after the bitness.
    [InlineData("1C:Enterprise 8 (x86-64) Thin client", "8.5.1.1343", "8.5.1.1343", PlatformArchitecture.X64, true)]
    [InlineData("1C:Enterprise 8 (x86-64) Thin client (8.5.1.1343)", "8.5.1.1343", "8.5.1.1343", PlatformArchitecture.X64, true)]
    [InlineData("1С:Предприятие 8 (x86-64) Тонкий клиент (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X64, true)]
    [InlineData("1С:Предприятие 8 (8.3.25)", "", "8.3.25.1257", PlatformArchitecture.X86, true)]
    [InlineData("1С:Предприятие 8 (8.3.24)", "", "8.3.25.1257", PlatformArchitecture.X86, false)]
    [InlineData("1С:Предприятие 8.2", "", "8.3.25.1257", PlatformArchitecture.X86, false)]
    // The name or the version contradicts the build.
    [InlineData("1С:Предприятие 8 (x86-64) (8.3.25.1257)", "8.3.25.1257", "8.3.25.1257", PlatformArchitecture.X86, false)]
    [InlineData("1С:Предприятие 8 (8.3.25.1257)", "8.3.25.1257", "8.3.24.1342", PlatformArchitecture.X86, false)]
    [InlineData("1С:Предприятие 8 (8.3.25.1257)", "unknown", "8.3.25.1257", PlatformArchitecture.X86, false)]
    public void PlatformEntriesAreKnownByNameVersionAndBitness(string name, string shown, string build, PlatformArchitecture architecture, bool platform) =>
        Assert.Equal(platform, InstalledVersions.IsPlatform(new(name, shown, "", "", null, "x", PlatformArchitecture.Any), Version.Parse(build), architecture));

    [Theory]
    [InlineData("MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", "msiexec.exe", "/x {11111111-2222-3333-4444-555555555555}")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "msiexec.exe", "/x {11111111-2222-3333-4444-555555555555}")]
    [InlineData("msiexec /x {11111111-2222-3333-4444-555555555555}", "msiexec.exe", "/x {11111111-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /i {11111111-2222-3333-4444-555555555555}", "msiexec.exe", "/x {11111111-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\Program Files\\1cv8\\uninst.exe\" /S", "C:\\Program Files\\1cv8\\uninst.exe", "/S")]
    [InlineData("C:\\Program Files\\1cv8\\uninst.exe /S /D=x", "C:\\Program Files\\1cv8\\uninst.exe", "/S /D=x")]
    [InlineData("C:\\tools\\remove.cmd", "C:\\tools\\remove.cmd", "")]
    [InlineData("C:\\tools\\remove.cmd /S", "C:\\tools\\remove.cmd", "/S")]
    // A folder named like a program is not the program; a program merely ending in «msiexec» is not Windows Installer.
    [InlineData("C:\\tools.exe\\uninstall.exe /S", "C:\\tools.exe\\uninstall.exe", "/S")]
    [InlineData("C:\\vendor\\notmsiexec.exe /I{11111111-2222-3333-4444-555555555555}", "C:\\vendor\\notmsiexec.exe", "/I{11111111-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\vendor\\notmsiexec.exe\" /X{11111111-2222-3333-4444-555555555555}", "C:\\vendor\\notmsiexec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    public void UninstallCommandRemovesAnMsiProductAndRunsAnyOtherAsRegistered(string command, string file, string arguments) =>
        Assert.Equal((file, arguments), InstalledVersions.UninstallCommand(command));

    [Fact] public void UninstallCommandOfAnMsiWithMoreArgumentsIsRunAsRegistered()
    {
        // Only the plain maintenance command is turned into a removal; anything else keeps the publisher's arguments.
        Assert.Equal(("MsiExec.exe", "/I{11111111-2222-3333-4444-555555555555} REBOOT=1"),
            InstalledVersions.UninstallCommand("MsiExec.exe /I{11111111-2222-3333-4444-555555555555} REBOOT=1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\broken\\uninst.exe /S")]
    [InlineData("\"C:\\a\\uninst.exe\"/S")]
    [InlineData("C:\\tools\\remove now /S")]
    // Two words could end the program: a folder «tools.exe backup», a script with an .exe argument.
    [InlineData("C:\\tools.exe backup\\uninstall.exe /S")]
    [InlineData("C:\\tools\\remove.cmd /c C:\\helper.exe /S")]
    public void UninstallCommandRefusesAnEmptyBrokenOrAmbiguousCommand(string command) =>
        Assert.Throws<ArgumentException>(() => InstalledVersions.UninstallCommand(command));

    [Theory]
    [InlineData("20260915", 2026, 9, 15)]
    [InlineData(" 20240101 ", 2024, 1, 1)]
    public void InstallDateIsReadAsTheRegistryWritesIt(string value, int year, int month, int day) =>
        Assert.Equal(new DateTime(year, month, day), InstalledVersions.ParseDate(value));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("15.09.2026")]
    [InlineData("20261315")]
    public void AnUnknownInstallDateIsEmpty(string? value) => Assert.Null(InstalledVersions.ParseDate(value));

    [Fact] public void RegistryEntriesOfThisMachineAreOnlyThePlatform()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The machine may have none; every one read is named as the platform and has a view.
        Assert.All(InstalledVersions.ReadRegistry(), x => Assert.Matches(@"^1[CС]:\s*(Предприятие|Enterprise)\s+8", x.DisplayName));
    }
}
