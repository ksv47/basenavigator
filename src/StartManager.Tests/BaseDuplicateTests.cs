using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class BaseDuplicateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BaseNavigator-duplicate-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(directory, "trade");
    public BaseDuplicateTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "1Cv8Log"));
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        File.WriteAllBytes(Path.Combine(Source, "1Cv8.1CD"), [.. Enumerable.Range(0, 3_000_000).Select(x => (byte)x)]);
        File.WriteAllText(Path.Combine(Source, "1Cv8Log", "1Cv8.lgf"), "log");
        File.SetLastWriteTimeUtc(Path.Combine(Source, "1Cv8.1CD"), new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
    }
    public void Dispose() => Directory.Delete(directory, true);
    private string[] Leftovers() => [.. Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Where(x => x!.Contains(".copying-"))!];

    [Fact] public void SuggestionsDoNotRepeatExistingNamesAndFolders()
    {
        Assert.Equal("Торговля (копия)", BaseDuplicate.SuggestName("Торговля", ["Торговля"]));
        Assert.Equal("Торговля (копия 3)", BaseDuplicate.SuggestName("Торговля", ["Торговля", "торговля (копия)", "Торговля (копия 2)"]));
        Assert.Equal(Source + "_copy", BaseDuplicate.SuggestFolder(Source + Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Source + "_copy");
        Assert.Equal(Source + "_copy2", BaseDuplicate.SuggestFolder(Source));
    }

    // Codex full audit 24.10, P2-4: a session that writes to the base during the copy (Linux has no lock against it) leaves a copy of
    // two moments: such a copy is not published, the staging folder goes.
    [Fact] public void ASourceChangedDuringTheCopyIsNotPublished()
    {
        var target = Path.Combine(directory, "trade copy");
        var total = BaseDuplicate.Measure(Source);
        var files = Directory.GetFiles(Source, "*", SearchOption.AllDirectories);
        var ex = Assert.Throws<IOException>(() => BaseDuplicate.CopyFiles(Source, target, new SyncProgress(copied =>
        {
            if (copied != total) return;
            // The last file is still being read; the one copied before it is written to (on Windows only it can be).
            foreach (var file in files)
                try { File.AppendAllText(file, "changed"); } catch (IOException) { }
        })));
        Assert.Contains("изменился во время копирования", ex.Message);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Leftovers());
    }

    [Fact] public void CopyGivesTheSameFilesFoldersAndDates()
    {
        var target = Path.Combine(directory, "trade copy");
        long reported = 0;
        BaseDuplicate.CopyFiles(Source, target, new SyncProgress(x => reported = x));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "1Cv8.1CD")), File.ReadAllBytes(Path.Combine(target, "1Cv8.1CD")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(target, "1Cv8Log", "1Cv8.lgf")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(Source, "1Cv8.1CD")), File.GetLastWriteTimeUtc(Path.Combine(target, "1Cv8.1CD")));
        Assert.Equal(BaseDuplicate.Measure(Source), reported);
        Assert.Empty(Leftovers());
    }

    [Fact] public void FileAttributesAreKept()
    {
        var log = Path.Combine(Source, "1Cv8Log", "1Cv8.lgf");
        var target = Path.Combine(directory, "attributes");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(log, FileAttributes.ReadOnly | FileAttributes.Hidden);
            try
            {
                BaseDuplicate.CopyFiles(Source, target);
                var copied = File.GetAttributes(Path.Combine(target, "1Cv8Log", "1Cv8.lgf"));
                Assert.True(copied.HasFlag(FileAttributes.ReadOnly) && copied.HasFlag(FileAttributes.Hidden));
                Assert.False(File.GetAttributes(Path.Combine(target, "1Cv8.1CD")).HasFlag(FileAttributes.ReadOnly));
            }
            finally
            {
                File.SetAttributes(log, FileAttributes.Normal);
                if (Directory.Exists(target)) foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        else
        {
            File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.GroupRead);
            BaseDuplicate.CopyFiles(Source, target);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, File.GetUnixFileMode(Path.Combine(target, "1Cv8Log", "1Cv8.lgf")));
        }
    }

    [Fact] public void EmptyTargetFolderIsAccepted()
    {
        var target = Path.Combine(directory, "prepared"); Directory.CreateDirectory(target);
        BaseDuplicate.CopyFiles(Source, target);
        Assert.True(File.Exists(Path.Combine(target, "1Cv8.1CD")));
    }

    [Fact] public void WrongTargetsAreRejectedWithoutWriting()
    {
        var busy = Path.Combine(directory, "busy"); Directory.CreateDirectory(busy); File.WriteAllText(Path.Combine(busy, "x.txt"), "x");
        var file = Path.Combine(directory, "file"); File.WriteAllText(file, "x");
        foreach (var target in new[] { "", Source, Source + Path.DirectorySeparatorChar, Path.Combine(Source, "inner"), directory, busy, file,
            "relative", Path.Combine(directory, "missing", "base"), Path.Combine(directory, "quote\"d") })
            Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(Source, target));
        Assert.Equal("x", File.ReadAllText(Path.Combine(busy, "x.txt")));
        Assert.False(Directory.Exists(Path.Combine(Source, "inner")));
        Assert.Empty(Leftovers());
        // Not a base: no 1Cv8.1CD.
        Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(busy, Path.Combine(directory, "other")));
    }

    [Fact] public void CancelledCopyLeavesNothing()
    {
        var target = Path.Combine(directory, "cancelled");
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => BaseDuplicate.CopyFiles(Source, target, new SyncProgress(x => { if (x > 1_000_000) cancel.Cancel(); }), cancel.Token));
        Assert.False(Directory.Exists(target));
        Assert.Empty(Leftovers());
    }

    [Fact] public void BaseOpenForWritingIsNotCopiedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(directory, "open");
        // As 1C keeps the database file open for writing.
        using (new FileStream(Path.Combine(Source, "1Cv8.1CD"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var ex = Assert.Throws<IOException>(() => BaseDuplicate.CopyFiles(Source, target));
            Assert.Contains("Завершите все сеансы 1С", ex.Message);
        }
        Assert.False(Directory.Exists(target));
        Assert.Empty(Leftovers());
    }

    [Fact] public void CloneKeepsSettingsWithANewIdentity()
    {
        var source = new BaseEntry { Name = "Торговля", Location = Source, Group = "Работа", Executable = "/opt/1cv8", PlatformVersion = "8.3.27" };
        var clone = BaseDuplicate.Clone(source, " Торговля (копия) ", Source + "_copy" + Path.DirectorySeparatorChar);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(("Торговля (копия)", Source + "_copy", "Работа", "/opt/1cv8", "8.3.27"), (clone.Name, clone.Location, clone.Group, clone.Executable, clone.PlatformVersion));
    }

    [Fact] public void V8iCopyKeepsEveryLineAndStandsAfterTheSource()
    {
        var list = Path.Combine(directory, "ibases.v8i");
        File.WriteAllText(list, "[Торговля]\r\nConnect=File=\"C:\\trade\";Usr=\"sample\";\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/Работа\r\n" +
            "OrderInList=16384\r\nApp=ThinClient\r\nVersion=8.3.27\r\nCustom=retain\r\n[Другая]\r\nConnect=File=\"C:\\other\";\r\nID=33333333-3333-3333-3333-333333333333\r\nFolder=/Работа\r\n",
            new UTF8Encoding(false));
        var store = new V8iStore(list); var library = store.Load(new());
        // A full path of this OS: Clone normalises it, and «C:\…» is a relative name on Linux.
        var target = OperatingSystem.IsWindows() ? @"C:\trade_copy" : "/srv/trade_copy";
        var clone = BaseDuplicate.Clone(library.Bases[0], "Торговля (копия)", target);
        store.Save(library with { Bases = [.. library.Bases, clone] }, copies: new Dictionary<Guid, Guid> { [clone.Id] = library.Bases[0].Id });
        var text = File.ReadAllText(list);
        var copy = text[text.IndexOf("[Торговля (копия)]", StringComparison.Ordinal)..text.IndexOf("[Другая]", StringComparison.Ordinal)];
        // The new folder in the 1C format; the other connection parameters stay as written.
        Assert.Contains("Connect=File=\"" + target + "\";Usr=\"sample\";\r\n", copy);
        Assert.Contains("ID=" + clone.Id, copy);
        foreach (var line in new[] { "Folder=/Работа", "OrderInList=16384", "App=ThinClient", "Version=8.3.27", "Custom=retain" }) Assert.Contains(line, copy);
        Assert.StartsWith("[Торговля]\r\nConnect=File=\"C:\\trade\";Usr=\"sample\";\r\nID=22222222-2222-2222-2222-222222222222", text);
        var reloaded = new V8iStore(list).Load(new());
        Assert.Equal(["Торговля", "Торговля (копия)", "Другая"], reloaded.Bases.Select(x => x.Name));
        Assert.Equal(clone.Id, reloaded.Bases[1].Id);
    }

    // A junction on Windows (no privilege needed), a symbolic link elsewhere.
    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "junction was not created");
    }
    private static void Unlink(string link) { if (Directory.Exists(link)) Directory.Delete(link); }

    // C:\PORTABLE was a volume mounted into a folder without a drive letter: .NET resolves it to itself, and every profile
    // write failed with «Слишком длинная цепочка ссылок». A junction to a volume GUID path is the same reparse point and needs
    // no privilege; on a volume with a letter it resolves to that letter. Every name of one folder must stay one place:
    // the protected files of the program, the nesting of a copy and the listed bases are checked through such names.
    [Fact] public void VolumeNamesAreOneFolderAndNoLinkLoop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetPathRoot(directory)!;
        var volume = new StringBuilder(64);
        Assert.True(GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity), "no volume name for " + root);
        var relative = directory[root.Length..];
        var mount = Path.Combine(directory, "MOUNT"); Link(mount, volume.ToString());
        var inner = Path.Combine(directory, "SUBDIR"); Link(inner, volume + Path.Combine(relative, "trade"));
        try
        {
            Assert.StartsWith("Volume{", new DirectoryInfo(mount).LinkTarget);
            Assert.StartsWith("Volume{", new DirectoryInfo(inner).LinkTarget);
            var alias = Path.Combine(mount, relative);
            Assert.Equal(BaseDuplicate.Physical(directory), BaseDuplicate.Physical(alias));
            Assert.Equal(BaseDuplicate.Physical(Source), BaseDuplicate.Physical(inner));
            // A protected file is not replaced through either name.
            var protectedFile = Path.Combine(Source, "protected.v8i"); File.WriteAllText(protectedFile, "KEEP");
            foreach (var name in new[] { Path.Combine(alias, "trade", "protected.v8i"), Path.Combine(inner, "protected.v8i") })
                Assert.Throws<ArgumentException>(() => ListMaintenance.Export(name, "OVERWRITTEN"u8.ToArray(), [protectedFile]));
            Assert.Equal("KEEP", File.ReadAllText(protectedFile));
            // A copy never goes inside its source through either name.
            foreach (var target in new[] { Path.Combine(alias, "trade", "child"), Path.Combine(inner, "child") })
                Assert.Contains("вложены", Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(Source, target)).Message);
            Assert.False(Directory.Exists(Path.Combine(Source, "child")));
            // A listed base is found as listed under either name.
            var bases = new[] { new BaseEntry { Name = "A", Kind = ConnectionKind.File, Location = Source } };
            Assert.True(BaseFinder.Listed(bases, Path.Combine(alias, "trade")));
            Assert.True(BaseFinder.Listed(bases, inner));
            // The profile is written and read back through the volume name, twice: the second save keeps a .bak.
            var settings = Path.Combine(alias, "Settings", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            new LibraryStore(settings).Save(new Library());
            var again = new LibraryStore(settings); again.Save(again.Load());
            Assert.True(File.Exists(Path.Combine(directory, "Settings", "settings.json.bak")));
        }
        finally { Unlink(inner); Unlink(mount); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);

    [Fact] public void LinkedPathsCannotHideNesting()
    {
        var alias = Path.Combine(directory, "alias"); Link(alias, Source);
        try
        {
            // The target lies inside the source through the alias, and the source is reached through the alias.
            Assert.Contains("вложены", Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(Source, Path.Combine(alias, "child"))).Message);
            Assert.Contains("вложены", Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(alias, Path.Combine(Source, "child"))).Message);
            // The alias and the source are one folder.
            Assert.Contains("совпадает", Assert.Throws<ArgumentException>(() => BaseDuplicate.CopyFiles(alias, Source)).Message);
            Assert.False(Directory.Exists(Path.Combine(Source, "child")));
            Assert.Empty(Directory.EnumerateDirectories(Source, ".*"));
            // A copy made through the alias to an unrelated folder is fine.
            BaseDuplicate.CopyFiles(alias, Path.Combine(directory, "through-alias"));
            Assert.True(File.Exists(Path.Combine(directory, "through-alias", "1Cv8.1CD")));
        }
        finally { Unlink(alias); }
    }

    [Fact] public void CancelWhileCreatingFoldersLeavesNothing()
    {
        for (var index = 0; index < 50; index++) Directory.CreateDirectory(Path.Combine(Source, "many", index.ToString()));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BaseDuplicate.CopyFiles(Source, Path.Combine(directory, "pre-cancelled"), cancel: cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(directory, "pre-cancelled")));
        Assert.Empty(Leftovers());
    }

    [Fact] public void ALeftoverThatCannotBeRemovedIsNamed()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(Path.Combine(Source, "zz-second.bin"), new byte[2_000_000]);
        var target = Path.Combine(directory, "held");
        using var cancel = new CancellationTokenSource();
        FileStream? held = null;
        try
        {
            // The copied database file is held open (as by an antivirus scanner) and the copy is cancelled on the next file.
            var ex = Assert.Throws<IncompleteCopyException>(() => BaseDuplicate.CopyFiles(Source, target, new SyncProgress(copied =>
            {
                if (held is not null || copied <= 3_000_000) return;
                var copy = Directory.EnumerateFiles(directory, "1Cv8.1CD", SearchOption.AllDirectories).Single(x => x.Contains(".copying-"));
                held = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
                cancel.Cancel();
            }), cancel.Token));
            Assert.True(ex.Cancelled);
            Assert.True(Directory.Exists(ex.Leftover));
            Assert.Contains(ex.Leftover, ex.Message);
            Assert.False(Directory.Exists(target));
        }
        finally { held?.Dispose(); foreach (var leftover in Leftovers()) Directory.Delete(Path.Combine(directory, leftover), true); }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
}
