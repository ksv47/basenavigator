using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class SettingsStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "storage-mode-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStorage storage;

    public SettingsStorageTests()
    {
        storage = new(Path.Combine(root, "app"), Path.Combine(root, "user"));
        Directory.CreateDirectory(storage.ApplicationDirectory);
    }

    [Fact] public void NormalIsDefaultAndResolutionDoesNotCreateData()
    {
        Assert.False(storage.ReadPortable());
        Assert.Equal(Path.Combine(root, "user", "library.json"), storage.SettingsPath);
        Assert.False(Directory.Exists(storage.NormalDirectory));
        Assert.False(Directory.Exists(storage.PortableDirectory));
    }

    [Fact] public void SwitchingBackRestoresEachIndependentProfileWithoutMigration()
    {
        var normal = storage.SettingsPath;
        new LibraryStore(normal).Save(new Library { Executable = "normal" });
        var before = File.ReadAllBytes(normal);
        storage.SaveMode(true, false);
        var portable = storage.SettingsPath;
        Assert.Equal(Path.Combine(storage.PortableDirectory, "library.json"), portable);
        Assert.Empty(new LibraryStore(portable).Load().Bases);
        Assert.False(File.Exists(portable));
        new LibraryStore(portable).Save(new Library { Executable = "portable" });
        storage.SaveMode(false, true);
        Assert.Equal(normal, storage.SettingsPath);
        Assert.Equal(before, File.ReadAllBytes(normal));
        storage.SaveMode(true, false);
        Assert.Equal("portable", new LibraryStore(storage.SettingsPath).Load().Executable);
    }

    [Fact] public void StaleChoiceDoesNotOverwriteAnotherInstancesMode()
    {
        storage.SaveMode(true, false);
        Assert.Throws<IOException>(() => storage.SaveMode(false, false));
        Assert.True(storage.ReadPortable());
    }

    [Fact] public void DestinationFailureKeepsPreviousMode()
    {
        File.WriteAllText(storage.PortableDirectory, "occupied");
        Assert.ThrowsAny<IOException>(() => storage.SaveMode(true, false));
        Assert.False(storage.ReadPortable());
        Assert.Equal("occupied", File.ReadAllText(storage.PortableDirectory));
    }

    [Fact] public void SwitchingToUnavailableNormalDirectoryKeepsPortableMode()
    {
        storage.SaveMode(true, false);
        File.WriteAllText(storage.NormalDirectory, "occupied");
        Assert.ThrowsAny<IOException>(() => storage.SaveMode(false, true));
        Assert.True(storage.ReadPortable());
    }

    [LinuxFact] public void ReadOnlyDestinationPreservesMode()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(storage.PortableDirectory);
        var original = File.GetUnixFileMode(storage.PortableDirectory);
        try
        {
            File.SetUnixFileMode(storage.PortableDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Throws<UnauthorizedAccessException>(() => storage.SaveMode(true, false));
            Assert.False(storage.ReadPortable());
        }
        finally { File.SetUnixFileMode(storage.PortableDirectory, original); }
    }

    [Fact] public void SettingsArgumentRejectsDirectoriesAndRoots()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--settings", root]));
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--settings", Path.GetPathRoot(root)!]));
    }

    [Fact] public void ConcurrentWriterPreventsModeChange()
    {
        using var guard = new FileStream(storage.MarkerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => storage.SaveMode(true, false));
        Assert.False(storage.ReadPortable());
    }

    [Fact] public void MarkerDirectoryIsReportedInsteadOfSilentlyUsingNormalProfile()
    {
        Directory.CreateDirectory(storage.MarkerPath);
        Assert.Throws<IOException>(() => storage.ReadPortable());
    }

    [Fact] public void PortableProfileTravelsWithApplicationDirectory()
    {
        storage.SaveMode(true, false);
        new LibraryStore(storage.SettingsPath).Save(new Library { Executable = "portable" });
        var moved = Path.Combine(root, "moved");
        Directory.Move(storage.ApplicationDirectory, moved);
        var reopened = new SettingsStorage(moved, storage.NormalDirectory);
        Assert.True(reopened.ReadPortable());
        Assert.Equal("portable", new LibraryStore(reopened.SettingsPath).Load().Executable);
    }

    [Fact] public void ExplicitSettingsPathTakesPrecedence()
    {
        var path = Path.Combine(root, "custom.json");
        Assert.Equal(path, DiagnosticArguments.Parse(["--settings", path]).SettingsPath);
        Assert.Equal(path, DiagnosticArguments.Parse(["--portable", "--settings", path, "--single-instance"]).SettingsPath);
    }

    [Fact] public void CommandLineModesResolveProfilesWithoutChangingSavedMode()
    {
        var defaults = new SettingsStorage();
        var before = defaults.ReadPortable();
        var portable = DiagnosticArguments.Parse(["--portable", "--single-instance", "--log-level", "off"]);
        Assert.Equal(Path.Combine(defaults.PortableDirectory, "library.json"), portable.SettingsPath);
        Assert.True(portable.PreventMultipleInstances);
        Assert.Equal(DiagnosticLevel.Off, portable.Level);
        Assert.Empty(portable.ApplicationArguments);
        Assert.Equal(Path.Combine(defaults.NormalDirectory, "library.json"), DiagnosticArguments.Parse(["--normal"]).SettingsPath);
        Assert.Equal(before, defaults.ReadPortable());
    }

    [Theory]
    [InlineData("--portable", "--normal")]
    [InlineData("--portable", "--portable")]
    [InlineData("--single-instance", "--single-instance")]
    [InlineData("--settings", "--portable")]
    public void InvalidCommandLineModeCombinationsAreRejected(string first, string second) =>
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse([first, second]));

    public void Dispose() => Directory.Delete(root, true);
}
