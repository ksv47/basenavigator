using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Очистка временных файлов» as the original: the roots of 8.2/8.1, the accounts, «выборочная» and «грубая» by the check boxes.
public sealed class CacheStage11Tests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-cache11-" + Guid.NewGuid().ToString("N"));
    private BaseCacheRoots Separate => new(Path.Combine(folder, "Local", "1cv8"), Path.Combine(folder, "Roaming", "1cv8"));
    public CacheStage11Tests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private static string Make(string path, params string[] files)
    {
        foreach (var file in files)
        {
            var full = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "x");
        }
        return path;
    }

    [Fact] public void EachFamilyHasItsRootAndAnotherProfileItsOwnFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(Path.Combine("1C", "1cv8"), BaseCacheRoots.For("8.3")!.Program);
            Assert.EndsWith(Path.Combine("1C", "1cv82"), BaseCacheRoots.For("8.2")!.User);
            Assert.EndsWith(Path.Combine("1C", "1cv81"), BaseCacheRoots.For("8.1")!.Program);
            var other = BaseCacheRoots.For("8.3", @"C:\Users\другой")!;
            Assert.Equal(@"C:\Users\другой\AppData\Local\1C\1cv8", other.Program);
            Assert.Equal(@"C:\Users\другой\AppData\Roaming\1C\1cv8", other.User);
        }
        else
        {
            Assert.Equal(BaseCacheRoots.Current, BaseCacheRoots.For("8.3"));
            Assert.Null(BaseCacheRoots.For("8.2"));
            Assert.Null(BaseCacheRoots.For("8.3", "/home/other"));
        }
    }

    // A profile whose Local\1C leads to Roaming\1C through a link is skipped: clearing «Local» would take the user settings.
    [Fact] public void AProfileWithALinkAboveTheRootsIsSkipped()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(folder, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming", "1C", "1cv8"));
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local"));
        Assert.NotNull(BaseCacheRoots.For("8.3", profile));
        try { Directory.CreateSymbolicLink(Path.Combine(profile, "AppData", "Local", "1C"), Path.Combine(profile, "AppData", "Roaming", "1C")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no right to make links here
        try { Assert.Null(BaseCacheRoots.For("8.3", profile)); }
        finally { Directory.Delete(Path.Combine(profile, "AppData", "Local", "1C")); }
    }

    // The link is put above the roots after the folders were found: the removal checks the chain again and refuses.
    [Fact] public void ALinkPutAboveTheRootsAfterTheSearchStopsTheRemoval()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(folder, "late");
        var id = Guid.NewGuid().ToString("D");
        var local = Make(Path.Combine(profile, "AppData", "Local", "1C", "1cv8", id), "cache.bin");
        var settings = Make(Path.Combine(profile, "AppData", "Roaming", "1C", "1cv8", id), "def.usr.pfl");
        var roots = BaseCacheRoots.For("8.3", profile)!;
        Assert.Equal(local, Assert.Single(BaseCache.Find(new BaseEntry { Id = Guid.Parse(id), Name = "Б", Location = "C:\\b" }, true, BaseCacheKind.Program, roots)));
        // Local\1C is replaced by a junction to Roaming\1C.
        var localOneC = Path.Combine(profile, "AppData", "Local", "1C");
        Directory.Move(localOneC, localOneC + ".old");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", localOneC, Path.Combine(profile, "AppData", "Roaming", "1C") }) start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!) process.WaitForExit(10000);
        if (!Directory.Exists(localOneC)) return; // no junction here
        try
        {
            var result = BaseCache.Clear([local], BaseCacheKind.Program, roots, selective: false);
            Assert.Equal(0, result.Removed);
            Assert.True(File.Exists(Path.Combine(settings, "def.usr.pfl")));
        }
        finally { Directory.Delete(localOneC); }
    }

    [Fact] public void TwoNamesOfOneListAreTheSameFile()
    {
        var path = Path.Combine(folder, "a.v8i"); File.WriteAllText(path, "");
        Assert.True(ListAccess.SameFile(path, Path.Combine(folder, ".", "a.v8i")));
        Assert.False(ListAccess.SameFile(path, Path.Combine(folder, "b.v8i")));
        var link = Path.Combine(folder, "link.v8i");
        try { File.CreateSymbolicLink(link, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        Assert.True(ListAccess.SameFile(link, path));
    }

    [Fact] public void TheCurrentAccountIsAlwaysListedFirst()
    {
        var accounts = CacheAccounts.List();
        Assert.True(accounts[0].Current);
        Assert.Single(accounts, x => x.Current);
        Assert.All(accounts, x => Assert.True(Path.IsPathFullyQualified(x.Profile)));
    }

    // «Замена буквы диска» in a list that is not open: its own store writes it, with .bak, and refuses a file changed meanwhile.
    [Fact] public void TheDriveIsReplacedInAListThatIsNotOpen()
    {
        var path = Path.Combine(folder, "ibases.v8i");
        File.WriteAllText(path, "[Бух]\r\nConnect=File=\"C:\\Базы\\Бух\";\r\nID=11111111-1111-1111-1111-111111111111\r\nUnknownKey=keep\r\n" +
            "[Сервер]\r\nConnect=Srvr=\"srv\";Ref=\"zup\";\r\nID=22222222-2222-2222-2222-222222222222\r\n", new System.Text.UTF8Encoding(true));
        var store = new V8iStore(path);
        var (next, changed) = ListMaintenance.ReplaceDrive(store.Load(new Library()), 'C', 'E');
        Assert.Equal(1, changed);
        store.Save(next);
        var text = File.ReadAllText(path);
        Assert.Contains("Connect=File=\"E:\\Базы\\Бух\";", text);
        Assert.Contains("UnknownKey=keep", text);
        Assert.Contains("Connect=Srvr=\"srv\";Ref=\"zup\";", text);
        Assert.True(File.Exists(path + ".bak"));
        // Changed by another program after it was read: nothing is written over it.
        var stale = new V8iStore(path);
        var (again, _) = ListMaintenance.ReplaceDrive(stale.Load(new Library()), 'E', 'F');
        File.AppendAllText(path, "\r\n[Новая]\r\nConnect=File=\"D:\\x\";\r\n");
        var before = File.ReadAllBytes(path);
        Assert.Throws<ListChangedException>(() => stale.Save(again));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact] public void SelectiveKeepsTheSettingsFilesRoughRemovesTheFolder()
    {
        var roots = Separate;
        var id = Guid.NewGuid().ToString("D");
        var settings = Make(Path.Combine(roots.User, id), "def.usr.pfl", "Config/cache.bin", "form.reg");
        var selective = BaseCache.Clear([settings], BaseCacheKind.UserSettings, roots, selective: true);
        Assert.Equal(1, selective.Removed);
        Assert.True(File.Exists(Path.Combine(settings, "def.usr.pfl")));
        Assert.True(File.Exists(Path.Combine(settings, "form.reg")));
        Assert.False(File.Exists(Path.Combine(settings, "Config", "cache.bin")));
        Assert.Equal(0, BaseCache.Size(settings, BaseCacheKind.UserSettings, roots, selective: true));
        var rough = BaseCache.Clear([settings], BaseCacheKind.UserSettings, roots, selective: false);
        Assert.Equal(1, rough.Removed);
        Assert.False(Directory.Exists(settings));
    }
}
