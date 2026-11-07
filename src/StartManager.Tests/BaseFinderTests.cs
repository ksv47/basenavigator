using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Поиск информационных баз на компьютере»: folders with 1Cv8.1CD, no loops, nothing written.
public sealed class BaseFinderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-find-" + Guid.NewGuid().ToString("N"));
    public BaseFinderTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private string Base(string relative, string file = "1Cv8.1CD")
    {
        var path = Path.Combine(folder, relative); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file), "base"); return Path.GetFullPath(path);
    }

    [Fact] public void FindsBasesAndDoesNotSearchInsideThem()
    {
        var a = Base("work/a");
        Base("work/a/copy"); // inside a found base: not searched
        var b = Base(Path.Combine("archive", "2025", "b"));
        var odd = Base("odd", "1cv8.1cd");
        Base("$Recycle.Bin/x");
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var visited = 0;
        var found = BaseFinder.Scan([folder], visited: x => visited = x).Select(x => x.Folder).ToList();
        Assert.Equal(new[] { b, odd, a }.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        Assert.True(visited >= 5);
        var one = BaseFinder.Scan([a]).Single();
        Assert.Equal(4, one.Size); Assert.Equal(a, one.Folder);
    }

    [Fact] public void PlatformCacheIsNotABase()
    {
        var real = Base("bases/buh");
        Base("cache/0db4cbfb/cgf-cache");
        var roots = new BaseCacheRoots(Path.Combine(folder, "cache"), Path.Combine(folder, "cache"));
        Assert.Equal([real], BaseFinder.Scan([folder], cacheRoots: roots).Select(x => x.Folder));
        // The cache of another user is recognised by its place.
        Base("Users/other/AppData/Local/1C/1cv8/5f1e/abc");
        Base("elsewhere/cgf-cache");
        Assert.Equal([real], BaseFinder.Scan([folder], cacheRoots: roots).Select(x => x.Folder));
    }

    [Fact] public void LinksAreNotFollowed()
    {
        var real = Base("real/base");
        var link = Path.Combine(folder, "loop");
        try { Directory.CreateSymbolicLink(link, folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // Windows without the right to create links.
        Assert.Equal([real], BaseFinder.Scan([folder]).Select(x => x.Folder));
    }

    [Fact] public void ALinkChosenAsARootIsItsTarget()
    {
        var real = Base("real");
        var link = Path.Combine(folder, "alias");
        try
        {
            if (OperatingSystem.IsWindows()) Junction(link, real); else Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return; }
        try
        {
            Assert.Equal([real], BaseFinder.Scan([link, real]).Select(x => x.Folder));
            var bases = new[] { new BaseEntry { Name = "A", Kind = ConnectionKind.File, Location = real } };
            Assert.True(BaseFinder.Listed(bases, link));
        }
        // The link goes alone: deleting the tree through it is refused on Windows.
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Fact] public void ARootInsideTheCacheOrTheRecycleBinFindsNothing()
    {
        var service = Base("cache/0db4/cgf-cache");
        var bin = Base("$Recycle.Bin/S-1/base");
        var roots = new BaseCacheRoots(Path.Combine(folder, "cache"), Path.Combine(folder, "cache"));
        Assert.Empty(BaseFinder.Scan([service, Path.Combine(folder, "cache", "0db4"), bin], cacheRoots: roots));
    }

    [Fact] public void TheCacheIsSkippedByItsPathUnderAParent()
    {
        var real = Base("bases/buh");
        Base("moved-cache/0db4"); // a service 1Cv8.1CD right in a cache folder whose names are not the usual ones
        var roots = new BaseCacheRoots(Path.Combine(folder, "moved-cache"), Path.Combine(folder, "moved-cache"));
        Assert.Equal([real], BaseFinder.Scan([folder], cacheRoots: roots).Select(x => x.Folder));
    }

    // A junction needs no privilege on Windows, unlike a symbolic link.
    private static void Junction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("mklink /J failed");
    }

    [Fact] public void CancellationStopsTheSearch()
    {
        Base("a/b");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => BaseFinder.Scan([folder], cancel.Token).ToList());
    }

    [Fact] public void ListedBasesAndNamesFollowTheRules()
    {
        var a = Base("a");
        var bases = new[] { new BaseEntry { Name = "A", Kind = ConnectionKind.File, Location = a + Path.DirectorySeparatorChar } };
        Assert.True(BaseFinder.Listed(bases, a));
        Assert.Equal(OperatingSystem.IsWindows(), BaseFinder.Listed(bases, a.ToUpperInvariant()));
        Assert.False(BaseFinder.Listed(bases, Path.Combine(folder, "b")));
        var found = new FoundBase(Path.Combine(folder, "Бухгалтерия"), 1, new DateTime(2026, 9, 27));
        Assert.Equal("new_Бухгалтерия 27.09.2026", BaseFinder.Name(found, "new_", true));
        Assert.Equal("Бухгалтерия", BaseFinder.Name(found, "", false));
    }
}
