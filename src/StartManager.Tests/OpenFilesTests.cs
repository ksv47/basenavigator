using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Linux: a folder with a file open in a visible process is neither cleaned nor copied (/proc/<pid>/fd).
public sealed class OpenFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-open-" + Guid.NewGuid().ToString("N"));
    public OpenFilesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData("/home/u/.1cv8/1C/1cv8/abc", "/home/u/.1cv8/1C/1cv8/abc/Config/a.bin", true)]
    [InlineData("/home/u/.1cv8/1C/1cv8/abc/", "/home/u/.1cv8/1C/1cv8/abc", true)]
    [InlineData("/home/u/base", "/home/u/base/1Cv8.1CD (deleted)", true)]
    [InlineData("/home/u/base", "/home/u/base2/1Cv8.1CD", false)]
    [InlineData("/home/u/base", "/home/u/Base/1Cv8.1CD", false)]
    [InlineData("/home/u/base", "socket:[12345]", false)]
    public void LinkIsInsideTheFolder(string target, string link, bool inside) => Assert.Equal(inside, OpenFiles.Contains(target, link));

    [Fact] public void DescriptionNamesTheProcesses()
    {
        var text = OpenFiles.Describe([.. Enumerable.Range(1, 7).Select(x => new FileHolder(x, "1cv8c"))]);
        Assert.Contains("1cv8c (1)", text); Assert.Contains("и ещё 2", text);
    }

    [Fact] public void WindowsLeavesItToTheSharingRules() { if (OperatingSystem.IsWindows()) Assert.Empty(OpenFiles.Under(folder)); }

    [Fact] public void OwnOpenFileIsFound()
    {
        if (!OperatingSystem.IsLinux()) return;
        var file = Path.Combine(folder, "inner", "a.bin"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "x");
        Assert.DoesNotContain(OpenFiles.Under(folder), x => x.Pid == Environment.ProcessId);
        using (File.OpenRead(file)) Assert.Contains(OpenFiles.Under(folder), x => x.Pid == Environment.ProcessId);
        Assert.DoesNotContain(OpenFiles.Under(folder + "2"), x => x.Pid == Environment.ProcessId);
    }

    [Fact] public void UnreadableProcRefusesInsteadOfAllowing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var empty = Path.Combine(folder, "proc"); Directory.CreateDirectory(empty);
        Assert.Throws<IOException>(() => OpenFiles.Under(folder, empty));
        Assert.Throws<IOException>(() => OpenFiles.Under(folder, Path.Combine(folder, "missing")));
    }

    [Fact] public void DescriptorsAreFoundThroughFdinfoAndWithoutIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var held = Path.Combine(folder, "base", "1Cv8.1CD"); Directory.CreateDirectory(Path.GetDirectoryName(held)!); File.WriteAllText(held, "x");
        var proc = Path.Combine(folder, "proc");
        // 100: fdinfo lists the descriptor; 200: no fdinfo, only fd; 300: fdinfo names a descriptor that leads elsewhere;
        // 400: fdinfo cannot be listed; 500: fdinfo is empty. fd still shows the descriptor in 200, 400 and 500.
        foreach (var (pid, info, target) in new[] { (100, "file", held), (200, "none", held), (300, "file", "/elsewhere/file"), (400, "denied", held), (500, "empty", held) })
        {
            Directory.CreateDirectory(Path.Combine(proc, pid.ToString(), "fd"));
            File.CreateSymbolicLink(Path.Combine(proc, pid.ToString(), "fd", "7"), target);
            if (info == "none") continue;
            var fdinfo = Path.Combine(proc, pid.ToString(), "fdinfo"); Directory.CreateDirectory(fdinfo);
            if (info != "empty") File.WriteAllText(Path.Combine(fdinfo, "7"), "pos: 0\n");
            if (info == "denied") File.SetUnixFileMode(fdinfo, UnixFileMode.None);
        }
        try { Assert.Equal([100, 200, 400, 500], OpenFiles.Under(Path.Combine(folder, "base"), proc).Select(x => x.Pid).Order()); }
        finally { File.SetUnixFileMode(Path.Combine(proc, "400", "fdinfo"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact] public void ProfileAliasSharesTheLockOfItsTarget()
    {
        var real = Path.Combine(folder, "real.json");
        new LibraryStore(real).Save(new Library());
        var alias = Path.Combine(folder, "alias.json");
        try { File.CreateSymbolicLink(alias, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // Windows without the right to create links.
        var store = new LibraryStore(alias); var library = store.Load();
        using (new FileStream(real + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("занят другим окном", Assert.Throws<IOException>(() => store.Save(library with { Executable = "/x" })).Message);
        store.Save(library with { Executable = "/x" });
        Assert.Contains("\"/x\"", File.ReadAllText(real));
    }

    [Fact] public void HeldCacheFolderStaysOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var roots = new BaseCacheRoots(Path.Combine(folder, "Local"), Path.Combine(folder, "Roaming"));
        var cache = Path.Combine(roots.Program, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "a.bin"), "x"); File.WriteAllText(Path.Combine(cache, "b.bin"), "y");
        using (File.OpenRead(Path.Combine(cache, "a.bin")))
        {
            var result = BaseCache.Clear([cache], BaseCacheKind.Program, roots);
            Assert.Equal(0, result.Removed); Assert.StartsWith(cache + " — Файлы открыты:", Assert.Single(result.Failed));
        }
        Assert.True(File.Exists(Path.Combine(cache, "b.bin")));
        Assert.Equal(1, BaseCache.Clear([cache], BaseCacheKind.Program, roots).Removed);
    }

    [Fact] public void OpenBaseIsNotCopiedOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = Path.Combine(folder, "base"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "1Cv8.1CD"), "base");
        var target = Path.Combine(folder, "copy");
        using (new FileStream(Path.Combine(source, "1Cv8.1CD"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Contains("База открыта", Assert.Throws<IOException>(() => BaseDuplicate.CopyFiles(source, target)).Message);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateDirectories(folder, ".copy.copying-*"));
        BaseDuplicate.CopyFiles(source, target);
        Assert.True(File.Exists(Path.Combine(target, "1Cv8.1CD")));
    }
}
