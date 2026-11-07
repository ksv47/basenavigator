using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Base folders of the platform on temporary roots: names like the platform's, and clearing that keeps what it must.
public sealed class BaseCacheTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-cache-" + Guid.NewGuid().ToString("N"));
    private BaseCacheRoots Separate => new(Path.Combine(folder, "Local", "1cv8"), Path.Combine(folder, "Roaming", "1cv8"));
    private BaseCacheRoots Shared => new(Path.Combine(folder, "home", "1cv8"), Path.Combine(folder, "home", "1cv8"));
    private static readonly BaseEntry FileBase = new() { Id = Guid.Parse("11111111-2222-4333-8444-555555555501"), Name = "Файловая", Location = @"D:\Bases\Учебные материалы" };
    private static readonly BaseEntry ServerBase = new() { Id = Guid.Parse("11111111-2222-4333-8444-555555555502"), Name = "Серверная", Kind = ConnectionKind.Server, Location = @"host1:8314\accounting" };
    public BaseCacheTests() => Directory.CreateDirectory(folder);
    public void Dispose()
    {
        if (!Directory.Exists(folder)) return;
        // Links made by a test are removed as links first, so the cleanup never walks into their targets.
        foreach (var link in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Where(x => new DirectoryInfo(x).Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderByDescending(x => x.Length).ToList())
            Directory.Delete(link);
        Directory.Delete(folder, true);
    }

    private static string Make(string path, params string[] files)
    {
        Directory.CreateDirectory(path);
        foreach (var file in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, file))!); File.WriteAllText(Path.Combine(path, file), "12345"); }
        return path;
    }
    private void WriteMap(BaseCacheRoots roots, params (string Connection, string Guid)[] pairs)
    {
        Directory.CreateDirectory(roots.User);
        var body = string.Join(",\n", pairs.Select((x, i) => $"{{2,{i},2,\n{{\"S\",\"{x.Connection.Replace("\"", "\"\"")}\"}},\n{{\"S\",\"{x.Guid}\"}},0}}"));
        File.WriteAllText(Path.Combine(roots.User, "1cv8u.pfl"), "\uFEFF{\n{\"Universal\",\n{\"IdConnStrMap\",\n{1,2,\n" + body + "\n}}}}");
    }

    [Fact] public void NamesComeFromTheListIdTheConnectionMapAndOldServerFolders()
    {
        var roots = Separate;
        WriteMap(roots, ("File=\"d:/bases/учебные материалы/\";", "11111111-2222-4333-8444-555555555506"),
            ("Srvr=\"HOST1:8314\";Ref=\"ACCOUNTING\";", "11111111-2222-4333-8444-555555555503"),
            ("Srvr=\"host1\";Ref=\"accounting\";", "11111111-2222-4333-8444-555555555504"),
            ("Srvr=\"other:8314\";Ref=\"accounting\";", "11111111-2222-4333-8444-555555555505"));
        var fileNames = BaseCache.Names(FileBase, true, roots);
        Assert.Contains("11111111-2222-4333-8444-555555555501", fileNames);
        if (OperatingSystem.IsWindows()) Assert.Contains("11111111-2222-4333-8444-555555555506", fileNames);
        Assert.DoesNotContain("11111111-2222-4333-8444-555555555501", BaseCache.Names(FileBase, false, roots));
        var serverNames = BaseCache.Names(ServerBase, true, roots);
        Assert.Contains("11111111-2222-4333-8444-555555555503", serverNames);
        // The same base name on the default port or on another host is another cluster.
        Assert.DoesNotContain("11111111-2222-4333-8444-555555555504", serverNames);
        Assert.DoesNotContain("11111111-2222-4333-8444-555555555505", serverNames);
        Assert.Contains("Srvr__host1_8314__Ref__accounting__", serverNames);
        Assert.DoesNotContain("Srvr__host1__Ref__accounting__", serverNames);
    }

    [Theory]
    [InlineData(@"server\base", "Srvr=\"server:1541\";Ref=\"base\";", true)]
    [InlineData(@"server:1541\base", "Srvr=\"SERVER\";Ref=\"BASE\";", true)]
    [InlineData(@"server:1541\base", "Srvr=\"server:2541\";Ref=\"base\";", false)]
    [InlineData(@"server:2541\base", "Srvr=\"server\";Ref=\"base\";", false)]
    [InlineData(@"[fe80::1]:1541\base", "Srvr=\"[fe80::1]\";Ref=\"base\";", true)]
    [InlineData(@"[fe80::1]:1541\base", "Srvr=\"[fe80::1]:2541\";Ref=\"base\";", false)]
    [InlineData(@"srv1,srv2\base", "Srvr=\"srv1\";Ref=\"base\";", false)]
    [InlineData(@"[fe80::1]:1541,[fe80::2]:1541\base", "Srvr=\"[fe80::1]:1541,[fe80::3]:1541\";Ref=\"base\";", false)]
    [InlineData(@"[fe80::1]:1541,[fe80::2]:1541\base", "Srvr=\"[FE80::1]:1541,[fe80::2]:1541\";Ref=\"base\";", true)]
    [InlineData(@"[fe80::1]x\base", "Srvr=\"[fe80::1]y\";Ref=\"base\";", false)]
    [InlineData(@"host:port\base", "Srvr=\"host:other\";Ref=\"base\";", false)]
    public void ServerBasesMatchOnlyTheSameCluster(string location, string connection, bool same)
    {
        var roots = Separate;
        WriteMap(roots, (connection, "1f9265c4-b4a6-49a8-be3d-619db89f580f"));
        var entry = ServerBase with { Location = location };
        Assert.Equal(same, BaseCache.Names(entry, false, roots).Contains("1f9265c4-b4a6-49a8-be3d-619db89f580f"));
    }

    [Fact] public void ALinkOnTheWayCannotLeadTheRemovalOutsideTheRoot()
    {
        var roots = Separate;
        Directory.CreateDirectory(roots.Program);
        var outside = Make(Path.Combine(folder, "outside", FileBase.Id.ToString("D")), "sentinel.pfl", "cache.bin");
        var link = Path.Combine(roots.Program, "8.3");
        Assert.True(MakeDirectoryLink(link, Path.Combine(folder, "outside")), "the test link was not created");
        Assert.Empty(BaseCache.Find(FileBase, true, BaseCacheKind.Program, roots));
        var result = BaseCache.Clear([Path.Combine(link, FileBase.Id.ToString("D"))], BaseCacheKind.Program, roots);
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(Path.Combine(outside, "sentinel.pfl")));
        // A link as the base folder itself is refused too.
        var direct = Path.Combine(roots.Program, FileBase.Id.ToString("D"));
        Assert.True(MakeDirectoryLink(direct, outside));
        Assert.Equal(0, BaseCache.Clear([direct], BaseCacheKind.Program, roots).Removed);
        Assert.True(File.Exists(Path.Combine(outside, "cache.bin")));
    }

    // A junction on Windows (no rights needed), a symbolic link elsewhere.
    private static bool MakeDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return true; }
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(10000);
        return Directory.Exists(link);
    }

    [Fact] public void FindsExistingFoldersDirectlyAndInVersionFolders()
    {
        var roots = Separate;
        var direct = Make(Path.Combine(roots.Program, FileBase.Id.ToString("D")), "Config/a.bin");
        var old = Make(Path.Combine(roots.Program, "8.2.19.130", FileBase.Id.ToString("D")), "b.bin");
        Make(Path.Combine(roots.Program, "not-a-version", FileBase.Id.ToString("D")), "c.bin");
        Assert.Equal(new[] { direct, old }, BaseCache.Find(FileBase, true, BaseCacheKind.Program, roots));
        Assert.Empty(BaseCache.Find(FileBase, true, BaseCacheKind.UserSettings, roots));
    }

    [Fact] public void SeparateRootsRemoveTheWholeFolderOfTheKindOnly()
    {
        var roots = Separate;
        var cache = Make(Path.Combine(roots.Program, FileBase.Id.ToString("D")), "Config/a.bin", "SICache/b.bin", "dbg.pfl");
        var settings = Make(Path.Combine(roots.User, FileBase.Id.ToString("D")), "1cv8.pfl");
        File.SetAttributes(Path.Combine(cache, "Config", "a.bin"), FileAttributes.ReadOnly);
        Assert.Equal(15, BaseCache.Size(cache, BaseCacheKind.Program, roots));
        var result = BaseCache.Clear([cache], BaseCacheKind.Program, roots);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(cache));
        Assert.True(File.Exists(Path.Combine(settings, "1cv8.pfl")));
        Assert.Empty(Directory.GetDirectories(roots.Program));
    }

    [Fact] public void SharedRootKeepsTheSettingsWhenClearingTheCache()
    {
        var roots = Shared;
        var base_ = Make(Path.Combine(roots.Program, FileBase.Id.ToString("D")), "1cv8c.pfl", "lognhist.reg", "071523a4/1cv8c.pfl", "071523a4/vrs-cache/x.bin", "cache.bin");
        Assert.Equal(10, BaseCache.Size(base_, BaseCacheKind.Program, roots));
        Assert.Equal(BaseCache.Clear([base_], BaseCacheKind.Program, roots), new BaseCacheResult(1, []), new ResultComparer());
        Assert.True(File.Exists(Path.Combine(base_, "1cv8c.pfl")));
        Assert.True(File.Exists(Path.Combine(base_, "lognhist.reg")));
        Assert.True(File.Exists(Path.Combine(base_, "071523a4", "1cv8c.pfl")));
        Assert.False(Directory.Exists(Path.Combine(base_, "071523a4", "vrs-cache")));
        Assert.False(File.Exists(Path.Combine(base_, "cache.bin")));
        BaseCache.Clear([base_], BaseCacheKind.UserSettings, roots);
        Assert.False(Directory.Exists(base_));
    }

    [Fact] public void FoldersOutsideThePlatformRootAreRefused()
    {
        var roots = Separate;
        var outside = Make(Path.Combine(folder, "Documents", FileBase.Id.ToString("D")), "keep.txt");
        var tricky = Path.Combine(roots.Program, "..", "..", "Documents", FileBase.Id.ToString("D"));
        var result = BaseCache.Clear([outside, tricky], BaseCacheKind.Program, roots);
        Assert.Equal(0, result.Removed); Assert.Equal(2, result.Failed.Count);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact] public void AFolderHeldBy1CStaysIntact()
    {
        if (!OperatingSystem.IsWindows()) return; // Linux does not lock open files against renaming.
        var roots = Separate;
        var cache = Make(Path.Combine(roots.Program, FileBase.Id.ToString("D")), "Config/a.bin", "b.bin");
        using (new FileStream(Path.Combine(cache, "Config", "a.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = BaseCache.Clear([cache], BaseCacheKind.Program, roots);
            Assert.Equal(0, result.Removed); Assert.Equal(new[] { cache }, result.Failed);
        }
        Assert.True(File.Exists(Path.Combine(cache, "b.bin")));
    }

    private sealed class ResultComparer : IEqualityComparer<BaseCacheResult>
    {
        public bool Equals(BaseCacheResult? x, BaseCacheResult? y) => x!.Removed == y!.Removed && x.Failed.SequenceEqual(y.Failed);
        public int GetHashCode(BaseCacheResult obj) => obj.Removed;
    }
}
