using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// PlatformCatalog.UserRoots is process-wide (it is set when the settings are read or saved): these tests run alone, so that
// another test reading a profile at the same moment cannot reset it.
[CollectionDefinition("PlatformFolders", DisableParallelization = true)]
public sealed class PlatformFoldersCollection;

[Collection("PlatformFolders")]
public sealed class PlatformFolderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-platform-folders-" + Guid.NewGuid().ToString("N"));
    public PlatformFolderTests() => Directory.CreateDirectory(folder);
    public void Dispose() { PlatformCatalog.UseFolders(new Library()); Directory.Delete(folder, true); }

    private string FakeClient(string root, string version)
    {
        var path = Path.Combine(root, version, "bin", OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path;
    }

    [Fact] public void FolderListIsNormalized()
    {
        var custom = Path.Combine(folder, "custom");
        var list = PlatformCatalog.NormalizeFolders(["  " + custom + Path.DirectorySeparatorChar, "", "   ", "\"" + custom + "\"", Path.Combine(folder, "other")]);
        Assert.Equal([custom, Path.Combine(folder, "other")], list);
        // A root keeps its separator.
        var root = Path.GetPathRoot(folder)!;
        Assert.Equal([root], PlatformCatalog.NormalizeFolders([root]));
        Assert.Empty(PlatformCatalog.NormalizeFolders([]));
        foreach (var bad in new[] { "relative\\folder", "1cv8", "~/1cv8" })
            Assert.Contains(bad, Assert.Throws<ArgumentException>(() => PlatformCatalog.NormalizeFolders([custom, bad])).Message);
        if (OperatingSystem.IsWindows()) Assert.Single(PlatformCatalog.NormalizeFolders([custom, custom.ToUpperInvariant()]));
    }

    [Fact] public void SearchFoldersOfTheSettingsAreScannedWithTheStandardOnes()
    {
        var custom = Path.Combine(folder, "custom");
        var client = FakeClient(custom, "8.5.1.1343");
        try
        {
            Assert.DoesNotContain(PlatformCatalog.Scan().Installations, x => x.Executable == client);
            PlatformCatalog.UseFolders(new Library { PlatformFolders = [custom, "relative-is-ignored"] });
            Assert.Contains(custom, PlatformCatalog.DefaultRoots());
            Assert.DoesNotContain("relative-is-ignored", PlatformCatalog.DefaultRoots());
            var found = Assert.Single(PlatformCatalog.Scan().Installations, x => x.Executable == client);
            Assert.Equal(new Version(8, 5, 1, 1343), found.Version);
            // The rule «8.5» takes a build of that family (a fixture file has no readable architecture, so one is given here).
            var typed = found with { Architecture = PlatformArchitecture.X64 };
            Assert.Equal(typed, PlatformCatalog.Select(new() { Version = "8.5" }, [typed], PlatformArchitecture.X64, OperatingSystem.IsWindows()));
        }
        finally { PlatformCatalog.UseFolders(new Library()); }
        Assert.DoesNotContain(PlatformCatalog.Scan().Installations, x => x.Executable == client);
    }

    // A client file whose architecture can be read: x64.
    private string RealisticClient(string root, string version)
    {
        var path = FakeClient(root, version);
        var bytes = new byte[128];
        if (OperatingSystem.IsWindows())
        { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64); bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664); }
        else { bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F'; bytes[4] = 2; bytes[5] = 1; bytes[18] = 62; }
        File.WriteAllBytes(path, bytes); return path;
    }

    // Codex audit P2.3: a library is resolved with its own folders, not with those of the profile read last.
    [Fact] public void ResolveUsesTheFoldersOfItsOwnLibrary()
    {
        var custom = Path.Combine(folder, "custom");
        var client = RealisticClient(custom, "8.5.1.1343");
        var rule = new PlatformRule { Version = "8.5.1.1343", Architecture = PlatformArchitecture.X64 };
        var a = new Library { PlatformRule = rule, PlatformFolders = [custom] };
        var b = new Library { PlatformRule = rule };
        var entry = new BaseEntry { Name = "Б", Kind = ConnectionKind.Server, Location = "srv\\b" };
        // The active profile is the other one in both directions.
        PlatformCatalog.UseFolders(b);
        Assert.Equal(client, PlatformCatalog.Resolve(a, entry));
        PlatformCatalog.UseFolders(a);
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Resolve(b, entry));
    }

    // Codex audit P3: a folder of the settings that is gone, or is a file, is reported; the standard places stay silent.
    [Fact] public void MissingAndFileFoldersOfTheSettingsAreReported()
    {
        var missing = Path.Combine(folder, "gone"); var file = Path.Combine(folder, "file.txt"); File.WriteAllText(file, "x");
        var existing = Path.Combine(folder, "here"); Directory.CreateDirectory(existing);
        var scan = PlatformCatalog.Scan([missing, existing], configured: [missing, existing, file]);
        Assert.Equal(2, scan.Warnings.Count(x => x.Contains("Каталог поиска платформ")));
        Assert.Contains(scan.Warnings, x => x.Contains(missing)); Assert.Contains(scan.Warnings, x => x.Contains(file));
        // Not configured: nothing to report, as for an absent standard folder.
        Assert.Empty(PlatformCatalog.Scan([missing]).Warnings);
        // The default search reports the active profile's folders.
        PlatformCatalog.UseFolders(new Library { PlatformFolders = [missing] });
        Assert.Contains(PlatformCatalog.Scan().Warnings, x => x.Contains(missing));
        // The standard list never contains the settings' own folders.
        Assert.DoesNotContain(missing, PlatformCatalog.StandardRoots()); Assert.Contains(missing, PlatformCatalog.DefaultRoots());
    }

    [Fact] public void SearchFoldersAreKeptInTheProfileAndOldProfilesLoadWithoutThem()
    {
        var settings = Path.Combine(folder, "settings.json");
        var custom = Path.Combine(folder, "custom");
        try
        {
            var store = new LibraryStore(settings);
            store.Save(new Library { PlatformFolders = [custom] });
            var json = File.ReadAllText(settings);
            Assert.Contains("PlatformFolders", json);
            Assert.Equal([custom], new LibraryStore(settings).Load().PlatformFolders);
            Assert.Equal([custom], PlatformCatalog.UserRoots);
            // Saving publishes the folders as well (Codex audit P2.3): no read is needed.
            PlatformCatalog.UseFolders(new Library());
            var changed = new LibraryStore(settings); var other = Path.Combine(folder, "other");
            changed.Load(); PlatformCatalog.UseFolders(new Library());
            changed.Save(new Library { PlatformFolders = [other] });
            Assert.Equal([other], PlatformCatalog.UserRoots);
            // A profile written before the setting existed.
            File.WriteAllText(settings, "{\"Version\":1}");
            Assert.Empty(new LibraryStore(settings).Load().PlatformFolders);
            Assert.Empty(PlatformCatalog.UserRoots);
            // A damaged value is refused instead of searching a null path.
            File.WriteAllText(settings, "{\"Version\":1,\"PlatformFolders\":[null]}");
            Assert.Throws<InvalidDataException>(() => new LibraryStore(settings).Load());
            File.WriteAllText(settings, "{\"Version\":1,\"PlatformFolders\":null}");
            Assert.Throws<InvalidDataException>(() => new LibraryStore(settings).Load());
        }
        finally { PlatformCatalog.UseFolders(new Library()); }
    }
}
