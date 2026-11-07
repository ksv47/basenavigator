using System.Runtime.InteropServices;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// umask, resource limits and signal dispositions belong to the entire process.
[CollectionDefinition("Linux process file policy", DisableParallelization = true)]
public sealed class LinuxProcessFilePolicyCollection;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires Linux file modes and resource limits."; }
}

[Collection("Linux process file policy")]
public sealed class LinuxPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-linux-persistence-" + Guid.NewGuid().ToString("N"));
    public LinuxPersistenceTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [LinuxFact]
    public void ReplacementAndBackupPreserveGroupPermissionsDespiteUmask()
    {
        if (!OperatingSystem.IsLinux()) return;
        var previousMask = umask(0x3f); // 0077
        try
        {
            foreach (var permissions in new[] { 0x180, 0x1b0, 0x1b4 }) // 0600, 0660, 0664
            {
                var path = Path.Combine(root, $"library-{permissions}.json");
                var store = new LibraryStore(path); store.Save(new Library());
                var activity = new ActivityStore(path); activity.SetFavorite("test", Guid.NewGuid(), true);
                var list = Path.Combine(root, $"list-{permissions}.v8i");
                File.WriteAllText(list, "[Original]\nConnect=Srvr=server;Ref=base;\n");
                var shared = new V8iStore(list); var library = shared.Load(new Library());
                foreach (var file in new[] { path, activity.PathName, list })
                    File.SetUnixFileMode(file, (UnixFileMode)permissions);
                store.Save(new Library { Executable = "changed" });
                activity.SetFavorite("test", Guid.NewGuid(), true);
                shared.Save(library with { Bases = [library.Bases[0] with { Name = "Changed" }] });
                foreach (var file in new[] { path, activity.PathName, list })
                    foreach (var target in new[] { file, file + ".bak" })
                        Assert.Equal((UnixFileMode)permissions, File.GetUnixFileMode(target));
            }
        }
        finally { umask(previousMask); }
    }

    [LinuxFact]
    public void FileSizeLimitPreservesJsonAndRollsBackPartialSharedListWrite()
    {
        var path = Path.Combine(root, "library.json");
        var store = new LibraryStore(path); store.Save(new Library());
        var originalJson = File.ReadAllBytes(path);
        var list = Path.Combine(root, "list.v8i");
        File.WriteAllText(list, "[Original]\nConnect=Srvr=server;Ref=base;\nUnknown=keep\n");
        var shared = new V8iStore(list); var library = shared.Load(new Library());
        var originalList = File.ReadAllBytes(list);
        Assert.Equal(0, getrlimit(1, out var previousLimit)); // RLIMIT_FSIZE
        Assert.True(previousLimit.Current >= 4096);
        var limited = previousLimit; limited.Current = 4096;
        var previousSignal = signal(25, new IntPtr(1)); // SIGXFSZ: SIG_IGN
        Assert.NotEqual(new IntPtr(-1), previousSignal);
        Exception? jsonError = null, listError = null;
        try
        {
            Assert.Equal(0, setrlimit(1, ref limited));
            // No assertion logging while the process file-size limit is active.
            try { store.Save(new Library { Executable = new string('x', 20000) }); }
            catch (Exception ex) { jsonError = ex; }
            try { shared.Save(library with { Bases = [library.Bases[0] with { Name = new string('x', 20000) }] }); }
            catch (Exception ex) { listError = ex; }
        }
        finally
        {
            var restored = setrlimit(1, ref previousLimit);
            signal(25, previousSignal);
            Assert.Equal(0, restored);
        }
        Assert.IsType<IOException>(jsonError);
        Assert.Contains("Файл не записан", LegacyImport.ErrorMessage(jsonError));
        Assert.IsType<IOException>(listError);
        Assert.Contains("исходное содержимое восстановлено", listError.Message);
        Assert.Equal(originalJson, File.ReadAllBytes(path));
        Assert.Equal(originalList, File.ReadAllBytes(list));
        Assert.Equal(originalList, File.ReadAllBytes(list + ".bak"));
        Assert.False(File.Exists(list + ".writing"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        // The store remains usable after a successful rollback.
        Assert.False(shared.Lost);
        shared.Save(library with { Bases = [library.Bases[0] with { Name = "After rollback" }] });
        Assert.Equal("After rollback", new V8iStore(list).Load(new Library()).Bases[0].Name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Limit { public ulong Current; public ulong Maximum; }
    [DllImport("libc")] private static extern uint umask(uint mask);
    [DllImport("libc")] private static extern int getrlimit(int resource, out Limit limit);
    [DllImport("libc")] private static extern int setrlimit(int resource, ref Limit limit);
    [DllImport("libc")] private static extern IntPtr signal(int number, IntPtr handler);
}
