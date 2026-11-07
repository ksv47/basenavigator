using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "StartManager-tests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(folder);
    private BaseEntry Entry => new() { Name = "Бухгалтерия с пробелами", Location = folder };
    private LibraryStore Store => new(Path.Combine(folder, "nested", "library.json"));
    public void Dispose() => Directory.Delete(folder, true);

    [Fact] public void MissingFileReturnsEmptyLibrary()
    {
        Assert.Empty(Store.Load().Bases);
        Assert.Equal(new SettingsStorage().SettingsPath, LibraryStore.DefaultPath);
    }
    [Fact] public void RoundtripPreservesUnicodeAndId()
    {
        var entry = Entry;
        var library = new Library { Executable = "C:\\Платформа 1С\\1cv8.exe", Bases = [entry] };
        Store.Save(library);
        Assert.Equal(entry, Assert.Single(Store.Load().Bases));
        Assert.Equal(library.Executable, Store.Load().Executable);
        Assert.Equal(entry.Name, entry.ToString());
    }
    [Fact] public void EditBacksUpPreviousVersion()
    {
        var original = Entry;
        Store.Save(new Library { Bases = [original] });
        Store.Save(new Library { Bases = [original with { Name = "Changed" }] });
        Assert.Equal("Changed", Assert.Single(Store.Load().Bases).Name);
        Assert.Equal(original, Assert.Single(new LibraryStore(Store.PathName + ".bak").Load().Bases));
    }
    [Fact] public void DeleteConnectionDoesNotDeleteDatabase()
    {
        var database = Path.Combine(folder, "1Cv8.1CD");
        File.WriteAllText(database, "fixture, not a database");
        Store.Save(new Library { Bases = [Entry] });
        Store.Save(new Library());
        Assert.Empty(Store.Load().Bases);
        Assert.True(File.Exists(database));
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Bases\":null}")]
    [InlineData("{\"Executable\":null}")]
    [InlineData("{\"Bases\":[null]}")]
    [InlineData("{\"Bases\":[{\"Name\":\"\"}]}")]
    public void BadSettingsAreRejectedAndPreserved(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store.PathName)!);
        File.WriteAllText(Store.PathName, json);
        Assert.ThrowsAny<Exception>(() => Store.Load());
        Assert.Equal(json, File.ReadAllText(Store.PathName));
    }
    [Fact] public void DuplicateIdsCannotOverwriteGoodSettings()
    {
        Store.Save(new Library());
        var entry = Entry;
        Assert.Throws<InvalidDataException>(() => Store.Save(new Library { Bases = [entry, entry] }));
        Assert.Empty(Store.Load().Bases);
    }
    [Fact] public void WriteFailurePreservesOriginalAndCleansTemporaryFile()
    {
        Store.Save(new Library());
        Directory.CreateDirectory(Store.PathName + ".bak");
        var error = Record.Exception(() => Store.Save(new Library { Bases = [Entry] }));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected error: {error}");
        Assert.Empty(Store.Load().Bases);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Store.PathName)!, "*.tmp"));
    }
    [Theory]
    [InlineData("", "C:\\base", ConnectionKind.File)]
    [InlineData(" ", "C:\\base", ConnectionKind.File)]
    [InlineData("Base", "", ConnectionKind.File)]
    [InlineData("Base", "x\"y", ConnectionKind.File)]
    [InlineData("Base", "x\ny", ConnectionKind.File)]
    [InlineData("Base", "server", ConnectionKind.Server)]
    [InlineData("Base", "\\base", ConnectionKind.Server)]
    [InlineData("Base", "server\\", ConnectionKind.Server)]
    [InlineData("Base", "x", (ConnectionKind)99)]
    public void InvalidEntriesRejected(string name, string location, ConnectionKind kind) =>
        Assert.Throws<ArgumentException>(() => new BaseEntry { Name = name, Location = location, Kind = kind }.Validate());

    [Fact] public void ElevationKeepsTheCommandAndAsksWindows()
    {
        var info = Launcher.Build(Environment.ProcessPath!, Entry, designer: false);
        if (!OperatingSystem.IsWindows()) { Assert.Throws<PlatformNotSupportedException>(() => Launcher.Elevate(info)); Assert.False(Launcher.IsElevated()); return; }
        Launcher.Elevate(info);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(new[] { "ENTERPRISE", "/F", folder }, info.ArgumentList);
        Assert.Equal(Environment.ProcessPath, info.FileName);
        Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), info.WorkingDirectory);
        _ = Launcher.IsElevated();
    }
    [Theory]
    [InlineData(false, "ENTERPRISE")]
    [InlineData(true, "DESIGNER")]
    public void FileLaunchKeepsArgumentsSeparate(bool designer, string mode)
    {
        var info = Launcher.Build(Environment.ProcessPath!, Entry, designer);
        Assert.Equal(new[] { mode, "/F", folder }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.Equal(Environment.ProcessPath, info.FileName);
        Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), info.WorkingDirectory);
    }
    [Theory]
    [InlineData(false, "ENTERPRISE")]
    [InlineData(true, "DESIGNER")]
    public void ServerLaunchPreservesSpacesAndUnicode(bool designer, string mode)
    {
        var entry = Entry with { Kind = ConnectionKind.Server, Location = "srv1c\\База с пробелами" };
        Assert.Equal(new[] { mode, "/S", entry.Location }, Launcher.Build(Environment.ProcessPath!, entry, designer).ArgumentList);
    }
    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    public void InvalidExecutableRejected(string path) => Assert.Throws<ArgumentException>(() => Launcher.Build(path, Entry, false));
    // The message names the missing file, so the user knows which path to fix.
    [Fact] public void MissingExecutableRejected() => Assert.Contains(Path.Combine(folder, "absent.exe"),
        Assert.Throws<ArgumentException>(() => Launcher.Build(Path.Combine(folder, "absent.exe"), Entry, false)).Message);
    [Fact] public void MissingDatabaseDirectoryRejected() => Assert.Throws<ArgumentException>(() => Launcher.Build(Environment.ProcessPath!, Entry with { Location = Path.Combine(folder, "absent") }, false));
}
