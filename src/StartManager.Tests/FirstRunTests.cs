using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

[CollectionDefinition("First run list", DisableParallelization = true)]
public sealed class FirstRunListCollection;

// LibraryStore.FirstRunListPath is process-wide; other tests must not observe it.
[Collection("First run list")]
public sealed class FirstRunListTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-first-run-" + Guid.NewGuid().ToString("N"));
    public FirstRunListTests() => Directory.CreateDirectory(folder);
    public void Dispose() { LibraryStore.FirstRunListPath = null; Directory.Delete(folder, true); }

    [Fact] public void NewProfileOpensStandardListAndSharedEntriesLoad()
    {
        var list = Path.Combine(folder, "ibases.v8i");
        File.WriteAllText(list, "[test]\nConnect=Srvr=\"localhost\";Ref=\"test\";\nVersion=8.3\n");
        LibraryStore.FirstRunListPath = list;
        var settings = Path.Combine(folder, "library.json");
        var loaded = new LibraryStore(settings).Load();
        Assert.Equal(list, loaded.ListPath);
        Assert.Equal("test", Assert.Single(new V8iStore(list).Load(LibrarySources.Select(loaded, settings, list)).Bases).Name);
        Assert.False(File.Exists(settings));
    }

    [Fact] public void MissingStandardListKeepsLocalList()
    {
        LibraryStore.FirstRunListPath = Path.Combine(folder, "absent.v8i");
        Assert.Equal("", new LibraryStore(Path.Combine(folder, "library.json")).Load().ListPath);
    }

    [Fact] public void ExistingLocalListWithBasesOrGroupsIsKept()
    {
        var list = Path.Combine(folder, "ibases.v8i"); File.WriteAllText(list, "");
        var settings = Path.Combine(folder, "library.json");
        LibraryStore.FirstRunListPath = list;
        new LibraryStore(settings).Save(new Library { Bases = [new BaseEntry { Name = "Local", Kind = ConnectionKind.Server, Location = "srv\\db" }] });
        Assert.Equal("", new LibraryStore(settings).Load().ListPath);
        File.Delete(settings);
        new LibraryStore(settings).Save(new Library { Groups = ["Empty group"] });
        Assert.Equal("", new LibraryStore(settings).Load().ListPath);
    }

    [Fact] public void EmptyLocalListOpensStandardListUntilSaved()
    {
        var list = Path.Combine(folder, "ibases.v8i"); File.WriteAllText(list, "[test]\nConnect=Srvr=\"localhost\";Ref=\"test\";\n");
        var settings = Path.Combine(folder, "library.json");
        new LibraryStore(settings).Save(new Library());
        var bytes = File.ReadAllBytes(settings);
        LibraryStore.FirstRunListPath = list;
        var loaded = new LibraryStore(settings).Load();
        Assert.Equal(list, loaded.ListPath);
        Assert.Equal(bytes, File.ReadAllBytes(settings));
        // The local list stays reachable, e.g. with --list "".
        Assert.Empty(LibrarySources.Select(loaded, settings, "").Bases);
        LibraryStore.FirstRunListPath = Path.Combine(folder, "absent.v8i");
        Assert.Equal("", new LibraryStore(settings).Load().ListPath);
    }

    [Fact] public void NewProfileCanStillChooseLocalList()
    {
        var list = Path.Combine(folder, "ibases.v8i"); File.WriteAllText(list, "[test]\nConnect=Srvr=\"localhost\";Ref=\"test\";\n");
        LibraryStore.FirstRunListPath = list;
        var settings = Path.Combine(folder, "library.json");
        var local = LibrarySources.Select(new LibraryStore(settings).Load(), settings, "");
        Assert.Equal("", local.ListPath); Assert.Empty(local.Bases);
    }

    [Fact] public void ShortcutWorksOnceFirstRunProfileIsSaved()
    {
        var id = Guid.NewGuid();
        var list = Path.Combine(folder, "ibases.v8i"); File.WriteAllText(list, $"[test]\nID={id}\nConnect=Srvr=\"localhost\";Ref=\"test\";\n");
        LibraryStore.FirstRunListPath = list;
        var settings = Path.Combine(folder, "library.json");
        var store = new LibraryStore(settings);
        var loaded = new V8iStore(list).Load(LibrarySources.Select(store.Load(), settings, list));
        var request = new ShortcutRequest(settings, list, id, false);
        Assert.Throws<FileNotFoundException>(() => request.Resolve());
        store.Save(loaded);
        Assert.Equal("test", request.Resolve().Entry.Name);
        Assert.Equal(list, new LibraryStore(settings).Load().ListPath);
    }

    [Fact] public void StandardPathMatchesLauncherLocation()
    {
        var expected = OperatingSystem.IsWindows() ? Path.Combine("1C", "1CEStart", "ibases.v8i") : Path.Combine(".1C", "1cestart", "ibases.v8i");
        Assert.EndsWith(expected, V8iStore.StandardPath, StringComparison.Ordinal);
    }
}

[CollectionDefinition("1CEStart default version", DisableParallelization = true)]
public sealed class StartDefaultVersionCollection;

// PlatformCatalog.StartDefaultVersion is process-wide; the real 1CEStart.cfg of the machine must not decide the result.
[Collection("1CEStart default version")]
public sealed class ImplicitPlatformTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-implicit-" + Guid.NewGuid().ToString("N"));
    private readonly Func<PlatformRule?> savedDefault = PlatformCatalog.StartDefaultVersion;
    public ImplicitPlatformTests() { Directory.CreateDirectory(folder); PlatformCatalog.StartDefaultVersion = () => null; }
    public void Dispose() { PlatformCatalog.StartDefaultVersion = savedDefault; Directory.Delete(folder, true); }

    [Theory] [InlineData("8.3", "8.3.27.2214")] [InlineData("8.3.27.1989", "8.3.27.1989")] [InlineData(null, "8.5.1.1343")]
    public void RecordWithoutVersionUsesDefaultVersionOf1CEStart(string? configured, string selected)
    {
        PlatformCatalog.StartDefaultVersion = () => configured is null ? null : new PlatformRule { Version = configured };
        Assert.Equal(Installed.Single(x => x.Version == Version.Parse(selected)).Executable, PlatformCatalog.Resolve(new Library(), Entry(""), Installed));
        // The record's own Version still wins.
        Assert.Equal(Installed[2].Executable, PlatformCatalog.Resolve(new Library(), Entry("8.5"), Installed));
    }

    [Fact] public void DefaultVersionIsReadFromUserThenCommonFile()
    {
        string File(string name, string text, System.Text.Encoding encoding)
        { var path = Path.Combine(folder, name); System.IO.File.WriteAllText(path, text, encoding); return path; }
        var broken = File("broken.cfg", "DefaultVersion=8.3.x\r\nAutoUninstall=0\r\n", new System.Text.UTF8Encoding(false));
        var common = File("common.cfg", "InstalledLocation=C:\\1cv8\r\n defaultversion = 8.3.27 \r\n", System.Text.Encoding.Unicode);
        var user = File("user.cfg", "\uFEFFDefaultVersion=8.5\n", new System.Text.UTF8Encoding(false));
        Assert.Equal("8.5", PlatformCatalog.ReadDefaultVersion([Path.Combine(folder, "missing.cfg"), user, common])?.Version);
        Assert.Equal("8.3.27", PlatformCatalog.ReadDefaultVersion([broken, common])?.Version);
        Assert.Null(PlatformCatalog.ReadDefaultVersion([broken, folder]));
        Assert.Null(PlatformCatalog.ReadDefaultVersion([]));
        Assert.All(PlatformCatalog.StartConfigFiles(), x => Assert.EndsWith("1cestart.cfg", x, StringComparison.OrdinalIgnoreCase));
    }

    // Documented form: DefaultVersion=<version>[;<bitness>]; bitness is a preference, as AppArch of the list.
    [Theory]
    [InlineData("8.3;x86_64_prt", "8.3", PlatformArchitecture.PreferX64)] [InlineData("8.3; x86_64", "8.3", PlatformArchitecture.PreferX64)]
    [InlineData("8.3.27;x86_prt", "8.3.27", PlatformArchitecture.PreferX86)] [InlineData("8.3;X86", "8.3", PlatformArchitecture.PreferX86)]
    [InlineData("8.3;", "8.3", PlatformArchitecture.Any)] [InlineData("8.3;arm", "8.3", PlatformArchitecture.Any)]
    public void DefaultVersionWithBitnessKeepsTheVersion(string value, string version, PlatformArchitecture architecture)
    {
        var path = Path.Combine(folder, "cfg.cfg"); File.WriteAllText(path, "DefaultVersion=" + value + "\r\n");
        var rule = PlatformCatalog.ReadDefaultVersion([path])!;
        Assert.Equal((version, architecture), (rule.Version, rule.Architecture));
        PlatformCatalog.StartDefaultVersion = () => rule;
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), Entry(""), Installed));
        var both = new[] { new PlatformInstallation("x86-2214", new Version(8, 3, 27, 2214), PlatformArchitecture.X86), Installed[1] };
        if (OperatingSystem.IsWindows() && Native == PlatformArchitecture.X64 && architecture != PlatformArchitecture.Any)
            Assert.Equal(architecture == PlatformArchitecture.PreferX64 ? Installed[1].Executable : "x86-2214", PlatformCatalog.Resolve(new Library(), Entry(""), both));
    }
    [Theory]
    [InlineData("AppArch=x86_64\n", PlatformArchitecture.PreferX64)] [InlineData("AppArch=x86\n", PlatformArchitecture.PreferX86)]
    [InlineData("AppArch=X86_64_prt\n", PlatformArchitecture.PreferX64)] [InlineData("AppArch=arm\n", PlatformArchitecture.Any)] [InlineData("", PlatformArchitecture.Any)]
    public void AppArchOfTheListIsReadAsAPreference(string line, PlatformArchitecture expected)
    {
        var entry = Assert.Single(V8iImporter.Import(new Library(), "[Base]\nConnect=Srvr=\"host\";Ref=\"base\";\nVersion=8.3\n" + line).Library.Bases);
        Assert.Equal(expected, entry.ListArchitecture);
    }

    // Windows x64 with both bitnesses of one build, the usual 1C installation: AppArch decides, a newer build still wins.
    [Fact]
    public void AppArchChoosesTheBitnessOfTheSameBuildButNotAStrictOrOwnChoice()
    {
        if (!OperatingSystem.IsWindows() || Native != PlatformArchitecture.X64) return;
        var x86 = new PlatformInstallation("x86-2214", new Version(8, 3, 27, 2214), PlatformArchitecture.X86);
        var both = new[] { x86, Installed[1] };
        var x64Base = Entry("8.3") with { ListArchitecture = PlatformArchitecture.PreferX64 };
        var x86Base = Entry("8.3") with { ListArchitecture = PlatformArchitecture.PreferX86 };
        Assert.Equal(x86.Executable, PlatformCatalog.Resolve(new Library(), Entry("8.3"), both));
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), x64Base, both));
        Assert.Equal(x86.Executable, PlatformCatalog.Resolve(new Library(), x86Base, both));
        // A preference of 1CEStart or of the common rule is refined by the record; a strict bitness is not.
        PlatformCatalog.StartDefaultVersion = () => new PlatformRule { Version = "8.3", Architecture = PlatformArchitecture.PreferX86 };
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), x64Base with { PlatformVersion = "" }, both));
        var common = new Library { PlatformRule = new() { Version = "8.3", Architecture = PlatformArchitecture.PreferX86 } };
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(common, x64Base, both));
        var strict = new Library { PlatformRule = new() { Version = "8.3", Architecture = PlatformArchitecture.X86 } };
        Assert.Equal(x86.Executable, PlatformCatalog.Resolve(strict, x64Base, both));
        var own = x64Base with { PlatformRule = new() { Version = "8.3", Architecture = PlatformArchitecture.PreferX86 } };
        Assert.Equal(x86.Executable, PlatformCatalog.Resolve(new Library(), own, both));
        var ownAuto = x64Base with { PlatformRule = new() { Version = "8.3" } };
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), ownAuto, both));
        // A preference only: without an x64 build the base still starts.
        Assert.Equal(x86.Executable, PlatformCatalog.Resolve(new Library(), x64Base, [x86]));
        var newer = new PlatformInstallation("x86-2300", new Version(8, 3, 27, 2300), PlatformArchitecture.X86);
        Assert.Equal(newer.Executable, PlatformCatalog.Resolve(new Library(), x64Base, [newer, Installed[1]]));
    }

    [Fact] public void InstalledLocationsOf1CEStartAreSearchRoots()
    {
        var root = Path.Combine(folder, "Program Files", "1cv8");
        var client = Path.Combine(root, "8.3.27.2214", "bin", OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
        Directory.CreateDirectory(Path.GetDirectoryName(client)!); File.WriteAllText(client, "fixture");
        var common = Path.Combine(folder, "common.cfg");
        File.WriteAllText(common, $"InstalledLocation={root}\r\nInstalledLocation=relative\\1cv8\r\n installedlocation = {Path.Combine(folder, "missing")} \r\n",
            System.Text.Encoding.Unicode);
        var user = Path.Combine(folder, "user.cfg"); File.WriteAllText(user, "DefaultVersion=8.3\n");
        var roots = PlatformCatalog.InstalledLocations([Path.Combine(folder, "absent.cfg"), user, common]).ToArray();
        Assert.Equal(new[] { root, Path.Combine(folder, "missing") }, roots);
        var scan = PlatformCatalog.Scan(roots);
        Assert.Equal(new Version(8, 3, 27, 2214), Assert.Single(scan.Installations).Version);
        Assert.Empty(scan.Warnings);
    }

    private static readonly PlatformArchitecture Native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
        System.Runtime.InteropServices.Architecture.Arm64 ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
    private static BaseEntry Entry(string version) => new() { Name = "Test", Kind = ConnectionKind.Server, Location = "server\\base", PlatformVersion = version };
    private static string Client(string version, bool thin) => Path.Combine("opt", version, OperatingSystem.IsWindows() ? (thin ? "1cv8c.exe" : "1cv8.exe") : (thin ? "1cv8c" : "1cv8"));
    private static PlatformInstallation Item(string version, bool thin = false) => new(Client(version, thin), Version.Parse(version), Native);
    private static readonly PlatformInstallation[] Installed = [Item("8.3.27.1989", thin: true), Item("8.3.27.2214"), Item("8.5.1.1343")];

    [Theory]
    [InlineData("8.3.27.1989", "8.3.27.1989")] [InlineData("8.3.27.2214", "8.3.27.2214")]
    [InlineData("8.3", "8.3.27.2214")] [InlineData("", "8.5.1.1343")]
    public void EmptyProfileUsesListVersionLike1CEStart(string requested, string selected) =>
        Assert.Equal(Installed.Single(x => x.Version == Version.Parse(selected)).Executable, PlatformCatalog.Resolve(new Library(), Entry(requested), Installed));

    // User report 24.09.2026: Version=8.3, DefaultVersion of a removed build, AppArch=x86 and only x64 installed.
    [Theory]
    [InlineData("Version=8.3\nDefaultVersion=8.3.12.1790\nAppArch=x86\n", "8.3")]
    [InlineData("DefaultVersion=8.3.27.1989\n", "8.3.27.1989")]
    [InlineData("Version=bad\nDefaultVersion=8.3.27\n", "8.3.27")]
    [InlineData("Version=8.3.27.1989\nDefaultVersion=8.3.12.1790\n", "8.3.27.1989")]
    public void ListVersionRuleWinsOverServiceDefaultVersion(string lines, string expected)
    {
        var entry = Assert.Single(V8iImporter.Import(new Library(), "[Base]\nConnect=Srvr=\"host\";Ref=\"base\";\n" + lines).Library.Bases);
        Assert.Equal(expected, entry.PlatformVersion);
        if (expected == "8.3") Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), entry, Installed));
    }

    [Fact] public void ExplicitDefaultsStillWin()
    {
        Assert.Equal("default", PlatformCatalog.Resolve(new Library { Executable = "default" }, Entry("8.3.27.1989"), Installed));
        // The general rule chooses for a record without a full release only (decision of the user 12.11.2026).
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library { PlatformRule = new() { Version = "8.3.27.2214" } }, Entry("8.3"), Installed));
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library { PlatformRule = new() { Version = "8.3.27.2214" } }, Entry(""), Installed));
    }

    // A thin client of the newest 8.3 did not connect to an older server: the base listed Version=8.3.27.1989, the general rule 8.3
    // chose 8.3.27.2214 and /AppAutoCheckVersion- kept 1C from choosing the server's release (Linux, 12.11.2026).
    [Theory] [InlineData("8.3")] [InlineData("8.3.27.2214")] [InlineData("8.3.27")]
    public void GeneralRuleDoesNotReplaceAFullReleaseOfTheRecord(string general)
    {
        var library = new Library { PlatformRule = new() { Version = general } };
        Assert.Equal(Installed[0].Executable, PlatformCatalog.Resolve(library, Entry("8.3.27.1989"), Installed));
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(library, Entry("8.3.27.2214"), Installed));
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 1989), Native);
        Assert.DoesNotContain("/AppAutoCheckVersion-", Launcher.Build(library, Entry("8.3.27.1989"), false, [installation]).ArgumentList);
    }

    // With a general rule the settings keep the disabled path of «Ручной клиент»: it must not start a record with a full release,
    // even when it is missing or of another branch (Codex audit 4, F1).
    [Theory] [InlineData("opt/8.5.1.1343/1cv8")] [InlineData("/missing/old/1cv8")]
    public void AKeptPathOfTheManualModeDoesNotStartWithAGeneralRule(string kept)
    {
        var library = new Library { Executable = kept, PlatformRule = new() { Version = "8.3" } };
        Assert.Equal(Installed[0].Executable, PlatformCatalog.Resolve(library, Entry("8.3.27.1989"), Installed));
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(library, Entry("8.3"), Installed));
        // The manual mode itself (no general rule) still wins over Version= of the record.
        Assert.Equal(kept, PlatformCatalog.Resolve(library with { PlatformRule = null }, Entry("8.3.27.1989"), Installed));
    }

    // A check of the version written in the parameters is kept as the only one; a value of another key is not one (Codex audit 4, F3).
    [Theory] [InlineData("/AppAutoCheckVersion+", 1)] [InlineData("/AppAutoCheckVersion-", 1)] [InlineData("/AppAutoCheckVersion", 1)]
    [InlineData("/C /AppAutoCheckVersion+", 2)]
    public void TheVersionCheckOfTheParametersIsNotRepeated(string parameters, int expected)
    {
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 2214), Native);
        var library = new Library { PlatformRule = new() { Version = "8.3.27.2214" } };
        var args = Launcher.Build(library, Entry("8.3") with { AdditionalParameters = parameters }, false, [installation]).ArgumentList;
        Assert.Equal(expected, args.Count(x => x.StartsWith("/AppAutoCheckVersion", StringComparison.OrdinalIgnoreCase)));
        if (expected == 2) Assert.Equal("/AppAutoCheckVersion-", args[^1]);
    }

    // A client chosen by hand shows its own release, not Version= of the record it overrides (Codex audit 4, F4).
    [Fact] public void TheColumnOfAManualClientShowsItsRelease()
    {
        var manual = PlatformCatalog.Resolve(new Library { Executable = Environment.ProcessPath! }, Entry("8.3.27.1989"), Installed);
        Assert.Equal(Environment.ProcessPath, manual);
        string Stub(params string[] parts)
        {
            var path = Path.Combine([folder, .. parts, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8"]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path;
        }
        var versioned = BaseInfo.Read(Entry("8.3.27.1989"), new Library { Executable = Stub("8.5.1.100", "bin") }, Installed);
        Assert.Equal("8.5.1.100", versioned.Platform); Assert.Equal("8.5.1.100", versioned.Release);
        // Outside a version folder (a text stub has no FileVersionInfo) the release is unknown, not «Version= *» (Grok audit, P3).
        var odd = Stub("odd");
        var unknown = BaseInfo.Read(Entry("8.3.27.1989"), new Library { Executable = odd }, Installed);
        Assert.Equal("—", unknown.Platform); Assert.Equal("", unknown.Release);
        var own = BaseInfo.Read(Entry("8.3.27.1989") with { Executable = odd }, new Library(), Installed);
        Assert.Equal("—", own.Platform);
        // The scan knows the release of that file.
        var found = BaseInfo.Read(Entry("8.3.27.1989"), new Library { Executable = odd }, [new PlatformInstallation(odd, new(8, 5, 1, 200), Native)]);
        Assert.Equal("8.5.1.200", found.Platform); Assert.Equal("8.5.1.200", found.Release);
        Assert.Equal("8.3.27.1989", BaseInfo.Read(Entry("8.3.27.1989"), new Library(), Installed).Platform);
    }

    [Theory] [InlineData("8.3", false)] [InlineData("8.3.27", false)] [InlineData("8.3.27.2214", true)]
    public void OnlyARuleOfOneBuildKeepsTheClientFrom1CVersionCheck(string version, bool pinned)
    {
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 2214), Native);
        var general = Launcher.Build(new Library { PlatformRule = new() { Version = version } }, Entry("8.3"), false, [installation]);
        var own = Launcher.Build(new Library(), Entry("8.3") with { PlatformRule = new() { Version = version } }, true, [installation]);
        Assert.Equal(pinned, general.ArgumentList.Contains("/AppAutoCheckVersion-"));
        Assert.Equal(pinned, own.ArgumentList.Contains("/AppAutoCheckVersion-"));
    }

    [Fact] public void ThinOnlyVersionStartsEnterpriseButNotDesigner()
    {
        Assert.True(Installed[0].Thin);
        Assert.Contains("тонкий клиент", Installed[0].ToString());
        var error = Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Resolve(new Library(), Entry("8.3.27.1989"), Installed, designer: true));
        Assert.Contains("толстый клиент 1cv8", error.Message);
        Assert.Equal(Installed[1].Executable, PlatformCatalog.Resolve(new Library(), Entry("8.3"), [Installed[0], Installed[1]], designer: true));
    }

    [Fact] public void SameVersionPrefersThickClientForEnterprise()
    {
        var both = new[] { Item("8.3.27.2214", thin: true), Item("8.3.27.2214") };
        Assert.Equal(both[1].Executable, PlatformCatalog.Resolve(new Library(), Entry("8.3.27.2214"), both));
    }

    [Fact] public void ImplicitLaunchKeepsClientVersionCheck()
    {
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 2214), Native);
        var start = Launcher.Build(new Library(), Entry("8.3"), false, [installation]);
        Assert.Equal(Environment.ProcessPath, start.FileName);
        Assert.DoesNotContain("/AppAutoCheckVersion-", start.ArgumentList);
    }

    [Fact] public void DisplayShowsInstalledAndMissingVersions()
    {
        var installation = new PlatformInstallation(Environment.ProcessPath!, new Version(8, 3, 27, 2214), Native);
        var found = BaseInfo.Read(Entry("8.3.27.2214"), new Library(), [installation]);
        Assert.False(found.PlatformMissing); Assert.Equal("8.3.27.2214", found.Platform);
        var missing = BaseInfo.Read(Entry("8.3.27.9999"), new Library(), [installation]);
        Assert.True(missing.PlatformMissing); Assert.Equal("8.3.27.9999", missing.Platform);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void ManualThinClientIsRefusedForDesigner(bool perBase)
    {
        var thin = Path.Combine(folder, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "fixture");
        var library = perBase ? new Library() : new Library { Executable = thin };
        var entry = perBase ? Entry("8.3") with { Executable = thin } : Entry("8.3");
        Assert.Equal(thin, Launcher.Build(library, entry, false, []).FileName);
        Assert.Contains("толстый клиент 1cv8", Assert.Throws<ArgumentException>(() => Launcher.Build(library, entry, true, [])).Message);
    }

    [Fact] public void ScanFindsThinOnlyDistributionAndPrefersThickInSameFolder()
    {
        string Create(string version, string name)
        {
            var path = Path.Combine(folder, "x86_64", version, OperatingSystem.IsWindows() ? "bin" : "", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path;
        }
        var thin = Create("8.3.27.1989", "1cv8c");
        Create("8.3.27.2214", "1cv8c"); var thick = Create("8.3.27.2214", "1cv8");
        var scan = PlatformCatalog.Scan([folder]);
        Assert.Empty(scan.Warnings);
        Assert.Equal(new[] { Path.GetFullPath(thick), Path.GetFullPath(thin) }, scan.Installations.Select(x => x.Executable));
        Assert.True(scan.Installations[1].Thin); Assert.False(scan.Installations[0].Thin);
    }
}
