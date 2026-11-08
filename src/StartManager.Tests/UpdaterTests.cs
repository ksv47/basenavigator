using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Updater.ChangedInThisProcess is process-wide; these tests reset it and must not run in parallel with each other.
[Collection("First run list")]
public sealed class UpdaterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-update-" + Guid.NewGuid().ToString("N"));
    private readonly string app, releases;
    private static readonly string Exe = OperatingSystem.IsWindows() ? "BaseNavigator.exe" : "BaseNavigator";
    private static readonly string Runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    private static readonly Version Current = new(0, 2, 0, 20), Next = new(0, 2, 0, 21);

    private sealed class FolderSource(string folder, ReleaseInfo release) : IReleaseSource
    {
        public ReleaseInfo Latest() => release;
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory)
        {
            foreach (var asset in assets) File.Copy(Path.Combine(folder, asset), Path.Combine(directory, asset));
        }
    }

    public UpdaterTests()
    {
        Updater.ChangedInThisProcess = false; Updater.HelperFailure = null;
        app = Path.Combine(folder, "app"); releases = Path.Combine(folder, "releases");
        Directory.CreateDirectory(Path.Combine(app, "Settings")); Directory.CreateDirectory(Path.Combine(app, "Assets")); Directory.CreateDirectory(releases);
        File.WriteAllText(Path.Combine(app, Exe), "old program");
        File.WriteAllText(Path.Combine(app, "Assets", "basenavigator.png"), "old icon");
        File.WriteAllText(Path.Combine(app, "Settings", "library.json"), "{\"keep\":1}");
        File.WriteAllText(Path.Combine(app, "BaseNavigator.portable"), "");
    }
    // Set by an incomplete cleanup: an operation may still use the folder, so it is kept (its path is in the failure).
    private bool keepFolder;
    public void Dispose()
    {
        Updater.ChangedInThisProcess = false; Updater.HelperFailure = null;
        if (!keepFolder) Directory.Delete(folder, true);
    }

    /// <summary>The next operation as if made after a restart.</summary>
    private static void Restart() => Updater.ChangedInThisProcess = false;

    private static ReleaseInfo Release => new(Next, "v" + Next, "notes");

    /// <param name="bare">As given; otherwise the files every release carries (RequiredFiles) are added when missing.</param>
    private string Package(Dictionary<string, string> entries, bool light = true, string? sum = null, Version? version = null, bool bare = false)
    {
        if (!bare && entries.ContainsKey("BaseNavigator/" + Exe))
        {
            entries = new(entries);
            entries.TryAdd("BaseNavigator/Assets/basenavigator.png", "icon");
            entries.TryAdd("BaseNavigator/THIRD-PARTY-NOTICES.txt", "notices");
            if (OperatingSystem.IsWindows()) entries.TryAdd("BaseNavigator/Start-with-log.cmd", "script");
        }
        var name = Updater.AssetName(version ?? Next, Runtime, light);
        var path = Path.Combine(releases, name);
        if (File.Exists(path)) File.Delete(path);
        if (name.EndsWith(".zip"))
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entry, text) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write(text);
        }
        else
        {
            using var stream = new GZipStream(File.Create(path), CompressionLevel.Fastest);
            using var tar = new TarWriter(stream, TarEntryFormat.Pax);
            foreach (var (entry, text) in entries)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) });
        }
        using (var file = File.OpenRead(path))
            File.WriteAllText(Path.Combine(releases, "SHA256SUMS.txt"), $"{sum ?? Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()}  {name}\n");
        return name;
    }

    private Dictionary<string, string> Good => new() { ["BaseNavigator/" + Exe] = "new program", ["BaseNavigator/Assets/basenavigator.png"] = "new icon" };

    private const string ByContent = "by content";

    /// <param name="probe">What a trial run reports for any file; by default the version the fixture file stands for.</param>
    private Updater Create(string? probe = ByContent, string? directory = null, int? keep = 3, bool? light = true) =>
        new(directory ?? app, Path.Combine(directory ?? app, Exe), new FolderSource(releases, Release), probe == ByContent ? FakeProbe : _ => probe)
        { Keep = keep, Light = light };

    /// <summary>A trial run of a fixture «program»: its text names its version; anything else does not start.</summary>
    private static string? FakeProbe(string candidate)
    {
        var text = File.Exists(candidate) ? File.ReadAllText(candidate) : "";
        return text switch { "old program" => "0.2.0.20", "new program" => "0.2.0.21", _ when text.StartsWith("program ") => text[8..], _ => null };
    }

    /// <summary>Archived copies of the fixture versions (relative to the program folder).</summary>
    private static readonly string V20 = Path.Combine(Updater.ArchiveFolder, "0.2.0.20"), V21 = Path.Combine(Updater.ArchiveFolder, "0.2.0.21");

    private string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

    private string HelperFolder { get { var folder = Path.Combine(app, ".helper-test"); Directory.CreateDirectory(folder); return folder; } }

    /// <summary>The helper as started by the program: the program has exited, no restart is performed.</summary>
    private int Helper(Version? current = null, Action<string, IReadOnlyList<string>>? start = null) =>
        Updater.ApplyPending(42, app, Exe, current ?? Current, _ => false, start ?? ((_, _) => { }), HelperFolder, probe: FakeProbe);

    /// <summary>Installs version <paramref name="to"/> over <paramref name="from"/> directly (as --update does), then «restarts».</summary>
    private void InstallVersion(Version from, Version to, int? keep = 3, bool? light = true)
    {
        var release = new ReleaseInfo(to, "v" + to, "");
        Package(new() { ["BaseNavigator/" + Exe] = "program " + to, ["BaseNavigator/Assets/basenavigator.png"] = "icon " + to }, version: to);
        new Updater(app, Path.Combine(app, Exe), new FolderSource(releases, release), _ => to.ToString()) { Keep = keep, Light = light }
            .Install(release, from, Runtime, true);
        Restart();
    }

    private static Version V(int build) => new(0, 2, 0, build);

    /// <summary>Archived versions on disk (names of the archive folders), newest first.</summary>
    private string[] Archived() => Directory.Exists(Path.Combine(app, Updater.ArchiveFolder))
        ? Directory.GetDirectories(Path.Combine(app, Updater.ArchiveFolder)).Select(Path.GetFileName).OfType<string>()
            .OrderByDescending(x => x.StartsWith('.') ? new Version() : Version.Parse(x.Split('~')[0])).ToArray()
        : [];

    [Fact]
    public void RuntimeOfReleaseAssetsIsPortable()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Assert.Equal((OperatingSystem.IsWindows() ? "win-" : "linux-") + architecture, Updater.CurrentRuntime);
        Assert.Equal("linux-x64", Updater.Runtime("linux", System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("linux-arm64", Updater.Runtime("linux", System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("win-x86", Updater.Runtime("win", System.Runtime.InteropServices.Architecture.X86));
    }

    [Theory]
    [InlineData("win-x64", false, "BaseNavigator-0.2.0.21-win-x64.zip")]
    [InlineData("win-x64", true, "BaseNavigator-0.2.0.21-win-x64-light.zip")]
    [InlineData("linux-x64", false, "BaseNavigator-0.2.0.21-linux-x64.tar.gz")]
    [InlineData("linux-x64", true, "BaseNavigator-0.2.0.21-linux-x64-light.tar.gz")]
    public void AssetMatchesReleaseScript(string runtime, bool light, string expected) =>
        Assert.Equal(expected, Updater.AssetName(Next, runtime, light));

    [Fact] public void InstallReplacesProgramFilesOnlyAndKeepsPrevious()
    {
        Package(Good);
        var files = Create().Install(Release, Current, Runtime, true);
        Assert.Contains(Exe, files);
        Assert.Equal("new program", Read(Exe)); Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("{\"keep\":1}", Read("Settings", "library.json")); Assert.True(File.Exists(Path.Combine(app, "BaseNavigator.portable")));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
        Assert.False(Directory.Exists(Path.Combine(app, ".previous.new")));
        // A running Windows executable is renamed, removed at the next start.
        if (OperatingSystem.IsWindows()) Assert.Equal("old program", File.ReadAllText(Assert.Single(Directory.GetFiles(app, Exe + ".old-*"))));
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.Empty(Directory.GetFiles(app, Exe + ".old*"));
    }

    [Fact] public void RollbackSwapsBothWays()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        // Without a restart the files on disk no longer match the running version.
        Assert.Contains("Перезапустите", Assert.Throws<InvalidOperationException>(() => Create().Rollback(Current)).Message);
        Restart();
        Assert.Equal("0.2.0.20", Create().Rollback(Next));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Restart();
        Assert.Equal("0.2.0.21", Create().Rollback(Current));
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("{\"keep\":1}", Read("Settings", "library.json"));
    }

    // From 0.4.0.1 the release carries the program's licence as Assets/LICENSE.txt: older updaters (0.3.0.8, 0.4.0.0) refuse any
    // file outside the executable, Start-with-log.cmd, THIRD-PARTY-NOTICES.txt and Assets/*, so a top-level LICENSE would stop
    // their update to 0.4.0.1 (reports/public-repo-20261026/part2.md).
    private static readonly string License = Path.Combine("Assets", "LICENSE.txt");

    [Fact] public void LicenseComesWithTheReleaseAndGoesWithARollbackToAVersionWithoutIt()
    {
        var entries = Good; entries["BaseNavigator/Assets/LICENSE.txt"] = "MIT";
        Package(entries);
        Assert.Contains(License, Create().Install(Release, Current, Runtime, true));
        Assert.Equal("MIT", Read(License));
        Assert.False(File.Exists(Path.Combine(app, V20, License)));
        Restart();
        // The older version had no licence: its files come back exactly, the newer copy keeps it.
        Assert.Equal("0.2.0.20", Create().Rollback(Next));
        Assert.False(File.Exists(Path.Combine(app, License)));
        Assert.Equal("MIT", Read(V21, License));
        Restart();
        Assert.Equal("0.2.0.21", Create().Rollback(Current));
        Assert.Equal("MIT", Read(License));
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void LicenseComesWithAPreparedUpdateToo()
    {
        var entries = Good; entries["BaseNavigator/Assets/LICENSE.txt"] = "MIT";
        Package(entries);
        Create().Stage(Release, Runtime, true);
        Helper();
        Assert.Equal("MIT", Read(License));
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void AReleaseWithoutTheLicenseStillInstalls()
    {
        // An older release (0.4.0.0 and before) has no licence file and is a complete program.
        Package(Good);
        Assert.DoesNotContain(License, Create().Install(Release, Current, Runtime, true));
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void ATopLevelLicenseIsRefusedAsOlderUpdatersDo()
    {
        var entries = Good; entries["BaseNavigator/LICENSE"] = "MIT";
        Package(entries);
        Assert.Contains("вне состава", Assert.Throws<InvalidDataException>(() => Create().Install(Release, Current, Runtime, true)).Message);
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void SavedCopyRestoresItselfWithoutMovingItsFolder()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var saved = Create(directory: Path.Combine(app, V20));
        Assert.True(saved.FromPrevious);
        Assert.Throws<InvalidOperationException>(() => saved.Install(Release, Current, Runtime, true));
        Assert.Equal("0.2.0.20", saved.Rollback(Current));
        Assert.Equal("old program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
    }

    [Theory] [InlineData("checksum")] [InlineData("probe")] [InlineData("traversal")] [InlineData("missing")]
    public void RejectedPackageChangesNothing(string defect)
    {
        var entries = defect switch
        {
            "traversal" => new() { ["BaseNavigator/../evil.txt"] = "x", ["BaseNavigator/" + Exe] = "new" },
            "missing" => new Dictionary<string, string> { ["BaseNavigator/readme.txt"] = "x" },
            _ => Good
        };
        Package(entries, sum: defect == "checksum" ? new string('0', 64) : null);
        var updater = Create(probe: defect == "probe" ? null : "0.2.0.21");
        var error = Assert.ThrowsAny<Exception>(() => updater.Install(Release, Current, Runtime, true));
        Assert.True(error is InvalidDataException or InvalidOperationException, error.GetType().Name);
        if (defect == "probe") Assert.Contains(".NET 10", error.Message);
        Assert.Equal("old program", Read(Exe));
        Assert.False(Directory.Exists(Path.Combine(app, Updater.ArchiveFolder)));
        Assert.False(File.Exists(Path.Combine(folder, "evil.txt")));
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
    }

    [Fact] public void RollbackWithoutSavedVersionExplains() =>
        Assert.Contains("не сохранена", Assert.Throws<InvalidOperationException>(() => Create().Rollback(Current)).Message);

    [Fact] public void CleanupRemovesInterruptedDownloads()
    {
        Directory.CreateDirectory(Path.Combine(app, ".update-stale", "package"));
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
        Assert.True(Updater.IsLight(typeof(UpdaterTests).Assembly));
    }

    [Fact] public void GitHubAnswersAndErrorsAreReadable()
    {
        var release = GitHubReleaseSource.ParseLatest("{\"tagName\":\"v0.2.0.21\",\"body\":\"notes\"}");
        Assert.Equal(Next, release.Version); Assert.Equal("v0.2.0.21", release.Tag); Assert.Equal("notes", release.Notes);
        Assert.Equal("", GitHubReleaseSource.ParseLatest("{\"tagName\":\"v1.0\"}").Notes);
        // The tag of the release 0.4.0.0: newer than every 0.3.0.x, compared by number (0.3.0.10 is not above it).
        var stable = GitHubReleaseSource.ParseLatest("{\"tagName\":\"v0.4.0.0\",\"body\":\"\"}");
        Assert.Equal(new Version(0, 4, 0, 0), stable.Version); Assert.Equal("v0.4.0.0", stable.Tag);
        Assert.True(stable.Version > new Version(0, 3, 0, 10)); Assert.True(stable.Version < new Version(0, 4, 0, 1));
        Assert.Throws<InvalidDataException>(() => GitHubReleaseSource.ParseLatest("{\"tagName\":\"latest\"}"));
        Assert.Contains("gh auth login", GitHubReleaseSource.Failure(1, "To get started with GitHub CLI, please run:  gh auth login").Message);
        Assert.Contains("release not found", GitHubReleaseSource.Failure(1, "release not found\n").Message);
        Assert.Contains("код 4", GitHubReleaseSource.Failure(4, " ").Message);
    }

    [Fact] public void ProbeRejectsMissingAndNonExecutableFiles()
    {
        Assert.Null(Updater.ProbeVersion(Path.Combine(folder, "absent")));
        Assert.Null(Updater.ProbeVersion(Path.Combine(app, "Assets", "basenavigator.png")));
    }

    [Fact] public void TarPackagesUnpackAndLinksAreRefused()
    {
        var archive = Path.Combine(folder, "p.tar.gz");
        void Write(Action<TarWriter> entries)
        {
            using var stream = new GZipStream(File.Create(archive), CompressionLevel.Fastest);
            using var tar = new TarWriter(stream, TarEntryFormat.Pax);
            entries(tar);
        }
        Write(tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "BaseNavigator/Assets/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "BaseNavigator/Assets/a.png") { DataStream = new MemoryStream([1, 2]) });
        });
        Assert.Equal([Path.Combine("Assets", "a.png")], Updater.Extract(archive, Path.Combine(folder, "out1")));
        Write(tar => tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "BaseNavigator/link") { LinkName = "/etc/passwd" }));
        Assert.Throws<InvalidDataException>(() => Updater.Extract(archive, Path.Combine(folder, "out2")));
        Write(tar => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Other/x") { DataStream = new MemoryStream([1]) }));
        Assert.Throws<InvalidDataException>(() => Updater.Extract(archive, Path.Combine(folder, "out3")));
    }

    [Fact] public void FailedReplacementRestoresEveryFile()
    {
        Package(Good);
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        // A file that cannot be replaced: read-only on Windows, a directory in its place elsewhere.
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        try
        {
            Assert.ThrowsAny<Exception>(() => Create().Install(Release, Current, Runtime, true));
            Assert.Equal("old program", Read(Exe));
            if (OperatingSystem.IsWindows()) Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
            Assert.False(Directory.Exists(Path.Combine(app, Updater.ArchiveFolder)));
            Assert.False(Directory.Exists(Path.Combine(app, ".previous.new")));
        }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); }
        Restart();
        if (!OperatingSystem.IsWindows()) Directory.Delete(icon, true);
        else File.SetAttributes(icon, FileAttributes.ReadOnly);
        try { if (!OperatingSystem.IsWindows()) File.WriteAllText(icon, "old icon"); }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); }
        Create().Install(Release, Current, Runtime, true);
        Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        // The saved copy is deletable, so the next update can replace it.
        Assert.All(Directory.GetFiles(Path.Combine(app, Updater.ArchiveFolder), "*", SearchOption.AllDirectories),
            file => Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));
    }

    [Fact] public void ProbeReadsVersionOfARealProgram()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !File.Exists(dotnet)) return;
        Assert.Matches(@"^\d+\.\d+", Updater.ProbeVersion(dotnet)?.Trim() ?? "");
        Assert.Equal(Release, Create().Latest());
    }

    [Fact] public void GitHubCliFailuresBecomeMessages()
    {
        var missing = new GitHubReleaseSource(gh: Path.Combine(folder, "no-gh"));
        Assert.Contains("GitHub CLI (gh)", Assert.Throws<InvalidOperationException>(() => missing.Latest()).Message);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !File.Exists(dotnet)) return;
        // dotnet rejects "release view", like gh with an unknown repository.
        var failing = new GitHubReleaseSource("owner/repo", dotnet);
        Assert.Equal("owner/repo", failing.Repository);
        Assert.Contains("GitHub CLI вернул ошибку", Assert.Throws<InvalidOperationException>(() => failing.Latest()).Message);
        Assert.Throws<InvalidOperationException>(() => failing.Download(Release, ["a.zip"], folder));
    }

    [Fact] public void FailedRollbackKeepsBothVersions()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        try { Assert.ThrowsAny<Exception>(() => Create().Rollback(Next)); }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); else Directory.Delete(icon, true); }
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
        if (!OperatingSystem.IsWindows()) File.WriteAllText(icon, "new icon");
        Assert.Equal("0.2.0.20", Create().Rollback(Next));
        Assert.Equal("old program", Read(Exe));
    }

    [Theory] [InlineData("BaseNavigator/Settings/library.json")] [InlineData("BaseNavigator/BaseNavigator.portable")]
    [InlineData("BaseNavigator/.previous/version.txt")] [InlineData("BaseNavigator/SETTINGS/library.json")]
    [InlineData("BaseNavigator/.versions/0.2.0.19/version.txt")]
    public void PackageCannotTouchProfileOrServiceFiles(string entry)
    {
        var entries = Good; entries[entry] = "PACKAGE DEFAULT";
        Package(entries);
        Assert.Contains("вне состава", Assert.Throws<InvalidDataException>(() => Create().Install(Release, Current, Runtime, true)).Message);
        Assert.Equal("{\"keep\":1}", Read("Settings", "library.json"));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void CleanupLeavesRunningOperationAlone()
    {
        var active = Path.Combine(app, ".update-active");
        Directory.CreateDirectory(active);
        using (new FileStream(Path.Combine(app, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Updater.Cleanup(app, Path.Combine(app, Exe));
            Assert.True(Directory.Exists(active));
            Package(Good);
            Assert.Contains("уже выполняется", Assert.Throws<IOException>(() => Create().Install(Release, Current, Runtime, true)).Message);
        }
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.False(Directory.Exists(active));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void InterruptedOperationIsUndoneOrCompletedAtStart(bool committed)
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        // State right after files were replaced by a second operation that did not finish.
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, Exe), "new program");
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "new icon");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.21");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe, "+" + Path.Combine("Assets", "basenavigator.png"), "-Start-with-log.cmd"]);
        File.WriteAllText(Path.Combine(app, Exe), "third program");
        File.WriteAllText(Path.Combine(app, "Start-with-log.cmd"), "added");
        if (committed) File.WriteAllLines(Path.Combine(app, ".update-committed"), ["promote", ""]);
        Helper();
        Assert.False(Directory.Exists(staging)); Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
        if (committed)
        {
            Assert.Equal("third program", Read(Exe));
            Assert.Equal("0.2.0.21", Create().PreviousVersion);
        }
        else
        {
            Assert.Equal("new program", Read(Exe));
            Assert.False(File.Exists(Path.Combine(app, "Start-with-log.cmd")));
            Assert.Equal("old program", Read(V20, Exe));
        }
    }

    [Fact] public void InterruptedCleanupNeverDeletesRestoredFiles()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "saved icon");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Path.Combine("Assets", "basenavigator.png")]);
        // First recovery restores the file, but its leftovers are removed only partly (the saved copy goes, not everything).
        File.Delete(Path.Combine(staging, "Assets", "basenavigator.png"));
        Helper();
        Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        Assert.False(Directory.Exists(staging));
    }

    [Fact] public void FailedCommitMarkerUndoesTheInstall()
    {
        Package(Good);
        Directory.CreateDirectory(Path.Combine(app, ".update-committed"));
        Assert.ThrowsAny<Exception>(() => Create().Install(Release, Current, Runtime, true));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Assert.False(Updater.ChangedInThisProcess);
    }

    [Fact] public void RollbackFromSavedCopyIsATransaction()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        var saved = Create(directory: Path.Combine(app, V20));
        try { Assert.ThrowsAny<Exception>(() => saved.Rollback(Next)); }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); else Directory.Delete(icon, true); }
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.False(Directory.Exists(Path.Combine(app, ".previous.new")));
    }

    [Fact] public void FinishedRollbackFromSavedCopySurvivesFailedCleanup()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var saved = Create(directory: Path.Combine(app, V20));
        // The manifest cannot be deleted while the rollback finishes (open without delete sharing).
        var staging = Path.Combine(app, ".previous.new");
        FileStream? locked = null;
        Updater.BeforeFinish = folder => locked = new FileStream(Path.Combine(folder, "files.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { Assert.Equal("0.2.0.20", saved.Rollback(Next)); }
        finally { Updater.BeforeFinish = null; }
        Assert.NotNull(locked);
        Assert.Equal("old program", Read(Exe));
        if (OperatingSystem.IsWindows()) Assert.True(File.Exists(Path.Combine(staging, "files.txt")));
        locked!.Dispose();
        Restart();
        Helper();
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.False(Directory.Exists(staging)); Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
    }

    [Fact] public void RecoveryAtStartBlocksFurtherChangesUntilRestart()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, Exe), "new program");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.21");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe]);
        File.WriteAllText(Path.Combine(app, Exe), "interrupted program");
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.Equal("interrupted program", Read(Exe));
        Assert.True(Create().NeedsRecovery);
        Helper();
        Assert.Equal("new program", Read(Exe));
        Assert.True(Updater.ChangedInThisProcess);
        Assert.Throws<InvalidOperationException>(() => Create().Rollback(Next));
        // The same recovery inside an operation also stops it before anything is saved with a wrong version.
        Restart();
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, Exe), "new program");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe]);
        File.WriteAllText(Path.Combine(app, Exe), "interrupted program");
        Assert.Throws<InvalidOperationException>(() => Create().Install(Release, Current, Runtime, true));
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
    }

    [Fact] public void RecoveryWithLockedManifestStillBlocksChanges()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, Exe), "new program");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe]);
        File.WriteAllText(Path.Combine(app, Exe), "interrupted program");
        using (new FileStream(Path.Combine(staging, "files.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Helper();
        Assert.Equal("new program", Read(Exe));
        Assert.True(Updater.ChangedInThisProcess);
        Helper();
        Assert.Throws<InvalidOperationException>(() => Create().Install(Release, Current, Runtime, true));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
    }

    [Fact] public void StagedUpdateLeavesTheRunningProgramUntouched()
    {
        Package(Good);
        var updater = Create();
        updater.Stage(Release, Runtime, true);
        Assert.Equal("old program", Read(Exe));
        Assert.Equal("new program", Read(Updater.PendingFolder, Exe));
        Assert.Equal("0.2.0.21", updater.PendingVersion);
        Assert.True(Updater.ChangedInThisProcess);
        Assert.Throws<InvalidOperationException>(() => Create().Stage(Release, Runtime, true));
        Assert.False(Directory.Exists(Path.Combine(app, Updater.ArchiveFolder)));
        Assert.Contains("подготовленного", Assert.Throws<InvalidOperationException>(() => { Restart(); updater.DiscardPending(); updater.LaunchHelper(Current, []); }).Message);
    }

    [Fact] public void HelperThatDoesNotStartKeepsThePreparedPackageAndSaysSo()
    {
        Package(Good);
        // The running file cannot be copied for the helper (Codex audit 15.10): the download itself succeeded.
        var updater = new Updater(app, Path.Combine(folder, "gone", Exe), new FolderSource(releases, Release), _ => "0.2.0.21");
        var staged = updater.StageAndLaunch(Release, Current, Runtime, true, []);
        Assert.NotNull(staged.HelperError);
        Assert.Equal("0.2.0.21", updater.PendingVersion);
        Assert.Equal("new program", Read(Updater.PendingFolder, Exe));
        Assert.Equal("old program", Read(Exe));
        Assert.True(Updater.ChangedInThisProcess);
        Assert.StartsWith("Загружена версия 0.2.0.21, но установщик не запустился (", staged.Notice);
        // Shared by every entry point (the settings page, the offer, the startup download): Grok audit 15.10.
        Assert.Equal(staged.HelperError, Updater.HelperFailure);
        Assert.EndsWith("Она будет установлена после следующего запуска и закрытия программы", staged.Notice);
        // The package survives the session: the next start finds it pending and starts the helper (Program.PrepareUpdateAtStart).
        Restart();
        Assert.Equal("0.2.0.21", Create().PendingVersion);
    }

    [Fact] public void FailedDownloadLeavesNothingPending()
    {
        Package(Good, sum: new string('0', 64));
        var updater = Create();
        Assert.ThrowsAny<Exception>(() => updater.StageAndLaunch(Release, Current, Runtime, true, []));
        Assert.Null(updater.PendingVersion);
        Assert.False(Updater.ChangedInThisProcess);
        Assert.Equal("Загружена версия 0.2.0.21; она будет установлена после закрытия программы", new StagedUpdate(Next, null).Notice);
    }

    [Fact] public void HelperGivesUpWhenAnotherCopyKeepsRunning()
    {
        Package(Good);
        var updater = Create();
        updater.Stage(Release, Runtime, true);
        using (var running = Updater.Register(app, TimeSpan.Zero))
        {
            var code = Updater.ApplyPending(42, app, Exe, Current, _ => false, (_, _) => { }, HelperFolder, TimeSpan.FromMilliseconds(300));
            Assert.Equal(7, code);
        }
        Assert.Equal("old program", Read(Exe));
        Assert.StartsWith("error Другая копия", Read(".update-result.txt"));
        Assert.Equal("0.2.0.21", Create().PendingVersion);
    }

    [Fact] public void HelperAppliesAfterExitRecordsResultAndRestarts()
    {
        Package(Good);
        var updater = Create();
        updater.Stage(Release, Runtime, true);
        updater.RequestRestart();
        var cwd = Path.Combine(folder, "start folder"); Directory.CreateDirectory(cwd);
        File.WriteAllLines(Path.Combine(HelperFolder, "arguments.txt"), [cwd, "--settings", "x y.json"]);
        var polls = 0; (string Exe, IReadOnlyList<string> Args)? started = null;
        var code = Updater.ApplyPending(42, app, Exe, Current, pid => ++polls < 3, (exe, args) => started = (exe, args), HelperFolder, probe: FakeProbe);
        Assert.Equal(0, code); Assert.Equal(3, polls);
        Assert.Equal("new program", Read(Exe)); Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
        // The program starts again in the folder it was started from (a relative --settings keeps its profile).
        Assert.Equal(Path.Combine(app, Exe), started?.Exe); Assert.Equal([cwd, "--settings", "x y.json"], started?.Args);
        Assert.Equal("ok 0.2.0.21", Updater.TakeResult(app));
        Assert.Null(Updater.TakeResult(app));
        Assert.Equal("{\"keep\":1}", Read("Settings", "library.json"));
    }

    [Fact] public void HelperRollbackSwapsWithoutRestartRequest()
    {
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Restart();
        Assert.Equal("0.2.0.20", Create().StageRollback(Next));
        Assert.Equal("new program", Read(Exe));
        var started = false;
        Assert.Equal(0, Helper(Next, (_, _) => started = true));
        Assert.False(started);
        Assert.Equal("old program", Read(Exe));
        Assert.Equal("new program", Read(V21, Exe));
        Assert.Equal("0.2.0.21", Create().PreviousVersion);
        Assert.Equal("ok 0.2.0.20", Updater.TakeResult(app));
    }

    [Fact] public void HelperFailureRestoresFilesAndReportsError()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        try { Assert.Equal(7, Helper()); }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); else Directory.Delete(icon, true); }
        Assert.Equal("old program", Read(Exe));
        if (OperatingSystem.IsWindows()) Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Assert.StartsWith("error ", Updater.TakeResult(app));
        Restart();
        Create().DiscardPending();
        Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
        // Without prepared files the helper does nothing and records nothing.
        Assert.Equal(0, Helper());
        Assert.Null(Updater.TakeResult(app));
    }

    [Fact] public void RealProcessPathsFailSafely()
    {
        Package(Good);
        var updater = Create();
        updater.Stage(Release, Runtime, true);
        // The fixture "program" is a text file: the helper copy is made, starting it fails with a clear error.
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => updater.LaunchHelper(Current, ["--x"]));
        var launched = Assert.Single(Directory.GetDirectories(app, ".helper-*"));
        Assert.Equal([Environment.CurrentDirectory, "--x"], File.ReadAllLines(Path.Combine(launched, "arguments.txt")));
        // No such process: the helper does not wait; restarting a non-executable program is ignored.
        updater.RequestRestart();
        Assert.Equal(0, Updater.ApplyPending(int.MaxValue, app, Exe, Current, helperFolder: launched, probe: FakeProbe));
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("ok 0.2.0.21", Updater.TakeResult(app));
    }

    [Fact] public void CleanupKeepsOnlyRunningHelpers()
    {
        var helper = Path.Combine(app, ".helper-running");
        Directory.CreateDirectory(helper);
        using (new FileStream(Path.Combine(helper, "running.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Updater.Cleanup(app, Path.Combine(app, Exe));
            Assert.True(Directory.Exists(helper));
            Package(Good);
            Create().Stage(Release, Runtime, true);
            // A second helper is not started while one waits.
            Assert.False(Create().LaunchHelper(Current, []));
        }
        // A fresh folder may belong to a helper that has not taken its lock yet.
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.True(Directory.Exists(helper));
        Directory.SetCreationTimeUtc(helper, DateTime.UtcNow.AddDays(-2));
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.False(Directory.Exists(helper));
    }

    [Fact] public void RunLockIsSharedForCopiesAndExclusiveForUpdates()
    {
        using (var first = Updater.Register(app, TimeSpan.FromSeconds(1)))
        using (var second = Updater.Register(app, TimeSpan.FromSeconds(1)))
        {
            Assert.NotNull(first.Hold); Assert.NotNull(second.Hold); Assert.False(first.Waited);
            Assert.Null(Create().TryExclusive());
        }
        using (var exclusive = Create().TryExclusive())
        {
            Assert.NotNull(exclusive);
            // A copy started while files are replaced waits and gives up after the limit instead of running.
            var blocked = Updater.Register(app, TimeSpan.FromMilliseconds(600));
            Assert.True(blocked.TimedOut); Assert.Null(blocked.Hold);
        }
        // Released during the wait: the copy continues, marked as having waited (it must start again).
        var holder = Create().TryExclusive()!;
        var release = Task.Run(async () => { await Task.Delay(300); holder.Dispose(); });
        using var late = Updater.Register(app, TimeSpan.FromSeconds(10));
        release.GetAwaiter().GetResult();
        Assert.True(late.Waited); Assert.False(late.TimedOut); Assert.NotNull(late.Hold);
    }

    [Fact] public void HelperWaitsForEveryRunningCopy()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var other = Updater.Register(app, TimeSpan.FromSeconds(1));
        var helper = Task.Run(() => Helper());
        Thread.Sleep(1200);
        Assert.False(helper.IsCompleted);
        Assert.Equal("old program", Read(Exe));
        other.Dispose();
        Assert.True(helper.Wait(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, helper.Result);
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void UnusableRunLockStopsTheHelperWithAnError()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        Directory.CreateDirectory(Path.Combine(app, ".run.lock"));
        using (var registration = Updater.Register(app, TimeSpan.FromSeconds(1)))
        {
            Assert.Null(registration.Hold); Assert.False(registration.TimedOut);
        }
        Assert.Null(Create().TryExclusive(out var impossible)); Assert.True(impossible);
        Assert.Equal(7, Helper());
        Assert.Equal("old program", Read(Exe));
        Assert.StartsWith("error ", Updater.TakeResult(app));
    }

    [Fact] public void NoRestartWithoutKnownArguments()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        Create().RequestRestart();
        var started = false;
        // arguments.txt of this helper is missing: the update is applied, the program is not started with a default profile.
        Assert.Equal(0, Helper(start: (_, _) => started = true));
        Assert.False(started);
        Assert.Equal("new program", Read(Exe));
        Assert.Contains("not restarting", File.ReadAllText(Path.Combine(app, ".update-helper.log")));
    }

    [Fact] public void AppliedPackageIsNotAppliedAgainWhenItCannotBeRemoved()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var pending = Path.Combine(app, Updater.PendingFolder);
        // Every file of the package stays readable but cannot be removed, as when another program holds it open.
        var holds = Directory.GetFiles(pending, "*", SearchOption.AllDirectories)
            .Select(x => new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
        try
        {
            Assert.Equal(0, Helper());
            Assert.Equal("old program", Read(V20, Exe));
            Restart();
            Assert.Equal(0, Helper(Next));
        }
        finally { holds.ForEach(x => x.Dispose()); }
        // The second helper found the same package already applied: the archive still holds the useful old version.
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
    }

    [Fact] public void DisabledFileLockingStopsUpdates()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "tRuE");
        try
        {
            Assert.True(Updater.FileLockingDisabled);
            Assert.Null(Create().TryExclusive(out var impossible)); Assert.True(impossible);
            Package(Good);
            Create().Stage(Release, Runtime, true);
            Assert.Equal(7, Helper());
            Assert.Contains("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", Updater.TakeResult(app));
            Assert.Equal("old program", Read(Exe));
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", previous); }
    }

    [Fact] public void RegistrationIsReadOnlyAndKeepsTheLockFileName()
    {
        using var registration = Updater.Register(app, TimeSpan.FromSeconds(1));
        Assert.NotNull(registration.Hold);
        Assert.False(registration.Hold!.CanWrite);
        Assert.Null(Create().TryExclusive());
        if (OperatingSystem.IsWindows())
            Assert.ThrowsAny<IOException>(() => File.Move(Path.Combine(app, ".run.lock"), Path.Combine(app, "renamed.lock")));
    }

    [Fact] public void PackagesWithoutTheProgramAreRefused()
    {
        Package(new() { ["BaseNavigator/Assets/basenavigator.png"] = "new icon" });
        Assert.Contains("нет файла", Assert.Throws<InvalidDataException>(() => Create().Install(Release, Current, Runtime, true)).Message);
        Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
    }

    [Fact] public void DamagedPreparedPackageIsReported()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        File.Delete(Path.Combine(app, Updater.PendingFolder, Exe));
        Assert.Equal(7, Helper());
        Assert.Contains("неполное", Updater.TakeResult(app));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void RecoveryAtStartIsHandedToTheHelper()
    {
        Directory.CreateDirectory(Path.Combine(app, ".previous.new"));
        var updater = Create();
        Assert.True(updater.NeedsRecovery);
        // The fixture program cannot be started; the request for the helper is prepared all the same.
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => updater.LaunchRecovery(Current, ["--x"]));
        Assert.True(File.Exists(Path.Combine(app, Updater.PendingFolder, "restart")));
    }

    // Grok full audit 24.10, P2-2: the helper of the repair or of a prepared update did not start, and the start went on silently on
    // a folder of mixed files. Now it is said; .previous.new stays, so the next start repairs.
    [Fact] public void AHelperThatDoesNotStartAtTheStartIsSaid()
    {
        Assert.Equal((false, null, null), Create().RecoverAtStart(Current, ["--x"]));
        Directory.CreateDirectory(Path.Combine(app, ".previous.new"));
        var (started, notice, failure) = Create().RecoverAtStart(Current, ["--x"]);
        Assert.False(started); Assert.NotNull(failure);
        Assert.Contains("Прерванное обновление не восстановлено: установщик не запустился", notice);
        Assert.Contains("при следующем запуске", notice);
        Assert.True(Create().NeedsRecovery);
        // A repair that ran and failed is said with its reason and not repeated.
        File.WriteAllText(Path.Combine(app, ".update-result.txt"), "error disk full");
        Assert.Equal((false, "Не удалось восстановить прерванное обновление: disk full", null), Create().RecoverAtStart(Current, ["--x"]));
        Directory.Delete(Path.Combine(app, ".previous.new"), true);
        // A prepared update whose helper does not start.
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var pending = Create().ResumePending(Current, ["--x"]);
        Assert.Contains($"Версия {Release.Version} не установлена: установщик не запустился", pending);
        Assert.NotNull(Updater.HelperFailure);
    }

    [Fact] public void FailedAppliedMarkerIsFinishedLaterWithoutSecondApplication()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        Directory.CreateDirectory(Path.Combine(app, ".update-applied"));
        // Files are replaced and committed; recording the package fails, so the operation stays committed but unfinished.
        Assert.Equal(0, Helper());
        Assert.Equal("new program", Read(Exe));
        Assert.True(Create().NeedsRecovery);
        Directory.Delete(Path.Combine(app, ".update-applied"));
        Restart();
        Helper(Next);
        // Finished from the commit marker: the old version is kept, the package is not applied a second time.
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("old icon", Read(V20, "Assets", "basenavigator.png"));
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
        Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
    }

    [Theory] [InlineData("")] [InlineData("promote")] [InlineData("garbage|x")]
    public void IncompleteCommitMarkerIsTreatedAsUnfinished(string content)
    {
        var marker = content.Length == 0 ? [] : content.Split('|');
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, Exe), "old program");
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "old icon");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.20");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe, "+" + Path.Combine("Assets", "basenavigator.png")]);
        File.WriteAllText(Path.Combine(app, Exe), "new program");
        File.WriteAllText(Path.Combine(app, "Assets", "basenavigator.png"), "new icon");
        File.WriteAllLines(Path.Combine(app, ".update-committed"), marker);
        File.WriteAllText(Path.Combine(app, ".update-committed.tmp"), "promote");
        Assert.Equal(0, Helper());
        // Undone and then applied once: the saved previous version is the old one.
        Assert.Equal("new program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal("old icon", Read(V20, "Assets", "basenavigator.png"));
        Assert.False(File.Exists(Path.Combine(app, ".update-committed.tmp")));
    }

    [Fact] public void InterruptedBeforeCommitKeepsThePackageApplicable()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        // State after the executable was replaced and before the commit marker: backup and manifest, no marker.
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, Exe), "old program");
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "old icon");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.20");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe, "+" + Path.Combine("Assets", "basenavigator.png")]);
        File.WriteAllText(Path.Combine(app, Exe), "new program");
        Assert.Equal(0, Helper());
        // Recovery put the old files back, and the still valid package was applied afterwards.
        Assert.Equal("new program", Read(Exe)); Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal("ok 0.2.0.21", Updater.TakeResult(app));
    }

    [Fact] public void OnlyTheHelperThatInstalledRestarts()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        Create().RequestRestart();
        File.WriteAllLines(Path.Combine(HelperFolder, "arguments.txt"), [app]);
        var pending = Path.Combine(app, Updater.PendingFolder);
        var holds = Directory.GetFiles(pending, "*", SearchOption.AllDirectories)
            .Select(x => new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
        var starts = 0;
        try
        {
            Assert.Equal(0, Helper(start: (_, _) => starts++));
            Restart();
            Assert.Equal(0, Helper(Next, (_, _) => starts++));
        }
        finally { holds.ForEach(x => x.Dispose()); }
        Assert.Equal(1, starts);
    }

    [Fact] public void TrialRunLeavesNothingInThePackage()
    {
        Package(Good);
        // A trial run of an older build registers a .run.lock next to the candidate (found in the live check).
        var updater = new Updater(app, Path.Combine(app, Exe), new FolderSource(releases, Release), candidate =>
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(candidate)!, ".run.lock"), "");
            return "0.2.0.21";
        });
        updater.Stage(Release, Runtime, true);
        Assert.False(File.Exists(Path.Combine(app, Updater.PendingFolder, ".run.lock")));
        Assert.Equal(0, Helper());
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void RetryAfterShortLockUsesTheCompletePackage()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        // The block ends after the first attempt has already moved the executable of its copy.
        var release = Task.Run(async () =>
        {
            await Task.Delay(250);
            if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal);
            else Directory.Delete(icon, true);
        });
        Assert.Equal(0, Helper());
        release.Wait();
        Assert.Equal("new program", Read(Exe)); Assert.Equal("new icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("ok 0.2.0.21", Updater.TakeResult(app));
        Assert.Contains("attempt 1", File.ReadAllText(Path.Combine(app, ".update-helper.log")));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void IncompleteSavedCopyIsRefused(bool legacy)
    {
        // An archived copy (or .previous of an older build) without the program: not offered, refused when asked for.
        var folder = Path.Combine(app, legacy ? Updater.PreviousFolder : Path.Combine(Updater.ArchiveFolder, "0.2.0.19"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "version.txt"), "0.2.0.19");
        Assert.Empty(Create().SavedVersions());
        Assert.Throws<InvalidDataException>(() => Create().Rollback(Current));
        Assert.Throws<InvalidDataException>(() => Create().Rollback(Current, new Version(0, 2, 0, 19)));
        Assert.Throws<InvalidDataException>(() => Create().StageRollback(Current, new Version(0, 2, 0, 19)));
        Assert.Equal("old program", Read(Exe));
        Assert.False(Updater.ChangedInThisProcess);
        Assert.True(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("ksv47/basenavigator", true)] [InlineData("owner/repo.name_2", true)]
    [InlineData("", false)] [InlineData("owner", false)] [InlineData("owner/../x", false)] [InlineData("https://github.com/a/b", false)]
    public void RepositoryNameIsChecked(string value, bool valid) => Assert.Equal(valid, GitHubReleaseSource.IsRepository(value));

    [Fact] public void UpdateSettingsRoundTripAndRejectUnknownValues()
    {
        var path = Path.Combine(folder, "library.json");
        var store = new LibraryStore(path);
        store.Save(new Library { UpdateMode = UpdateMode.Download, UpdateRepository = "owner/mirror" });
        var loaded = new LibraryStore(path).Load();
        Assert.Equal(UpdateMode.Download, loaded.UpdateMode); Assert.Equal("owner/mirror", loaded.UpdateRepository);
        Assert.Equal(UpdateMode.Off, new Library().UpdateMode);
        File.WriteAllText(path, "{\"UpdateMode\":7}");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(path).Load());
        File.WriteAllText(path, "{\"UpdateRepository\":\"not a repo\"}");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(path).Load());
    }

    [Theory]
    [InlineData(new[] { "--update" }, UpdateCommand.Install)]
    [InlineData(new[] { "--check-update", "--no-gui" }, UpdateCommand.Check)]
    [InlineData(new[] { "--rollback" }, UpdateCommand.Rollback)]
    public void UpdateCommandsParse(string[] args, UpdateCommand expected) =>
        Assert.Equal(expected, DiagnosticArguments.Parse(args).Update);

    [Theory]
    [InlineData("--update", "--rollback")] [InlineData("--update", "--version")] [InlineData("--check-update", "--portable")]
    public void UpdateCommandsStandAlone(string first, string second) =>
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse([first, second]));

    [Fact] public void ArchiveKeepsTheNewestVersionsAndFollowsTheSetting()
    {
        var from = Current;
        foreach (var build in new[] { 21, 22, 23, 24, 25 }) { InstallVersion(from, V(build)); from = V(build); }
        // Every install archived the replaced version; the oldest ones went once there were more than three.
        Assert.Equal(["0.2.0.24", "0.2.0.23", "0.2.0.22"], Archived());
        Assert.Equal("program 0.2.0.25", Read(Exe));
        Assert.Equal("program 0.2.0.23", Read(Updater.ArchiveFolder, "0.2.0.23", Exe));
        Assert.Equal("icon 0.2.0.23", Read(Updater.ArchiveFolder, "0.2.0.23", "Assets", "basenavigator.png"));
        Assert.Equal("light", Read(Updater.ArchiveFolder, "0.2.0.23", "variant.txt"));
        Assert.False(File.Exists(Path.Combine(app, Updater.ArchiveFolder, "0.2.0.23", "files.txt")));
        var saved = Create().SavedVersions();
        Assert.Equal([V(24), V(23), V(22)], saved.Select(x => x.Version));
        Assert.All(saved, x => Assert.True(x.Light));
        Assert.Equal("0.2.0.24", Create().PreviousVersion);
        // A smaller setting trims at the next install, a larger one removes nothing.
        InstallVersion(V(25), V(26), keep: 1);
        Assert.Equal(["0.2.0.25"], Archived());
        InstallVersion(V(26), V(27), keep: 10);
        Assert.Equal(["0.2.0.26", "0.2.0.25"], Archived());
        // Without a known setting (the helper repairing an interruption) the archive is not trimmed.
        InstallVersion(V(27), V(28), keep: null);
        Assert.Equal(["0.2.0.27", "0.2.0.26", "0.2.0.25"], Archived());
        Assert.Equal("{\"keep\":1}", Read("Settings", "library.json"));
    }

    [Fact] public void RollbackToAChosenVersionAndBack()
    {
        InstallVersion(Current, V(21)); InstallVersion(V(21), V(22)); InstallVersion(V(22), V(23));
        Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        // Not the newest: the chosen one. The copy stays in the archive, the current version joins it.
        Assert.Equal("0.2.0.21", Create().Rollback(V(23), V(21)));
        Assert.Equal("program 0.2.0.21", Read(Exe)); Assert.Equal("icon 0.2.0.21", Read("Assets", "basenavigator.png"));
        // The installed version is not counted against the three kept ones.
        Assert.Equal(["0.2.0.23", "0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        Restart();
        // Without a version: the most recently saved one other than the running version — back where it came from.
        Assert.Equal("0.2.0.23", Create().Rollback(V(21)));
        Assert.Equal("program 0.2.0.23", Read(Exe));
        Restart();
        Assert.Equal("0.2.0.21", Create().Rollback(V(23)));
        Restart();
        // Through the helper, as the window does it.
        Assert.Equal("0.2.0.20", Create().StageRollback(V(21), V(20)));
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.Equal(0, Helper(V(21)));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Assert.Equal("ok 0.2.0.20", Updater.TakeResult(app));
        Assert.Equal(["0.2.0.23", "0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        Restart();
        Assert.Contains("0.2.0.19", Assert.Throws<InvalidOperationException>(() => Create().Rollback(Current, V(19))).Message);
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void StagedPackageCarriesTheSettingAndTheVariant()
    {
        InstallVersion(Current, V(21), keep: 10); InstallVersion(V(21), V(22), keep: 10);
        Assert.Equal(["0.2.0.21", "0.2.0.20"], Archived());
        Package(new() { ["BaseNavigator/" + Exe] = "program 0.2.0.23" }, version: V(23));
        var release = new ReleaseInfo(V(23), "v0.2.0.23", "");
        new Updater(app, Path.Combine(app, Exe), new FolderSource(releases, release), _ => "0.2.0.23") { Keep = 1, Light = false }.Stage(release, Runtime, true);
        Restart();
        Assert.Equal(0, Helper(V(22)));
        Assert.Equal("program 0.2.0.23", Read(Exe));
        // The helper trimmed to the setting of the window that prepared the package and recorded the variant it was told.
        Assert.Equal(["0.2.0.22"], Archived());
        Assert.Equal("self-contained", Read(Updater.ArchiveFolder, "0.2.0.22", "variant.txt"));
        Assert.False(Assert.Single(Create().SavedVersions()).Light);
    }

    [Fact] public void ArchivedCopyIsCompleteEvenWhenThePackageDropsAFile()
    {
        File.WriteAllText(Path.Combine(app, "Start-with-log.cmd"), "old script");
        File.WriteAllText(Path.Combine(app, "Assets", "extra.png"), "old extra");
        // A release (of Linux, where Start-with-log.cmd is not a part of it) that no longer has extra.png.
        var entries = Good; entries["BaseNavigator/THIRD-PARTY-NOTICES.txt"] = "notices";
        Package(entries, bare: true);
        var release = Release;
        var linux = new Updater(app, Path.Combine(app, Exe), new FolderSource(releases, release), FakeProbe) { Keep = 3, Light = true };
        Assert.Contains("Start-with-log.cmd", linux.RequiredFiles("win-x64")); Assert.DoesNotContain("Start-with-log.cmd", linux.RequiredFiles("linux-x64"));
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Assert.Equal("old script", Read(V20, "Start-with-log.cmd"));
        Assert.Equal("old extra", Read(V20, "Assets", "extra.png"));
        Assert.Equal("old program", Read(V20, Exe));
        // Settings and the portable marker are never archived.
        Assert.False(Directory.Exists(Path.Combine(app, V20, "Settings")));
        Assert.False(File.Exists(Path.Combine(app, V20, "BaseNavigator.portable")));
        // The program files are now exactly those of the new version (Grok audit 15.10): the dropped ones went.
        Assert.False(File.Exists(Path.Combine(app, "Assets", "extra.png")));
        Assert.Equal(OperatingSystem.IsWindows(), File.Exists(Path.Combine(app, "Start-with-log.cmd")));
        // A failed replacement puts everything back as it was.
        Restart();
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        try { Assert.ThrowsAny<Exception>(() => Create().Rollback(Next)); }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal); else { Directory.Delete(icon, true); File.WriteAllText(icon, "new icon"); } }
        Assert.Equal("new program", Read(Exe)); Assert.False(File.Exists(Path.Combine(app, "Assets", "extra.png")));
        Assert.Equal(["0.2.0.20"], Archived());
        // A restoration brings them back with the version that has them.
        Assert.Equal("0.2.0.20", Create().Rollback(Next));
        Assert.Equal("old script", Read("Start-with-log.cmd")); Assert.Equal("old extra", Read("Assets", "extra.png"));
    }

    [Fact] public void LegacyPreviousIsOfferedAndMovedIntoTheArchive()
    {
        // .previous of an older build: no variant, no archive yet.
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(Path.Combine(legacy, "Assets"));
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "Assets", "basenavigator.png"), "icon 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        var saved = Assert.Single(Create().SavedVersions());
        Assert.Equal(V(19), saved.Version); Assert.Null(saved.Light); Assert.Equal("—", saved.Variant);
        Assert.Equal(legacy, saved.Folder);
        Assert.Equal("0.2.0.19", Create().PreviousVersion);
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(["0.2.0.20", "0.2.0.19"], Archived());
        Assert.Equal("program 0.2.0.19", Read(Updater.ArchiveFolder, "0.2.0.19", Exe));
        Restart();
        Assert.Equal("0.2.0.19", Create().Rollback(Next, V(19)));
        Assert.Equal("program 0.2.0.19", Read(Exe));
    }

    [Fact] public void LegacyPreviousCanBeRestoredDirectlyAndDuplicatesAreDropped()
    {
        // .previous holds the same version the archive already has: the archive's copy is kept, .previous goes.
        InstallVersion(Current, V(21));
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(Path.Combine(legacy, "Assets"));
        File.WriteAllText(Path.Combine(legacy, Exe), "legacy 0.2.0.20");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.20");
        // The archive's copy of a version is the one offered and restored.
        Assert.Equal(Path.Combine(app, V20), Assert.Single(Create().SavedVersions()).Folder);
        Assert.Equal("0.2.0.20", Create().Rollback(V(21)));
        Assert.Equal("old program", Read(Exe));
        Assert.False(Directory.Exists(legacy));
        Assert.Empty(Directory.GetDirectories(app, ".previous*"));
        Assert.Equal(["0.2.0.21", "0.2.0.20"], Archived());
        // Restored straight from .previous (no archive copy of that version yet), then moved into the archive.
        Restart();
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.18");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.18");
        Assert.Equal("0.2.0.18", Create(keep: 10).Rollback(Current, V(18)));
        Assert.Equal("program 0.2.0.18", Read(Exe));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(["0.2.0.21", "0.2.0.20", "0.2.0.18"], Archived());
    }

    [Fact] public void IncompleteLegacyPreviousIsLeftAlone()
    {
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Assert.True(File.Exists(Path.Combine(legacy, "version.txt")));
        Assert.Equal([V(20)], Create().SavedVersions().Select(x => x.Version));
    }

    [Fact] public void ArchivedCopyStartedByItselfRestoresAndCannotUpdate()
    {
        InstallVersion(Current, V(21)); InstallVersion(V(21), V(22));
        var copy = Create(directory: Path.Combine(app, V20));
        Assert.True(copy.FromPrevious);
        Assert.Equal(Path.Combine(app, Updater.ArchiveFolder), copy.ArchivePath);
        Assert.Throws<InvalidOperationException>(() => copy.Stage(Release, Runtime, true));
        // Another archived version from the copy (the command line, under the exclusive lock): the version of the program folder,
        // asked from its executable, is archived as every restoration does (Codex audit 15.10); its variant is not known.
        Assert.Equal("0.2.0.21", copy.Rollback(Current, V(21)));
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        Assert.Equal("program 0.2.0.22", Read(Updater.ArchiveFolder, "0.2.0.22", Exe));
        Assert.False(File.Exists(Path.Combine(app, Updater.ArchiveFolder, "0.2.0.22", "variant.txt")));
        Restart();
        Assert.Equal("0.2.0.20", copy.Rollback(Current));
        Assert.Equal("old program", Read(Exe));
        Assert.Equal("old program", Read(V20, Exe));
        Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        // Registration of the copy goes to the program folder, as for .previous.
        using (Updater.Register(Path.Combine(app, V20), TimeSpan.FromSeconds(1))) Assert.Null(Create().TryExclusive());
    }

    [Fact] public void ArchiveIsNeverTrimmedAwayFromTheInstalledVersion()
    {
        InstallVersion(Current, V(21), keep: 10); InstallVersion(V(21), V(22), keep: 10); InstallVersion(V(22), V(23), keep: 10);
        Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        // Rolled back to the oldest with one kept: the installed version stays and does not count.
        Assert.Equal("0.2.0.20", Create(keep: 1).Rollback(V(23), V(20)));
        Assert.Equal(["0.2.0.23", "0.2.0.20"], Archived());
    }

    [Fact] public void FailedRemovalOfAnOldCopyDoesNotFailTheInstall()
    {
        InstallVersion(Current, V(21), keep: 10); InstallVersion(V(21), V(22), keep: 10);
        // A file of the oldest copy is held open without delete sharing: on Windows the copy cannot be renamed away.
        var held = new FileStream(Path.Combine(app, V20, Exe), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            InstallVersion(V(22), V(23), keep: 1);
            Assert.Equal("program 0.2.0.23", Read(Exe));
            Assert.Contains("0.2.0.22", Archived());
            if (OperatingSystem.IsWindows()) Assert.Equal(["0.2.0.22", "0.2.0.20"], Archived());
        }
        finally { held.Dispose(); }
        // The copy stays complete and is removed at the next operation; retired leftovers go at the next start.
        Directory.CreateDirectory(Path.Combine(app, Updater.ArchiveFolder, ".old-leftover", "Assets"));
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.DoesNotContain(".old-leftover", Archived());
        InstallVersion(V(23), V(24), keep: 1);
        Assert.Equal(["0.2.0.23"], Archived());
    }

    [Fact] public void InterruptedArchivingIsFinishedWithoutLosingAVersion()
    {
        InstallVersion(Current, V(21));
        // Interrupted after the commit, before the saved files reached the archive.
        Updater.BeforeFinish = _ => throw new IOException("power off");
        try { Assert.Throws<IOException>(() => Create().Rollback(V(21), V(20))); }
        finally { Updater.BeforeFinish = null; }
        Assert.Equal("old program", Read(Exe));
        Assert.True(Create().NeedsRecovery);
        Restart();
        Assert.Equal(0, Helper(V(20)));
        Assert.Equal("recovered", Updater.TakeResult(app));
        Assert.Equal("old program", Read(Exe));
        Assert.Equal("program 0.2.0.21", Read(V21, Exe));
        Assert.Equal(["0.2.0.21", "0.2.0.20"], Archived());
        Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
    }

    [Theory] [InlineData("retired")] [InlineData("moved")]
    public void InterruptedReplacementOfAnArchivedVersionIsFinished(string step)
    {
        InstallVersion(Current, V(21));
        // An older archived version that the setting of the operation (one kept) removes.
        var older = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.19");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(older, "version.txt"), "0.2.0.19");
        File.SetLastWriteTimeUtc(Path.Combine(older, "version.txt"), DateTime.UtcNow.AddDays(-1));
        // A second save of 0.2.0.20 (committed) interrupted inside the archive step; the staging folder holds its variant, as
        // Transact writes it before the commit, and the marker the archive size and the installed version.
        var staging = Path.Combine(app, ".previous.new");
        var target = Path.Combine(app, V20);
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, Exe), "newer copy of 0.2.0.20");
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "old icon");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.20");
        File.WriteAllText(Path.Combine(staging, "variant.txt"), "self-contained");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe, "+" + Path.Combine("Assets", "basenavigator.png")]);
        // The marker as Promote leaves it before the move: with the folder the saved files go to.
        File.WriteAllLines(Path.Combine(app, ".update-committed"), ["promote", "", "1", "0.2.0.21", "0.2.0.20"]);
        Directory.Move(target, Path.Combine(app, Updater.ArchiveFolder, ".old-interrupted"));
        // Right after Directory.Move(staging, target): Recover finds no staging folder, only the marker.
        if (step == "moved") Directory.Move(staging, target);
        Assert.Equal(0, Helper(V(21)));
        Assert.Equal("newer copy of 0.2.0.20", Read(V20, Exe));
        Assert.False(Directory.Exists(staging)); Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
        // The variant survived and the trimming was resumed in both cases.
        var saved = Assert.Single(Create().SavedVersions());
        Assert.Equal(V(20), saved.Version); Assert.False(saved.Light);
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.Equal(["0.2.0.20"], Archived());
        Assert.Equal("program 0.2.0.21", Read(Exe));
    }

    [Theory] [InlineData("incomplete")] [InlineData("running copy")]
    public void ArchivedVersionIsNotReplacedByAWorseCopy(string kind)
    {
        InstallVersion(Current, V(21));
        // A committed save of 0.2.0.20 left for Recover: without the program, or over the copy that is running now.
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(Path.Combine(staging, "Assets"));
        File.WriteAllText(Path.Combine(staging, "Assets", "basenavigator.png"), "other icon");
        if (kind == "running copy") File.WriteAllText(Path.Combine(staging, Exe), "other copy of 0.2.0.20");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.20");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), [(kind == "running copy" ? "+" : "-") + Exe, "+" + Path.Combine("Assets", "basenavigator.png")]);
        File.WriteAllLines(Path.Combine(app, ".update-committed"), ["promote", ""]);
        if (kind == "incomplete") Assert.Equal(0, Helper(V(21)));
        else Assert.Equal("0.2.0.20", Create(directory: Path.Combine(app, V20)).Rollback(V(20), V(20)));
        Assert.Equal("old program", Read(V20, Exe)); Assert.Equal("old icon", Read(V20, "Assets", "basenavigator.png"));
        Assert.False(Directory.Exists(staging)); Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
        // Incomplete: discarded. Over the running copy: kept alongside, never dropped (Codex re-check 15.10); the restoration then
        // archived the program folder (0.2.0.21).
        if (kind == "incomplete") Assert.Equal(["0.2.0.20"], Archived());
        else
        {
            Assert.Equal(["0.2.0.21", "0.2.0.20"], Archived().Where(x => !x.Contains('~')));
            var alongside = Assert.Single(Directory.GetDirectories(Path.Combine(app, Updater.ArchiveFolder), "0.2.0.20~*"));
            Assert.Equal("other copy of 0.2.0.20", File.ReadAllText(Path.Combine(alongside, Exe)));
        }
    }

    [Fact] public void FailedReplacementKeepsTheArchiveAsItWas()
    {
        InstallVersion(Current, V(21));
        // The replacement of a rollback fails half-way: the files go back, the archive does not change.
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.ReadOnly);
        else { File.Delete(icon); Directory.CreateDirectory(Path.Combine(icon, "blocker")); }
        try { Assert.ThrowsAny<Exception>(() => Create().Rollback(V(21), V(20))); }
        finally
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(icon, FileAttributes.Normal);
            else { Directory.Delete(icon, true); File.WriteAllText(icon, "icon 0.2.0.21"); }
        }
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.Equal(["0.2.0.20"], Archived());
        Assert.Equal("old program", Read(V20, Exe));
        Assert.False(Directory.Exists(Path.Combine(app, ".previous.new")));
    }

    [Fact] public void KeepVersionsSettingLoadsFromOlderProfilesAndIsChecked()
    {
        var path = Path.Combine(folder, "library.json");
        File.WriteAllText(path, "{\"UpdateMode\":1}");
        Assert.Equal(3, new LibraryStore(path).Load().KeepVersions);
        new LibraryStore(path).Save(new Library { KeepVersions = 7 });
        Assert.Equal(7, new LibraryStore(path).Load().KeepVersions);
        foreach (var bad in new[] { 0, 11, -1 })
        {
            File.WriteAllText(path, "{\"KeepVersions\":" + bad + "}");
            Assert.Throws<InvalidDataException>(() => new LibraryStore(path).Load());
        }
    }

    [Theory]
    [InlineData(new[] { "--rollback", "0.3.0.7" }, "0.3.0.7")] [InlineData(new[] { "--rollback", "--no-gui" }, null)]
    [InlineData(new[] { "--no-gui", "--rollback", "0.2.0.20" }, "0.2.0.20")] [InlineData(new[] { "--rollback" }, null)]
    public void RollbackTakesAnOptionalVersion(string[] args, string? expected)
    {
        var parsed = DiagnosticArguments.Parse(args);
        Assert.Equal(UpdateCommand.Rollback, parsed.Update);
        Assert.Equal(expected, parsed.RollbackVersion?.ToString());
    }

    [Theory] [InlineData("latest")] [InlineData("x")] [InlineData("1.2.x")]
    public void RollbackVersionMustBeAVersion(string value) =>
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--rollback", value]));

    [Fact] public void RestorationFromARunningArchivedCopyGoesThroughTheHelper()
    {
        InstallVersion(Current, V(21)); InstallVersion(V(21), V(22));
        var copy = Create(directory: Path.Combine(app, V20));
        // The copy prepares, the helper replaces once every copy has closed (Codex audit 15.10: not past the exclusive lock).
        Assert.Equal("0.2.0.21", copy.StageRollback(Current, V(21)));
        Assert.Equal("program 0.2.0.22", Read(Exe));
        // The helper asks the program folder its version itself, after it has finished any interrupted operation.
        Assert.Equal("probe", File.ReadAllLines(Path.Combine(app, Updater.PendingFolder, "update.txt"))[4]);
        using (var running = Updater.Register(Path.Combine(app, V20), TimeSpan.Zero))
            Assert.Equal(7, Updater.ApplyPending(42, app, Exe, Current, _ => false, (_, _) => { }, HelperFolder, TimeSpan.FromMilliseconds(300)));
        Assert.Equal("program 0.2.0.22", Read(Exe));
        Assert.StartsWith("error ", Updater.TakeResult(app));
        Restart();
        Assert.Equal(0, Helper(V(20)));
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        Assert.Equal("program 0.2.0.22", Read(Updater.ArchiveFolder, "0.2.0.22", Exe));
        // The copy's own variant is not the folder's: none is recorded.
        Assert.False(File.Exists(Path.Combine(app, Updater.ArchiveFolder, "0.2.0.22", "variant.txt")));
    }

    [Fact] public void ProgramFolderThatDoesNotTellItsVersionIsKeptApart()
    {
        InstallVersion(Current, V(21));
        File.WriteAllText(Path.Combine(app, Exe), "broken program");
        var copy = Create(directory: Path.Combine(app, V20));
        Assert.Equal("0.2.0.20", copy.StageRollback(Current));
        Restart();
        Assert.Equal(0, Helper(V(20)));
        Assert.Equal("old program", Read(Exe));
        // Not dropped (Grok audit 15.10): kept apart, outside the list and the trimming, and named in the result.
        var parked = Assert.Single(Directory.GetDirectories(Path.Combine(app, Updater.ArchiveFolder), ".unidentified-*"));
        Assert.Equal("broken program", File.ReadAllText(Path.Combine(parked, Exe)));
        // The explanation stays with the files (Codex re-check 15.10).
        Assert.Contains("не определилась", File.ReadAllText(Path.Combine(parked, "README.txt")));
        Assert.Equal([V(20)], Create().SavedVersions().Select(x => x.Version));
        Assert.Contains("версия не определилась", Updater.TakeResult(app));
        Updater.Cleanup(app, Path.Combine(app, Exe));
        Assert.True(Directory.Exists(parked));
        // The command line of a copy does the same.
        Restart();
        File.WriteAllText(Path.Combine(app, Exe), "another broken program");
        var direct = Create(directory: Path.Combine(app, V20));
        Assert.Equal("0.2.0.20", direct.Rollback(Current));
        Assert.Contains(".unidentified-", direct.ParkedNote);
    }

    [Fact] public void FilesLeftByAProgramRunningFromTheArchiveAreIgnored()
    {
        InstallVersion(Current, V(21)); InstallVersion(V(21), V(22), keep: 10);
        // An older build started from .versions/0.2.0.21 registers its .run.lock there, writes a log and its own .previous.
        var folder = Path.Combine(app, V21);
        File.WriteAllText(Path.Combine(folder, "debug.log"), "log");
        Directory.CreateDirectory(Path.Combine(folder, ".previous"));
        File.WriteAllText(Path.Combine(folder, ".previous", "version.txt"), "0.2.0.19");
        var held = new FileStream(Path.Combine(folder, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var saved = Create().SavedVersions().Single(x => x.Version == V(21));
            Assert.True(saved.InUse);
            Assert.False(Create().SavedVersions().Single(x => x.Version == V(20)).InUse);
            // Never trimmed while it runs (Linux would let the folder go from under it), restorable, nothing foreign copied back.
            InstallVersion(V(22), V(23), keep: 1);
            Assert.Equal(["0.2.0.22", "0.2.0.21"], Archived());
            Assert.Equal("0.2.0.21", Create().Rollback(V(23), V(21)));
            Assert.Equal("program 0.2.0.21", Read(Exe));
            Assert.False(File.Exists(Path.Combine(app, "debug.log"))); Assert.False(File.Exists(Path.Combine(app, ".run.lock.x")));
            Assert.False(Directory.Exists(Path.Combine(app, ".previous")));
        }
        finally { held.Dispose(); }
        Restart();
        // Released: trimmed at the next install like any other; the one kept is the copy that install saved (0.2.0.21).
        InstallVersion(V(21), V(24), keep: 1);
        Assert.Equal(["0.2.0.21"], Archived());
    }

    [Fact] public void RestorationMakesTheProgramFilesThoseOfTheChosenVersion()
    {
        // 0.2.0.21 brings an asset 0.2.0.20 does not have.
        var release = new ReleaseInfo(V(21), "v0.2.0.21", "");
        Package(new() { ["BaseNavigator/" + Exe] = "program 0.2.0.21", ["BaseNavigator/Assets/basenavigator.png"] = "icon 0.2.0.21",
            ["BaseNavigator/Assets/extra.png"] = "extra 0.2.0.21" }, version: V(21));
        new Updater(app, Path.Combine(app, Exe), new FolderSource(releases, release), _ => "0.2.0.21") { Keep = 3, Light = true }.Install(release, Current, Runtime, true);
        Restart();
        Assert.Equal("0.2.0.20", Create().Rollback(V(21), V(20)));
        Assert.False(File.Exists(Path.Combine(app, "Assets", "extra.png")));
        Restart();
        Assert.Equal("0.2.0.21", Create().Rollback(V(20), V(21)));
        Assert.Equal("extra 0.2.0.21", Read("Assets", "extra.png"));
        // The second save of 0.2.0.20 is as clean as the first.
        Assert.False(File.Exists(Path.Combine(app, V20, "Assets", "extra.png")));
        Assert.Equal("old icon", Read(V20, "Assets", "basenavigator.png"));
    }

    [Fact] public void SavedCopyRestoresAfterAnInterruptedOperation()
    {
        InstallVersion(Current, V(21)); InstallVersion(V(21), V(22));
        // An operation interrupted half-way (not committed): the program file already replaced, the saved 0.2.0.22 waiting.
        var staging = Path.Combine(app, ".previous.new");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, Exe), "program 0.2.0.22");
        File.WriteAllText(Path.Combine(staging, "version.txt"), "0.2.0.22");
        File.WriteAllLines(Path.Combine(staging, "files.txt"), ["+" + Exe]);
        File.WriteAllText(Path.Combine(app, Exe), "half-written");
        Assert.Throws<InvalidOperationException>(() => Create().StageRollback(V(22), V(21)));
        // The copy prepares; the helper finishes the interrupted operation first, then asks the folder its version and applies.
        Assert.Equal("0.2.0.21", Create(directory: Path.Combine(app, V20)).StageRollback(Current, V(21)));
        Restart();
        Assert.Equal(0, Helper(V(20)));
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.Equal("program 0.2.0.22", Read(Updater.ArchiveFolder, "0.2.0.22", Exe));
        Assert.False(Directory.Exists(staging));
    }

    [Fact] public void ArchivedProgramStaysExecutableOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(Path.Combine(app, Exe), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Package(Good);
        Create().Install(Release, Current, Runtime, true);
        Assert.True(File.GetUnixFileMode(Path.Combine(app, V20, Exe)).HasFlag(UnixFileMode.UserExecute));
    }

    [Theory] [InlineData("0.3.0", "0.3.0.0")] [InlineData("0.3", "0.3.0.0")] [InlineData("0.3.0.7", "0.3.0.7")]
    public void RollbackVersionGetsFourParts(string value, string expected) =>
        Assert.Equal(expected, DiagnosticArguments.Parse(["--rollback", value]).RollbackVersion?.ToString());

    [Theory] [InlineData("changed")] [InlineData("missing")] [InlineData("extra")] [InlineData("probe")]
    public void DamagedArchivedCopyIsRefusedBeforeAnythingChanges(string damage)
    {
        InstallVersion(Current, V(21));
        var copy = Path.Combine(app, V20);
        Assert.True(File.Exists(Path.Combine(copy, "inventory.txt")));
        switch (damage)
        {
            case "changed": File.WriteAllText(Path.Combine(copy, Exe), "old progran"); break;
            case "missing": File.Delete(Path.Combine(copy, "Assets", "basenavigator.png")); break;
            case "extra": File.WriteAllText(Path.Combine(copy, "Assets", "planted.png"), "x"); break;
            // A copy of an older build has no inventory: the trial run finds a program that does not start.
            default: File.Delete(Path.Combine(copy, "inventory.txt")); File.WriteAllText(Path.Combine(copy, Exe), "truncated"); break;
        }
        var error = Assert.ThrowsAny<Exception>(() => Create().Rollback(V(21), V(20)));
        Assert.Contains(damage == "probe" ? "не запустилась" : damage switch { "changed" => "изменён файл", "missing" => "нет файла", _ => "лишний файл" }, error.Message);
        Assert.Throws(error.GetType(), () => Create().StageRollback(V(21), V(20)));
        Assert.Equal("program 0.2.0.21", Read(Exe));
        Assert.False(Updater.ChangedInThisProcess);
        Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
    }

    [Fact] public void LightCopyThatDoesNotStartNamesTheRuntime()
    {
        InstallVersion(Current, V(21));
        File.Delete(Path.Combine(app, V20, "inventory.txt"));
        Assert.Contains(".NET 10 Runtime", Assert.Throws<InvalidOperationException>(() => Create(probe: null).Rollback(V(21), V(20))).Message);
        Assert.Equal("program 0.2.0.21", Read(Exe));
    }

    [Fact] public void IncompleteArchivedCopyDoesNotHideACompletePrevious()
    {
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        var broken = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.19");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "version.txt"), "0.2.0.19");
        Assert.Equal(legacy, Assert.Single(Create().SavedVersions()).Folder);
        Assert.Equal("0.2.0.19", Create().Rollback(Current, V(19)));
        Assert.Equal("program 0.2.0.19", Read(Exe));
    }

    [Fact] public void RollbackWithoutVersionGoesBackToTheOtherVariantOfTheSameVersion()
    {
        // The running 0.2.0.20 is light; the archive holds 0.2.0.20 self-contained (saved last) and 0.2.0.19.
        foreach (var (version, variant) in new[] { ("0.2.0.19", "light"), ("0.2.0.20", "self-contained") })
        {
            var folder = Path.Combine(app, Updater.ArchiveFolder, version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Exe), version == "0.2.0.20" ? "old program" : "program 0.2.0.19");
            File.WriteAllText(Path.Combine(folder, "version.txt"), version);
            File.WriteAllText(Path.Combine(folder, "variant.txt"), variant);
            Thread.Sleep(20);
        }
        Assert.True(Create(light: true).IsRunningBuild(Create().SavedVersions().First(x => x.Version == Current) with { Light = null }, Current));
        // As the window's «уже используется»: the same version of the same variant is the running build, of another one is not.
        Assert.Equal("0.2.0.19", Create(light: false).StageRollback(Current));
        Restart(); Create().DiscardPending();
        Assert.Equal("0.2.0.20", Create(light: true).Rollback(Current));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void RunningWindowsExecutableIsRenamedAsideAndPutBackOnFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        Package(Good);
        var main = Path.Combine(app, Exe);
        var seen = new List<string>();
        // The direct command line replaces the running executable: it is renamed aside first (Windows lets a running file be
        // renamed only), then the new file takes the path.
        Updater.BeforeReplace = target =>
        {
            seen.Add(File.Exists(target) ? "present" : "aside");
            seen.Add(File.ReadAllText(Assert.Single(Directory.GetFiles(app, Exe + ".old-*"))));
        };
        try { Create().Install(Release, Current, Runtime, true); }
        finally { Updater.BeforeReplace = null; }
        Assert.Equal(["aside", "old program"], seen);
        Assert.Equal("new program", Read(Exe));
        Updater.Cleanup(app, main);
        // The new file cannot take the path: the old one comes back, nothing is left aside and nothing changed.
        Restart();
        Updater.BeforeReplace = _ => throw new IOException("disk full");
        try { Assert.Throws<IOException>(() => Create().Rollback(Next)); }
        finally { Updater.BeforeReplace = null; }
        Assert.Equal("new program", File.ReadAllText(main));
        Assert.Empty(Directory.GetFiles(app, Exe + ".old*"));
        Assert.False(Updater.ChangedInThisProcess);
    }

    /// <summary>
    /// The file as a running single-file .NET program holds its own executable: mapped as data, with a handle that lets it be
    /// renamed. Windows then refuses to remove or overwrite any name of it, a second hard link included.
    /// </summary>
    private static IDisposable MapAsRunning(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        return new Mapped(map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read), map);
    }

    private sealed class Mapped(IDisposable view, IDisposable map) : IDisposable
    {
        public void Dispose() { view.Dispose(); map.Dispose(); }
    }

    [Fact] public void RunningSingleFileWindowsExecutableIsReplacedFromTheCommandLine()
    {
        if (!OperatingSystem.IsWindows()) return;
        Package(Good);
        var main = Path.Combine(app, Exe);
        // Live check 07.11: --update failed «Нет доступа к файлу или папке» and left the copy on the old version.
        using (MapAsRunning(main))
        {
            Create().Install(Release, Current, Runtime, true);
            Assert.Equal("new program", Read(Exe));
            // The running file is kept under a second name while it runs; it goes at the next start.
            Assert.Equal("old program", File.ReadAllText(Assert.Single(Directory.GetFiles(app, Exe + ".old-*"))));
        }
        Assert.Equal("0.2.0.20", Create().PreviousVersion);
        Updater.Cleanup(app, main);
        Assert.Empty(Directory.GetFiles(app, Exe + ".old*"));
        // The command-line rollback replaces the running new program the same way.
        Restart();
        using (MapAsRunning(main)) Assert.Equal("0.2.0.20", Create().Rollback(Next));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void RunningSingleFileWindowsExecutableComesBackWhenALaterFileFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        Package(Good);
        var main = Path.Combine(app, Exe);
        var icon = Path.Combine(app, "Assets", "basenavigator.png");
        string[] kept = [Exe, Path.Combine("Assets", "basenavigator.png"), Path.Combine("Settings", "library.json"), "BaseNavigator.portable"];
        var before = kept.ToDictionary(x => x, x => File.ReadAllBytes(Path.Combine(app, x)));
        var replaced = new List<string>();
        // Codex audit 08.11 (P3): the running file is already aside and the new one in place when the next file of the package
        // fails; the restore puts the old program back through the same rename aside.
        File.SetAttributes(icon, FileAttributes.ReadOnly);
        Updater.BeforeReplace = target => replaced.Add(Path.GetFileName(target));
        try
        {
            using (MapAsRunning(main))
            {
                Assert.ThrowsAny<Exception>(() => Create().Install(Release, Current, Runtime, true));
                Assert.Equal([Exe, Exe], replaced);
                Assert.All(kept, x => Assert.Equal(before[x], File.ReadAllBytes(Path.Combine(app, x))));
                Assert.False(File.Exists(Path.Combine(app, "Start-with-log.cmd")));
                Assert.False(File.Exists(Path.Combine(app, "THIRD-PARTY-NOTICES.txt")));
                Assert.False(Directory.Exists(Path.Combine(app, ".previous.new")));
                Assert.False(Directory.Exists(Path.Combine(app, Updater.ArchiveFolder)));
                Assert.False(Updater.ChangedInThisProcess);
                // The running original stays aside while it is mapped; the cleanup does not fail and removes the rest. Windows 10
                // refuses to delete the mapped file (it stays); Windows Server of GitHub Actions deletes it (09.11) — both are fine.
                Updater.Cleanup(app, main);
                Assert.All(Directory.GetFiles(app, Exe + ".old-*"), x => Assert.Equal(before[Exe], File.ReadAllBytes(x)));
                Assert.True(Directory.GetFiles(app, Exe + ".old-*").Length <= 1);
                Assert.Equal(before[Exe], File.ReadAllBytes(main));
            }
        }
        finally { Updater.BeforeReplace = null; File.SetAttributes(icon, FileAttributes.Normal); }
        Updater.Cleanup(app, main);
        Assert.Empty(Directory.GetFiles(app, Exe + ".old*"));
        Assert.All(kept, x => Assert.Equal(before[x], File.ReadAllBytes(Path.Combine(app, x))));
    }

    [Fact] public void ReadOnlyLeftoverOfTheRunningProgramIsRemoved()
    {
        var main = Path.Combine(app, Exe);
        var old = Path.Combine(app, Exe + ".old-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(old, "old program");
        // Grok audit 08.11 (P3): a read-only copy aside stayed for good and each update added one more full copy of the program.
        File.SetAttributes(old, FileAttributes.ReadOnly);
        try { Updater.Cleanup(app, main); }
        finally { if (File.Exists(old)) File.SetAttributes(old, FileAttributes.Normal); }
        Assert.False(File.Exists(old));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void HelperReplacesTheProgramFileInOneRename()
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var replaced = false;
        Updater.BeforeReplace = _ => replaced = true;
        try { Assert.Equal(0, Helper()); }
        finally { Updater.BeforeReplace = null; }
        // The helper runs from its own copy: the program file is not running, no second name and no rename first.
        Assert.False(replaced);
        Assert.Empty(Directory.GetFiles(app, Exe + ".old*"));
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void ReleaseWithoutTheWholeProgramIsRefused()
    {
        File.WriteAllText(Path.Combine(app, "Start-with-log.cmd"), "old script");
        File.WriteAllText(Path.Combine(app, "THIRD-PARTY-NOTICES.txt"), "old notices");
        // A correctly signed package with the program only: it would leave the program without its companion files.
        Package(new() { ["BaseNavigator/" + Exe] = "new program" }, bare: true);
        var error = Assert.Throws<InvalidDataException>(() => Create().Install(Release, Current, Runtime, true));
        Assert.Contains("нет файла", error.Message);
        Assert.Throws<InvalidDataException>(() => Create().Stage(Release, Runtime, true));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old script", Read("Start-with-log.cmd")); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        Assert.False(Updater.ChangedInThisProcess);
    }

    [Fact] public void CopyWithoutInventoryNeverRemovesProgramFiles()
    {
        // .previous of an older build has no inventory: it only replaces files, as before.
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        File.WriteAllText(Path.Combine(app, "Start-with-log.cmd"), "script");
        Assert.Equal("0.2.0.19", Create().Rollback(Current, V(19)));
        Assert.Equal("program 0.2.0.19", Read(Exe));
        Assert.Equal("script", Read("Start-with-log.cmd")); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
        // The same through the helper.
        Restart();
        File.Delete(Path.Combine(app, V20, "inventory.txt"));
        Assert.Equal("0.2.0.20", Create().StageRollback(V(19), V(20)));
        Assert.Equal("", File.ReadAllLines(Path.Combine(app, Updater.PendingFolder, "update.txt"))[5]);
        Restart();
        Assert.Equal(0, Helper(V(19)));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("script", Read("Start-with-log.cmd"));
    }

    [Fact] public void BusyCopyOfTheSameVersionKeepsTheNewOneAlongside()
    {
        InstallVersion(Current, V(21));
        Assert.Equal("0.2.0.20", Create().Rollback(V(21), V(20)));
        Restart();
        // An older build runs from the light copy of 0.2.0.20; the program folder now holds 0.2.0.20 self-contained.
        var held = new FileStream(Path.Combine(app, V20, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            InstallVersion(V(20), V(21), light: false);
            // Not dropped (Codex re-check 15.10): the new copy is kept beside the busy one, both offered.
            var copies = Create().SavedVersions().Where(x => x.Version == V(20)).ToList();
            Assert.Equal(2, copies.Count);
            Assert.Contains(copies, x => x.Light == false && Path.GetFileName(x.Folder).StartsWith("0.2.0.20~"));
            Assert.Contains(copies, x => x.Light == true && x.InUse);
        }
        finally { held.Dispose(); }
        Restart();
        var alongside = Create().SavedVersions().Single(x => x.Light == false && x.Version == V(20));
        // The window restores the copy it shows, by its folder.
        Assert.Equal("0.2.0.20", Create(light: true).Rollback(V(21), V(20), alongside.Folder));
        Assert.Equal("old program", Read(Exe));
        Assert.Throws<InvalidOperationException>(() => Create().StageRollback(V(20), V(20), Path.Combine(app, Updater.ArchiveFolder, "0.2.0.20~gone")));
    }

    [Fact] public void FolderTakenBetweenTheCheckAndTheRemovalIsKept()
    {
        InstallVersion(Current, V(21), keep: 10); InstallVersion(V(21), V(22), keep: 10);
        var holds = new List<FileStream>();
        var blocked = new List<string>();
        // An older build starts from the copy at the worst moment: after the check, before the rename (Codex re-check 15.10).
        Updater.BeforeRetire = folder =>
        {
            try { holds.Add(new FileStream(Path.Combine(folder, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite)); }
            catch (IOException) { blocked.Add(folder); }
        };
        try { InstallVersion(V(22), V(23), keep: 1); }
        finally { Updater.BeforeRetire = null; }
        try
        {
            Assert.Equal("program 0.2.0.23", Read(Exe));
            // Either the start could not register (Linux: the exclusive lock was still held) or the copy it took stayed (Windows).
            Assert.Equal(2, holds.Count + blocked.Count);
            Assert.All(holds, x => Assert.True(File.Exists(x.Name)));
            if (OperatingSystem.IsWindows()) Assert.Equal(["0.2.0.22", "0.2.0.21", "0.2.0.20"], Archived());
        }
        finally { holds.ForEach(x => x.Dispose()); }
    }

    [Fact] public void BusyIncompleteDestinationKeepsPreviousWhereItIs()
    {
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        var broken = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.19");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "version.txt"), "0.2.0.19");
        using (new FileStream(Path.Combine(broken, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
        {
            Package(Good);
            Create().Install(Release, Current, Runtime, true);
        }
        Assert.True(Directory.Exists(broken)); Assert.True(File.Exists(Path.Combine(legacy, Exe)));
        Assert.Contains(Create().SavedVersions(), x => x.Folder == legacy);
    }

    [Fact] public void CopyKeptAlongsideIsNotTrimmedByTheOperationThatMadeIt()
    {
        InstallVersion(Current, V(21));
        Assert.Equal("0.2.0.20", Create().Rollback(V(21), V(20)));
        Restart();
        using (new FileStream(Path.Combine(app, V20, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
        {
            // One kept: the busy light 0.2.0.20 cannot go, the self-contained 0.2.0.20 just saved beside it must not (Codex re-check 2).
            InstallVersion(V(20), V(21), keep: 1, light: false);
            var copies = Create().SavedVersions().Where(x => x.Version == V(20)).ToList();
            Assert.Contains(copies, x => x.Light == false && Path.GetFileName(x.Folder).StartsWith("0.2.0.20~"));
        }
        // Next time, with nothing in use, the order is version then time saved: the newer self-contained copy is the one kept.
        Restart();
        InstallVersion(V(21), V(22), keep: 1);
        Assert.Equal(["0.2.0.21"], Archived());
    }

    [Theory] [InlineData("Start-with-log.cmd")] [InlineData("THIRD-PARTY-NOTICES.txt")] [InlineData("icon")]
    public void PreparedReleaseThatLostARequiredFileIsRefusedByTheHelper(string lost)
    {
        File.WriteAllText(Path.Combine(app, "Start-with-log.cmd"), "old script");
        File.WriteAllText(Path.Combine(app, "THIRD-PARTY-NOTICES.txt"), "old notices");
        Package(Good);
        Create().Stage(Release, Runtime, true);
        if (lost == "Start-with-log.cmd" && !OperatingSystem.IsWindows()) return;
        File.Delete(Path.Combine(app, Updater.PendingFolder, lost == "icon" ? Path.Combine("Assets", "basenavigator.png") : lost));
        Restart();
        Assert.Equal(7, Helper());
        Assert.Contains("нет файла", Updater.TakeResult(app));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old script", Read("Start-with-log.cmd"));
        Assert.Equal("old notices", Read("THIRD-PARTY-NOTICES.txt")); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
    }

    [Theory] [InlineData("removed")] [InlineData("changed")]
    public void PreparedRestorationThatChangedIsRefusedByTheHelper(string damage)
    {
        // A companion file every platform has (THIRD-PARTY-NOTICES.txt is in Windows releases only: Linux run 15.10).
        File.WriteAllText(Path.Combine(app, "THIRD-PARTY-NOTICES.txt"), "old notices");
        InstallVersion(Current, V(21));
        Assert.Equal("0.2.0.20", Create().StageRollback(V(21), V(20)));
        var pending = Path.Combine(app, Updater.PendingFolder);
        Assert.True(File.Exists(Path.Combine(pending, "inventory.txt")));
        if (damage == "removed") File.Delete(Path.Combine(pending, "THIRD-PARTY-NOTICES.txt"));
        else File.WriteAllText(Path.Combine(pending, Exe), "old progran");
        var installed = Read("THIRD-PARTY-NOTICES.txt");
        Restart();
        Assert.Equal(7, Helper(V(21)));
        Assert.Contains("повреждено", Updater.TakeResult(app));
        Assert.Equal("program 0.2.0.21", Read(Exe)); Assert.Equal(installed, Read("THIRD-PARTY-NOTICES.txt"));
    }

    [Fact] public void PreviousTakenBetweenTheCheckAndTheMoveStaysWhereItIs()
    {
        var legacy = Path.Combine(app, Updater.PreviousFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Exe), "program 0.2.0.19");
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "0.2.0.19");
        FileStream? hold = null;
        var blocked = false;
        // A build starts from .previous after the check, before the move (Codex re-check 2).
        Updater.BeforeRetire = folder =>
        {
            if (!string.Equals(folder, legacy, StringComparison.OrdinalIgnoreCase)) return;
            try { hold = new FileStream(Path.Combine(folder, ".run.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite); }
            catch (IOException) { blocked = true; }
        };
        try { Package(Good); Create().Install(Release, Current, Runtime, true); }
        finally { Updater.BeforeRetire = null; }
        try
        {
            Assert.Equal("new program", Read(Exe));
            Assert.True(hold is not null || blocked);
            if (hold is not null) Assert.True(File.Exists(Path.Combine(legacy, Exe)));
        }
        finally { hold?.Dispose(); }
    }

    [Fact] public void ExplanationOfUnidentifiedFilesSurvivesAnInterruption()
    {
        InstallVersion(Current, V(21));
        File.WriteAllText(Path.Combine(app, Exe), "broken program");
        // Interrupted right after the files were moved into their folder (Codex re-check 2: the note was written after).
        Updater.AfterPark = _ => throw new IOException("power off");
        // The files are already replaced: the failure is left for Recover, the commit marker stays.
        try { Assert.Equal("0.2.0.20", Create(directory: Path.Combine(app, V20)).Rollback(Current)); }
        finally { Updater.AfterPark = null; }
        Assert.True(File.Exists(Path.Combine(app, ".update-committed")));
        Restart();
        Assert.Equal(0, Helper(V(20)));
        var parked = Assert.Single(Directory.GetDirectories(Path.Combine(app, Updater.ArchiveFolder), ".unidentified-*"));
        Assert.Contains("не определилась", File.ReadAllText(Path.Combine(parked, "README.txt")));
        Assert.Equal("broken program", File.ReadAllText(Path.Combine(parked, Exe)));
        Assert.Equal("old program", Read(Exe));
        Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
    }

    [Fact] public void ResumedTrimmingKeepsTheCopyNamedInTheMarker()
    {
        InstallVersion(Current, V(21));
        // A committed operation moved its saved 0.2.0.20 (self-contained) beside the busy light copy, then was interrupted. The busy
        // copy's time is later (a clock correction): only the name in the marker tells which copy the operation made.
        var alongside = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.20~deadbeef");
        Directory.CreateDirectory(alongside);
        File.WriteAllText(Path.Combine(alongside, Exe), "old program");
        File.WriteAllText(Path.Combine(alongside, "version.txt"), "0.2.0.20");
        File.WriteAllText(Path.Combine(alongside, "variant.txt"), "self-contained");
        File.SetLastWriteTimeUtc(Path.Combine(app, V20, "version.txt"), DateTime.UtcNow.AddDays(1));
        File.SetLastWriteTimeUtc(Path.Combine(alongside, "version.txt"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllLines(Path.Combine(app, ".update-committed"), ["promote", "", "1", "0.2.0.21", "0.2.0.20~deadbeef"]);
        Restart();
        Assert.Equal(0, Helper(V(21)));
        Assert.True(Directory.Exists(alongside));
        Assert.False(Directory.Exists(Path.Combine(app, V20)));
        Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
    }

    [Fact] public void PromotionRecordsItsFolderBeforeTheMove()
    {
        InstallVersion(Current, V(21));
        string[]? seen = null;
        var movedYet = true;
        Updater.AfterRecord = lines => { seen = lines; movedYet = Directory.Exists(Path.Combine(app, V21)); };
        try { Assert.Equal("0.2.0.20", Create().Rollback(V(21), V(20))); }
        finally { Updater.AfterRecord = null; }
        // The marker named the folder of the saved 0.2.0.21 while the files were still in the transaction folder.
        Assert.Equal("0.2.0.21", seen![4]);
        Assert.False(movedYet);
        Assert.True(Directory.Exists(Path.Combine(app, V21)));
    }

    [Theory] [InlineData("truncated")] [InlineData("probe")]
    public void PreparedReleaseWhoseProgramChangedIsRefusedByTheHelper(string damage)
    {
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var pending = Path.Combine(app, Updater.PendingFolder);
        Assert.True(File.Exists(Path.Combine(pending, "inventory.txt")));
        if (damage == "truncated") File.WriteAllText(Path.Combine(pending, Exe), "new prog");
        else
        {
            // A package of an older build: no inventory; the trial run still catches a program that does not start.
            File.Delete(Path.Combine(pending, "inventory.txt"));
            var info = File.ReadAllLines(Path.Combine(pending, "update.txt")).Take(2).ToArray();
            File.WriteAllLines(Path.Combine(pending, "update.txt"), info);
            File.WriteAllText(Path.Combine(pending, Exe), "truncated");
        }
        Restart();
        Assert.Equal(7, Helper());
        Assert.Contains(damage == "truncated" ? "изменён файл" : "не запустилась", Updater.TakeResult(app));
        Assert.Equal("old program", Read(Exe)); Assert.Equal("old icon", Read("Assets", "basenavigator.png"));
    }

    [Fact] public void ResumedTrimmingWithoutTheFolderInTheMarkerTrimsNothing()
    {
        InstallVersion(Current, V(21));
        // As ResumedTrimmingKeepsTheCopyNamedInTheMarker, but the marker has no fifth line (written before the folder was recorded).
        var alongside = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.20~deadbeef");
        Directory.CreateDirectory(alongside);
        File.WriteAllText(Path.Combine(alongside, Exe), "old program");
        File.WriteAllText(Path.Combine(alongside, "version.txt"), "0.2.0.20");
        File.SetLastWriteTimeUtc(Path.Combine(app, V20, "version.txt"), DateTime.UtcNow.AddDays(1));
        File.SetLastWriteTimeUtc(Path.Combine(alongside, "version.txt"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllLines(Path.Combine(app, ".update-committed"), ["promote", "", "1", "0.2.0.21"]);
        Restart();
        Assert.Equal(0, Helper(V(21)));
        // Nothing trimmed by this recovery (Codex re-check 4): both copies stay until the next operation.
        Assert.True(Directory.Exists(alongside)); Assert.True(Directory.Exists(Path.Combine(app, V20)));
        Assert.False(File.Exists(Path.Combine(app, ".update-committed")));
    }

    [Fact] public void WholePackageWithoutInventoryIsAppliedAsReplacementOnly()
    {
        File.WriteAllText(Path.Combine(app, "Assets", "extra.png"), "old extra");
        Package(Good);
        Create().Stage(Release, Runtime, true);
        var pending = Path.Combine(app, Updater.PendingFolder);
        // The six-line format of an intermediate build of this step: flagged «release», no inventory.
        File.Delete(Path.Combine(pending, "inventory.txt"));
        Assert.Equal("release", File.ReadAllLines(Path.Combine(pending, "update.txt"))[5]);
        Restart();
        Assert.Equal(0, Helper());
        Assert.Equal("new program", Read(Exe));
        // Replacement only: a program file the package lacks stays.
        Assert.Equal("old extra", Read("Assets", "extra.png"));
        // A restoration package without its inventory likewise; the trial run still guards both.
        Restart();
        Assert.Equal("0.2.0.20", Create().StageRollback(Next, V(20)));
        File.Delete(Path.Combine(pending, "inventory.txt"));
        File.WriteAllText(Path.Combine(pending, Exe), "truncated");
        Restart();
        Assert.Equal(7, Helper(Next));
        Assert.Contains("не запустилась", Updater.TakeResult(app));
        Assert.Equal("new program", Read(Exe));
    }

    [Fact] public void ChosenVersionWithTwoCopiesPrefersItsMainCopy()
    {
        InstallVersion(Current, V(21));
        // A copy kept alongside, saved later; the choice must not follow the order the system lists folders in (Linux run 15.10).
        var alongside = Path.Combine(app, Updater.ArchiveFolder, "0.2.0.20~0a1b2c3d");
        Directory.CreateDirectory(alongside);
        File.WriteAllText(Path.Combine(alongside, Exe), "program 0.2.0.20");
        File.WriteAllText(Path.Combine(alongside, "version.txt"), "0.2.0.20");
        File.SetLastWriteTimeUtc(Path.Combine(alongside, "version.txt"), DateTime.UtcNow.AddDays(1));
        Assert.Equal("0.2.0.20", Create().Rollback(V(21), V(20)));
        Assert.Equal("old program", Read(Exe));
    }

    // The indicator of the download (user's decision 25.10): sizes from gh release view, the progress by polling the download folder.
    /// <summary>A source writing each file in parts with pauses, as gh on a slow line; it stops when cancelled.</summary>
    private sealed class SlowSource(string folder, ReleaseInfo release, int parts, int pause) : IReleaseSource
    {
        public ReleaseInfo Latest() => release;
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory) => throw new NotSupportedException();
        public void Download(ReleaseInfo release, IReadOnlyList<string> assets, string directory, CancellationToken cancel)
        {
            foreach (var asset in assets)
            {
                var bytes = File.ReadAllBytes(Path.Combine(folder, asset));
                using var target = File.Create(Path.Combine(directory, asset));
                for (var i = 0; i < parts; i++)
                {
                    var from = bytes.Length * i / parts; var to = bytes.Length * (i + 1) / parts;
                    target.Write(bytes, from, to - from); target.Flush();
                    if (cancel.WaitHandle.WaitOne(pause)) cancel.ThrowIfCancellationRequested();
                }
            }
        }
    }

    /// <summary>Takes the reports on the worker thread, in order; may cancel at the first report of a stage.</summary>
    private sealed class Recorder(CancellationTokenSource? cancel = null, UpdateStage? at = null) : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];
        public void Report(UpdateProgress value)
        {
            lock (Reports) Reports.Add(value);
            if (value.Stage == at) cancel?.Cancel();
        }
    }

    private ReleaseInfo WithSizes(bool package = true)
    {
        var asset = Updater.AssetName(Next, Runtime, true);
        var sizes = new Dictionary<string, long> { ["SHA256SUMS.txt"] = new FileInfo(Path.Combine(releases, "SHA256SUMS.txt")).Length };
        if (package) sizes[asset] = new FileInfo(Path.Combine(releases, asset)).Length;
        return Release with { AssetSizes = sizes };
    }

    [Fact] public void ProgressFollowsTheDownloadThenTheStagesInOrder()
    {
        // Big enough to be written in visible parts.
        Package(new(Good) { ["BaseNavigator/Assets/filler.bin"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(300_000)) });
        var release = WithSizes();
        var interval = Updater.ProgressInterval; Updater.ProgressInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            var updater = new Updater(app, Path.Combine(app, Exe), new SlowSource(releases, release, 5, 150), FakeProbe) { Keep = 3, Light = true };
            var recorder = new Recorder();
            var staged = updater.StageAndLaunch(release, Current, Runtime, true, [], recorder);
            var downloads = recorder.Reports.TakeWhile(x => x.Stage == UpdateStage.Downloading).ToList();
            Assert.True(downloads.Count >= 4, string.Join(", ", downloads.Select(x => x.Received)));
            Assert.Equal(0, downloads[0].Received);
            Assert.All(downloads, x => Assert.Equal(release.AssetSizes!.Values.Sum(), x.Total));
            Assert.Equal(downloads.Select(x => x.Received).Order(), downloads.Select(x => x.Received));
            Assert.Contains(downloads, x => x.Fraction is > 0 and < 1);
            Assert.Equal(1, downloads[^1].Fraction);
            Assert.Equal([UpdateStage.Verifying, UpdateStage.Extracting, UpdateStage.TrialRun], recorder.Reports.Skip(downloads.Count).Select(x => x.Stage));
            Assert.Equal(Next, staged.Version);
            Assert.Equal("0.2.0.21", updater.PendingVersion);
        }
        finally { Updater.ProgressInterval = interval; }
    }

    [Fact] public void WithoutTheSizeOfThePackageTheProgressIsIndeterminate()
    {
        Package(Good);
        foreach (var release in new[] { Release, WithSizes(package: false) })
        {
            Restart(); Create().DiscardPending();
            var recorder = new Recorder();
            new Updater(app, Path.Combine(app, Exe), new SlowSource(releases, release, 2, 60), FakeProbe) { Keep = 3, Light = true }.Stage(release, Runtime, true, recorder);
            Assert.All(recorder.Reports, x => { Assert.Null(x.Total); Assert.Null(x.Fraction); });
            Assert.Equal(UpdateStage.TrialRun, recorder.Reports[^1].Stage);
        }
    }

    [Theory]
    [InlineData(UpdateStage.Downloading)]
    [InlineData(UpdateStage.Verifying)]
    [InlineData(UpdateStage.Extracting)]
    [InlineData(UpdateStage.TrialRun)]
    public void CancelledPreparationLeavesNothing(UpdateStage at)
    {
        Package(Good);
        using var cancel = new CancellationTokenSource();
        var updater = Create();
        Assert.ThrowsAny<OperationCanceledException>(() => updater.StageAndLaunch(Release, Current, Runtime, true, [], new Recorder(cancel, at), cancel.Token));
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
        Assert.Null(updater.PendingVersion);
        Assert.False(Updater.ChangedInThisProcess);
        Assert.Equal("old program", Read(Exe));
        // Nothing stands in the way of the next attempt.
        updater.Stage(Release, Runtime, true);
        Assert.Equal("0.2.0.21", updater.PendingVersion);
    }

    [Fact] public void CancelStopsASlowDownloadMidway()
    {
        Package(new(Good) { ["BaseNavigator/Assets/filler.bin"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100_000)) });
        using var cancel = new CancellationTokenSource();
        var updater = new Updater(app, Path.Combine(app, Exe), new SlowSource(releases, Release, 50, 100), FakeProbe) { Keep = 3, Light = true };
        cancel.CancelAfter(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => updater.Stage(Release, Runtime, true, null, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), watch.Elapsed.ToString());
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
        Assert.Null(updater.PendingVersion);
    }

    /// <summary>A gh of one process that never answers (no child: a loop of the shell itself).</summary>
    private string SilentGh()
    {
        var gh = Path.Combine(folder, OperatingSystem.IsWindows() ? "gh.cmd" : "gh");
        File.WriteAllText(gh, OperatingSystem.IsWindows() ? "@echo off\r\n:loop\r\ngoto loop\r\n" : "#!/bin/sh\nwhile :; do :; done\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(gh, (UnixFileMode)0b111_101_101);
        return gh;
    }

    // Codex re-check 7, RC7-1: the cancel ends the root gh (its own process, not its tree) and returns at once.
    [Fact] public void GitHubCliIsStoppedWhenCancelled()
    {
        var source = new GitHubReleaseSource("owner/repo", SilentGh());
        var called = false;
        var kill = GitHubReleaseSource.KillRoot;
        GitHubReleaseSource.KillRoot = p => { called = true; return kill(p); };
        try
        {
            using var cancel = new CancellationTokenSource();
            cancel.CancelAfter(500);
            var stopped = Stopwatch(() => Assert.ThrowsAny<OperationCanceledException>(() => source.Download(Release, ["a.zip"], folder, cancel.Token)));
            Assert.True(stopped < TimeSpan.FromSeconds(20), stopped.ToString());
            Assert.True(called);
            // Already cancelled: gh is not started.
            Assert.ThrowsAny<OperationCanceledException>(() => new GitHubReleaseSource(gh: Path.Combine(folder, "no-gh")).Download(Release, ["a.zip"], folder, cancel.Token));
        }
        finally { GitHubReleaseSource.KillRoot = kill; }
    }

    // RC7-3: stopping that throws (an aggregate of a partial failure) still checks the exit of the root and closes the pipes.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailingStopIsCheckedByTheExitOfTheRoot(bool rootEnds)
    {
        var updater = new Updater(app, Path.Combine(app, Exe), new GitHubReleaseSource("owner/repo", SilentGh()), FakeProbe) { Keep = 3, Light = true };
        OwnedProcess? root = null;
        var (kill, wait) = (GitHubReleaseSource.KillRoot, GitHubReleaseSource.StopWait);
        GitHubReleaseSource.KillRoot = p =>
        {
            // An identity of the test's own (the program disposes its object), taken while the program's one is in use.
            root = p.Twin();
            if (rootEnds) p.Kill();
            throw new AggregateException(new System.ComponentModel.Win32Exception(5), new InvalidOperationException("partial"));
        };
        GitHubReleaseSource.StopWait = TimeSpan.FromSeconds(1);
        Exception? bodyFailure = null;
        try
        {
            using var cancel = new CancellationTokenSource();
            cancel.CancelAfter(500);
            Exception? failure = null;
            var elapsed = Stopwatch(() => failure = Record.Exception(() => updater.Stage(Release, Runtime, true, null, cancel.Token)));
            Assert.True(elapsed < TimeSpan.FromSeconds(15), elapsed.ToString());
            Assert.NotNull(root);
            if (rootEnds) Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else
            {
                // Not a successful cancel: the root still runs and says so.
                Assert.Contains("Не удалось остановить GitHub CLI", Assert.IsType<InvalidOperationException>(failure).Message);
                Assert.NotEqual(true, root!.HasExited);
            }
            Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
            Assert.Null(updater.PendingVersion);
        }
        catch (Exception ex) { bodyFailure = ex; throw; }
        finally
        {
            Cleanup(bodyFailure,
                () => (GitHubReleaseSource.KillRoot, GitHubReleaseSource.StopWait) = (kill, wait),
                () => { if (root is not null && !End(root)) throw new TimeoutException("the root gh did not end"); });
        }
    }

    [Fact] public void ReleaseSizesAreReadWhenGiven()
    {
        var release = GitHubReleaseSource.ParseLatest("{\"tagName\":\"v0.2.0.21\",\"body\":\"\",\"assets\":[{\"name\":\"a.zip\",\"size\":50266112,\"url\":\"x\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"size\":180},{\"name\":\"text.zip\",\"size\":\"12\"},{\"name\":\"negative.zip\",\"size\":-1},{\"name\":\"zero.zip\",\"size\":0}," +
            "{\"name\":\"half.zip\",\"size\":1.5},{\"size\":5},{\"name\":\"\",\"size\":5},{\"name\":7,\"size\":5},7]}");
        Assert.Equal(new Dictionary<string, long> { ["a.zip"] = 50266112, ["SHA256SUMS.txt"] = 180 }, release.AssetSizes);
        // Answers of older calls (and the e2e stub of 0.3.0.8) have no assets: the size stays unknown.
        Assert.Null(GitHubReleaseSource.ParseLatest("{\"tagName\":\"v1.0\"}").AssetSizes);
        Assert.Null(GitHubReleaseSource.ParseLatest("{\"tagName\":\"v1.0\",\"assets\":null}").AssetSizes);
        Assert.Null(GitHubReleaseSource.ParseLatest("{\"tagName\":\"v1.0\",\"assets\":[{\"name\":\"a.zip\",\"size\":\"big\"}]}").AssetSizes);
    }

    /// <summary>GitHub over a substituted HTTP: the API answer of the fixture release (real digests, unless told), its files
    /// behind a redirect to the storage host.</summary>
    /// <param name="failing">A file the download address answers with HTTP 500.</param>
    /// <param name="requests">Collects the addresses asked, in order.</param>
    private GitHubHttpReleaseSource Https(Func<string, string?>? digest = null, string? failing = null, List<string>? requests = null)
    {
        const string Url = "https://github.com/owner/repo/releases/download/v0.2.0.21/";
        var assets = Directory.GetFiles(releases).Select(x =>
        {
            var name = Path.GetFileName(x);
            var sum = digest is null ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))).ToLowerInvariant() : digest(name);
            return $"{{\"name\":\"{name}\",\"size\":{new FileInfo(x).Length},\"browser_download_url\":\"{Url}{name}\",\"digest\":{(sum is null ? "null" : $"\"sha256:{sum}\"")}}}";
        });
        var api = $"{{\"tag_name\":\"v{Next}\",\"body\":\"notes\",\"assets\":[{string.Join(",", assets)}]}}";
        return new("owner/repo", HttpReleaseSourceTests.FakeHandler.Of(r => { requests?.Add(r.RequestUri!.ToString()); return r.RequestUri!.Host switch
        {
            "api.github.com" => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(api) },
            "github.com" when r.RequestUri.Segments[^1] == failing => new(System.Net.HttpStatusCode.InternalServerError),
            "github.com" => new(System.Net.HttpStatusCode.Found) { Headers = { Location = new("https://release-assets.githubusercontent.com/" + r.RequestUri.Segments[^1]) } },
            _ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(releases, r.RequestUri.Segments[^1]))) },
        }; }));
    }

    // The update without gh (07.11): the check and the preparation up to the trial run, over HTTPS only.
    [Fact] public void AReleaseIsCheckedAndPreparedOverHttps()
    {
        Package(Good);
        var updater = new Updater(app, Path.Combine(app, Exe), new FallbackReleaseSource(Https(), new GitHubReleaseSource("owner/repo", Path.Combine(folder, "no-gh"))), FakeProbe)
        { Keep = 3, Light = true };
        var latest = updater.Latest();
        Assert.Equal(Next, latest.Version);
        var recorder = new Recorder();
        updater.Stage(latest, Runtime, true, recorder);
        Assert.Equal("0.2.0.21", updater.PendingVersion);
        Assert.Equal("new program", File.ReadAllText(Path.Combine(app, Updater.PendingFolder, Exe)));
        Assert.Equal([UpdateStage.Downloading, UpdateStage.Verifying, UpdateStage.Extracting, UpdateStage.TrialRun], recorder.Reports.Select(x => x.Stage).Distinct());
        Assert.Equal(DownloadSizeOf(latest), recorder.Reports.Last(x => x.Stage == UpdateStage.Downloading).Received);
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
    }

    // Codex audit 07.11 (optional): HTTPS brings the package but fails on SHA256SUMS.txt; the fallback gh writes both files
    // again and the preparation goes on to the trial run.
    [Fact] public void AnHttpsDownloadFailingMidwayIsCompletedThroughGh()
    {
        var asset = Package(Good);
        var calls = Path.Combine(folder, "gh-calls.txt");
        var gh = Path.Combine(folder, OperatingSystem.IsWindows() ? "gh.cmd" : "gh");
        // The download arguments: release download <tag> --repo <repo> --dir <dir> --clobber --pattern …; the 7th is the folder.
        // Codex re-check 07.11 (P3): gh starts only after HTTPS brought the package and not the checksums, and it overwrites
        // only when asked to (--clobber); otherwise it fails and so does the staging.
        File.WriteAllText(gh, OperatingSystem.IsWindows()
            ? $"@echo off\r\n>>\"{calls}\" echo %*\r\n" +
              $"if not exist \"%~7\\{asset}\" (echo no package 1>&2& exit /b 3)\r\n" +
              "if exist \"%~7\\SHA256SUMS.txt\" (echo checksums present 1>&2& exit /b 3)\r\n" +
              "if not \"%8\"==\"--clobber\" (echo no clobber 1>&2& exit /b 3)\r\n" +
              $"copy /y \"{releases}\\*\" \"%~7\" >nul\r\n"
            : $"#!/bin/sh\necho \"$*\" >> '{calls}'\n" +
              $"[ -f \"$7/{asset}\" ] || {{ echo no package >&2; exit 3; }}\n" +
              "[ ! -e \"$7/SHA256SUMS.txt\" ] || { echo checksums present >&2; exit 3; }\n" +
              "[ \"$8\" = --clobber ] || { echo no clobber >&2; exit 3; }\n" +
              $"cp '{releases}'/* \"$7\"/\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(gh, (UnixFileMode)0b111_101_101);
        // The guard of the fake itself: HTTPS failing on the package already sends gh to an empty folder, which it refuses.
        var early = new Updater(app, Path.Combine(app, Exe), new FallbackReleaseSource(Https(failing: asset), new GitHubReleaseSource("owner/repo", gh)), FakeProbe)
        { Keep = 3, Light = true };
        Assert.Contains("no package", Assert.Throws<InvalidOperationException>(() => early.Stage(early.Latest(), Runtime, true)).Message);
        File.Delete(calls);
        var requests = new List<string>();
        var https = Https(failing: "SHA256SUMS.txt", requests: requests);
        var updater = new Updater(app, Path.Combine(app, Exe), new FallbackReleaseSource(https, new GitHubReleaseSource("owner/repo", gh)), FakeProbe)
        { Keep = 3, Light = true };
        var latest = updater.Latest();
        updater.Stage(latest, Runtime, true);
        // Over HTTPS: the package came whole from the storage host, then the checksums failed.
        Assert.Equal(["api.github.com/" + "repos/owner/repo/releases/latest", "github.com/" + asset, "release-assets.githubusercontent.com/" + asset,
            "github.com/SHA256SUMS.txt"], requests.Select(x => new Uri(x)).Select(x => x.Host + "/" + (x.Host == "api.github.com" ? x.AbsolutePath[1..] : x.Segments[^1])));
        Assert.Equal("0.2.0.21", updater.PendingVersion);
        Assert.Equal("new program", File.ReadAllText(Path.Combine(app, Updater.PendingFolder, Exe)));
        Assert.StartsWith($"release download v{Next} --repo owner/repo", Assert.Single(File.ReadAllLines(calls)));
        Assert.Contains(asset, File.ReadAllText(calls));
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
    }

    private long DownloadSizeOf(ReleaseInfo release) => release.AssetSizes!.Values.Sum();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFileNotMatchingTheGitHubDigestIsNotInstalled(bool package)
    {
        var asset = Package(Good);
        var wrong = new string('0', 64);
        var source = Https(name => (name == asset) == package ? wrong : null);
        var updater = new Updater(app, Path.Combine(app, Exe), source, FakeProbe) { Keep = 3, Light = true };
        var error = Assert.Throws<InvalidDataException>(() => updater.Stage(source.Latest(), Runtime, true));
        Assert.Contains($"Файл {(package ? asset : "SHA256SUMS.txt")} не совпадает с контрольной суммой GitHub", error.Message);
        Assert.Null(updater.PendingVersion);
        Assert.Empty(Directory.GetDirectories(app, ".update-*"));
        Assert.Equal("old program", Read(Exe));
    }

    [Fact] public void ProgressTextsNameTheStage()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new("ru-RU");
        try
        {
            const long Mb = 1048576;
            var half = new UpdateProgress(UpdateStage.Downloading, 123 * Mb / 10, 479 * Mb / 10);
            Assert.Equal("Загрузка обновления… 12,3 из 47,9 МБ", half.Text);
            Assert.Equal("Загрузка обновления… 25 %", half.Percent);
            var over = new UpdateProgress(UpdateStage.Downloading, 60 * Mb, 479 * Mb / 10);
            Assert.Equal(1, over.Fraction); Assert.Equal("Загрузка обновления… 47,9 из 47,9 МБ", over.Text);
            Assert.Equal("Загрузка обновления…", new UpdateProgress(UpdateStage.Downloading).Text);
            var unknown = new UpdateProgress(UpdateStage.Downloading, Mb, 0);
            Assert.Null(unknown.Fraction); Assert.Equal("Загрузка обновления… 1,0 МБ", unknown.Text); Assert.Equal(unknown.Text, unknown.Percent);
            Assert.Equal("Проверка контрольной суммы…", new UpdateProgress(UpdateStage.Verifying, 5, 10).Percent);
            Assert.Null(new UpdateProgress(UpdateStage.Verifying, 5, 10).Fraction);
            Assert.Equal("Распаковка…", new UpdateProgress(UpdateStage.Extracting).Text);
            Assert.Equal("Пробный запуск…", new UpdateProgress(UpdateStage.TrialRun).Text);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    // Codex re-check 5, RC5-3 (rewritten after re-checks 6–8): a gh whose output a helper keeps open after gh has exited; cancelled
    // after that exit, the download returns in bounded time and nothing stays. The program ends no process but the root gh.
    // The helper (RC8-2, RC8-3): Windows — started by the gh stub, it publishes its own number and waits for a release file, so it
    // is alive while the test opens a handle to it; that handle is taken first, then the name and start time checked, and only a
    // checked helper is ever ended. Linux — the test starts the helper itself (its own child, ended through OwnedProcess); the
    // helper takes gh's output through /proc/<gh>/fd/1. In finally the release file is written (a helper never identified ends by
    // itself), the staging is cancelled and its end awaited.
    [Fact] public void CancelAfterGhExitedDoesNotWaitForADescendantHoldingItsOutput()
    {
        var gh = Path.Combine(folder, OperatingSystem.IsWindows() ? "gh.cmd" : "gh");
        var pidFile = Path.Combine(folder, "helper.pid"); var ghPid = Path.Combine(folder, "gh.pid");
        var held = Path.Combine(folder, "held"); var release = Path.Combine(folder, "release");
        System.Diagnostics.Process? helperWindows = null;
        OwnedProcess? helperLinux = null;
        System.Diagnostics.Process? ownChild = null;
        Task? run = null;
        Exception? failure = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            if (OperatingSystem.IsWindows())
                File.WriteAllText(gh, "@echo off\r\nstart \"\" /b powershell -NoProfile -NonInteractive -Command \"Set-Content -Encoding ascii -LiteralPath '" +
                    pidFile + ".tmp' -Value $PID; Move-Item -LiteralPath '" + pidFile + ".tmp' -Destination '" + pidFile + "'; " +
                    "$end = (Get-Date).AddSeconds(120); while (-not (Test-Path -LiteralPath '" + release + "') -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 100 }\"\r\n" +
                    "set n=0\r\n:wait\r\nif exist \"" + pidFile + "\" goto done\r\nset /a n+=1\r\nif %n% lss 2000000 goto wait\r\n:done\r\n");
            else
            {
                File.WriteAllText(gh, $"#!/bin/sh\necho $$ > '{ghPid}.tmp' && mv '{ghPid}.tmp' '{ghPid}'\n" +
                    $"i=0; while [ ! -e '{held}' ] && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done\nexit 0\n");
                File.SetUnixFileMode(gh, (UnixFileMode)0b111_101_101);
                // The test's own helper: it waits for gh's number, opens gh's output and holds it until the release file.
                var script = $"i=0; while [ ! -e '{ghPid}' ] && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done; [ -e '{ghPid}' ] || exit 1; " +
                    $"exec 3>\"/proc/$(cat '{ghPid}')/fd/1\" || exit 1; touch '{held}'; " +
                    $"i=0; while [ ! -e '{release}' ] && [ $i -lt 1200 ]; do sleep 0.1; i=$((i+1)); done";
                ownChild = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, UseShellExecute = false })!;
                helperLinux = OwnedProcess.Of(ownChild);
            }
            var updater = new Updater(app, Path.Combine(app, Exe), new GitHubReleaseSource("owner/repo", gh), FakeProbe) { Keep = 3, Light = true };
            var started = DateTime.Now;
            run = Task.Run(() => updater.Stage(Release, Runtime, true, null, cancel.Token));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (OperatingSystem.IsWindows())
                while (helperWindows is null && watch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                    {
                        // The candidate stays apart from the checked helper; the handle comes first (the helper waits, alive).
                        var candidate = System.Diagnostics.Process.GetProcessById(pid);
                        _ = candidate.SafeHandle;
                        if (candidate.ProcessName == "powershell" && candidate.StartTime >= started.AddSeconds(-1)) helperWindows = candidate;
                        else { candidate.Dispose(); break; }
                    }
                    else Thread.Sleep(100);
                }
            else
                while (!File.Exists(held) && watch.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(100);
            Assert.True(helperWindows is not null || File.Exists(held), "the helper must hold the output");
            Thread.Sleep(1000);  // the root has exited by now; the output stays open (the helper holds it)
            Assert.False(run.IsCompleted, "the download must still wait for the output");
            cancel.Cancel();
            var stopped = Stopwatch(() => Assert.ThrowsAny<OperationCanceledException>(() => run.GetAwaiter().GetResult()));
            Assert.True(stopped < TimeSpan.FromSeconds(15), stopped.ToString());
            Assert.False(helperWindows?.HasExited ?? ownChild!.HasExited, "the program must not end a process other than gh");
            Assert.Empty(Directory.GetDirectories(app, ".update-*"));
            Assert.False(Directory.Exists(Path.Combine(app, Updater.PendingFolder)));
            Assert.Null(updater.PendingVersion);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            Cleanup(failure,
                () => File.WriteAllText(release, ""),
                cancel.Cancel,
                () =>
                {
                    if (run is null) return;
                    try { if (!run.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("the staging is still running"); }
                    catch (AggregateException) { }  // cancelled or failed: it has ended
                },
                () =>
                {
                    if (helperWindows is null || helperWindows.WaitForExit(5000)) return;
                    helperWindows.Kill();
                    if (!helperWindows.WaitForExit(5000)) throw new TimeoutException("the Windows helper did not end");
                },
                () => helperWindows?.Dispose(),
                () =>
                {
                    if (helperLinux is null || ownChild is null || ownChild.WaitForExit(5000)) return;
                    if (!End(helperLinux)) throw new TimeoutException("the Linux helper did not end");
                },
                () => ownChild?.Dispose());
        }
    }

    /// <summary>RC9-1 (07.11): every cleanup step runs even if an earlier one threw. Any failure (a staging or a process whose end
    /// is not confirmed) fails the test together with the original failure, and the temporary folder they may still use is kept
    /// instead of being deleted under them; its path is in the message.</summary>
    private void Cleanup(Exception? failure, params Action[] steps)
    {
        var errors = new List<Exception>();
        foreach (var step in steps) { try { step(); } catch (Exception ex) { errors.Add(ex); } }
        if (errors.Count == 0) return;
        keepFolder = true;
        throw new AggregateException($"Cleanup incomplete, the temporary folder is kept: {folder}", failure is null ? errors : [failure, .. errors]);
    }

    [Fact] public void AnIncompleteCleanupRunsEveryStepAndKeepsTheFolder()
    {
        var ran = new List<int>(); var body = new InvalidOperationException("body");
        var error = Assert.Throws<AggregateException>(() => Cleanup(body,
            () => throw new IOException("release file"), () => ran.Add(2), () => throw new TimeoutException("still running"), () => ran.Add(4)));
        Assert.Equal([2, 4], ran);
        Assert.Contains(folder, error.Message);
        Assert.Equal([typeof(InvalidOperationException), typeof(IOException), typeof(TimeoutException)], error.InnerExceptions.Select(x => x.GetType()));
        Assert.True(keepFolder);
        Cleanup(null, () => ran.Add(5));  // a complete cleanup throws nothing
        Assert.Equal([2, 4, 5], ran);
        keepFolder = false;  // nothing of this test runs on: the fixture removes its folder
    }

    /// <summary>Ends a process of the test's own identity and waits (bounded) until it is gone; false if it is not.</summary>
    private static bool End(OwnedProcess process)
    {
        for (var i = 0; i < 50; i++)
        {
            if (process.HasExited == true || process.Kill() == OwnedProcess.Outcome.Exited) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static TimeSpan Stopwatch(Action action)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }
}
