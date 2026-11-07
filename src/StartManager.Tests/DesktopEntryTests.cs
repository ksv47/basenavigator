using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class DesktopEntryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BaseNavigator-menu-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(directory, "data");
    private string Program => Path.Combine(directory, "Базо Навигатор", "BaseNavigator");
    private Func<string, string?> Variables => name => name == "XDG_DATA_HOME" ? Data : null;
    private string Entry => DesktopEntry.EntryPath(Data);
    private string Icon => DesktopEntry.IconPath(Data);

    public DesktopEntryTests()
    {
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Program)!, "Assets"));
        File.WriteAllBytes(Program, []);
    }

    public void Dispose() => Directory.Delete(directory, true);

    private void Asset(byte[] bytes) => File.WriteAllBytes(DesktopEntry.AssetPath(Program), bytes);

    [Fact]
    public void DataHomeIsXdgWhenAbsoluteOtherwiseHome()
    {
        Assert.Equal("/xdg", DesktopEntry.DataHome(n => n == "XDG_DATA_HOME" ? "/xdg" : "/home/u"));
        Assert.Equal(Path.Combine("/home/u", ".local", "share"), DesktopEntry.DataHome(n => n == "HOME" ? "/home/u" : null));
        Assert.Equal(Path.Combine("/home/u", ".local", "share"), DesktopEntry.DataHome(n => n == "XDG_DATA_HOME" ? "relative/data" : "/home/u"));
        Assert.Equal(Path.Combine("/home/u", ".local", "share"), DesktopEntry.DataHome(n => n == "XDG_DATA_HOME" ? "" : "/home/u"));
        Assert.Contains("HOME", Assert.Throws<InvalidOperationException>(() => DesktopEntry.DataHome(_ => null)).Message);
        Assert.Throws<InvalidOperationException>(() => DesktopEntry.DataHome(n => n == "HOME" ? "home" : null));
        Assert.Equal(Path.Combine("/d", "applications", "basenavigator.desktop"), DesktopEntry.EntryPath("/d"));
        Assert.Equal(Path.Combine("/d", "icons", "hicolor", "256x256", "apps", "basenavigator.png"), DesktopEntry.IconPath("/d"));
    }

    [Fact]
    public void ContentQuotesThePathAndMarksOwnership()
    {
        var text = DesktopEntry.Content("/opt/Базо Навигатор/BaseNavigator", true);
        Assert.Equal(
            "[Desktop Entry]\nType=Application\nVersion=1.0\nName=БазоНавигатор\nGenericName=Список баз 1С\n" +
            "Comment=Запуск информационных баз 1С\nExec=\"/opt/Базо Навигатор/BaseNavigator\"\nIcon=basenavigator\nTerminal=false\n" +
            "Categories=Office;\nKeywords=1С;1C;базы;\nStartupNotify=true\nStartupWMClass=BaseNavigator\nX-BaseNavigator-Owner=true\n", text);
        Assert.Contains("\nIcon=application-x-executable\n", DesktopEntry.Content("/opt/app", false));
        Assert.Contains("Exec=\"/opt/50%%/BaseNavigator\"", DesktopEntry.Content("/opt/50%/BaseNavigator", true));
        Assert.Equal(text.Replace("X-BaseNavigator-Owner", "X-BaseNavigator-Icon-SHA256=ab;cd;\nX-BaseNavigator-Owner"),
            DesktopEntry.Content("/opt/Базо Навигатор/BaseNavigator", true, ["ab", "cd"]));
        Assert.Equal(text, DesktopEntry.Content("/opt/Базо Навигатор/BaseNavigator", true, []));
        Assert.DoesNotContain("SHA256", DesktopEntry.Content("/opt/app", false, ["ab"]));
        Assert.Throws<ArgumentException>(() => DesktopEntry.Content("relative/BaseNavigator", true));
        Assert.Throws<ArgumentException>(() => DesktopEntry.Content("/opt/a=b/BaseNavigator", true));
    }

    [Fact]
    public void InstallWritesEntryAndIconAndReinstallUpdatesThePath()
    {
        Asset([1, 2, 3]);
        Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables));
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([1, 2, 3])]), File.ReadAllText(Entry));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon));
        Assert.Equal((byte)'[', File.ReadAllBytes(Entry)[0]); // no UTF-8 BOM
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(Entry));

        var moved = Path.Combine(directory, "new place", "BaseNavigator");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        // The new place has no Assets: the icon we wrote stays ours, the entry keeps its recorded hash.
        DesktopEntry.Install(moved, Variables);
        Assert.Equal(DesktopEntry.Content(moved, true, [Sha([1, 2, 3])]), File.ReadAllText(Entry));
        Assert.Equal([Entry], Directory.GetFiles(Path.GetDirectoryName(Entry)!));
        Assert.Equal([Icon], Directory.GetFiles(Path.GetDirectoryName(Icon)!));
        AssertRemoval([Icon, Entry], null, DesktopEntry.Uninstall(Variables));
        Assert.False(File.Exists(Icon));
    }

    private static void AssertRemoval(string[] removed, string? note, (IReadOnlyList<string> Removed, string? Note) result)
    {
        Assert.Equal(removed, result.Removed);
        Assert.Equal(note, result.Note);
    }

    [Fact]
    public void EntryIsWrittenFirstSoAFailedIconWriteStaysOurs()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        Asset([4, 5]);
        if (!FailIconWrite(() => Assert.ThrowsAny<Exception>(() => DesktopEntry.Install(Program, Variables)))) return;
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon)); // the icon was not replaced; the planned hash was rolled back
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([1, 2, 3])]), File.ReadAllText(Entry));

        // Reinstall replaces the icon that is still ours; the next one trims the list to the current picture.
        Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables));
        Assert.Equal([4, 5], File.ReadAllBytes(Icon));
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([4, 5]), Sha([1, 2, 3])]), File.ReadAllText(Entry));
        DesktopEntry.Install(Program, Variables);
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([4, 5])]), File.ReadAllText(Entry));

        // Assets changed again and the icon write failed, then Assets disappeared: uninstall still removes our icon.
        Asset([6]);
        Assert.True(FailIconWrite(() => Assert.ThrowsAny<Exception>(() => DesktopEntry.Install(Program, Variables))));
        File.Delete(DesktopEntry.AssetPath(Program));
        AssertRemoval([Icon, Entry], null, DesktopEntry.Uninstall(Variables));
        Assert.False(File.Exists(Icon));
    }

    [Fact]
    public void FailedIconWriteDoesNotClaimThePlannedPicture()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        Asset([4, 5]);
        DesktopEntryResult? failed = null;
        if (!FailIconWrite(() => failed = DesktopEntryCommand.Apply(DesktopCommand.Install, Program, Variables, (_, _) => true))) return;
        // The entry was already written, so the message must not say it was not created.
        Assert.NotNull(failed);
        Assert.StartsWith("Ярлык в меню приложений обновлён, но значок не записан: ", failed.Error);
        Assert.Empty(failed.Lines);
        Assert.DoesNotContain(Sha([4, 5]), File.ReadAllText(Entry));
        // The user puts a file with the never-installed picture's bytes: it is theirs and stays.
        File.WriteAllBytes(Icon, [4, 5]);
        AssertRemoval([Entry], "Значок " + Icon + " изменён — оставлен.", DesktopEntry.Uninstall(Variables));
        Assert.Equal([4, 5], File.ReadAllBytes(Icon));

        // The icon was absent and its write failed: the entry records no hash.
        File.Delete(Icon);
        DesktopEntry.Install(Program, Variables); // creates the icon folder state anew
        File.Delete(Icon);
        if (OperatingSystem.IsWindows()) return; // the read-only attribute needs an existing icon
        Assert.True(FailIconWrite(() => Assert.ThrowsAny<Exception>(() => DesktopEntry.Install(Program, Variables))));
        Assert.Equal(DesktopEntry.Content(Program, true, []), File.ReadAllText(Entry));
    }

    [Fact]
    public void UnreadableOwnIconKeepsItsHash()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        Asset([4, 5]);
        var note = "Значок " + Icon + " не удалось прочитать — оставлен.";
        if (!LockIcon(() => Assert.Equal((Entry, note), DesktopEntry.Install(Program, Variables)))) return;
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([1, 2, 3])]), File.ReadAllText(Entry));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon));
        // Readable again: still ours, so uninstall removes it.
        AssertRemoval([Icon, Entry], null, DesktopEntry.Uninstall(Variables));

        // Unreadable during uninstall: kept with the note, our entry is removed.
        DesktopEntry.Install(Program, Variables);
        Assert.True(LockIcon(() => AssertRemoval([Entry], note, DesktopEntry.Uninstall(Variables))));
        Assert.Equal([4, 5], File.ReadAllBytes(Icon));
        Assert.False(File.Exists(Entry));
    }

    // Makes the installed icon unreadable: held open without sharing on Windows, mode 000 on Unix. False as root.
    private bool LockIcon(Action action)
    {
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(Icon, FileMode.Open, FileAccess.Read, FileShare.None);
            action();
            return true;
        }
        if (Environment.UserName == "root") return false;
        var mode = File.GetUnixFileMode(Icon);
        File.SetUnixFileMode(Icon, UnixFileMode.None);
        try { action(); }
        finally { File.SetUnixFileMode(Icon, mode); }
        return true;
    }

    // Makes replacing the icon fail while the entry folder stays writable: on Windows the icon is made read-only, so the
    // replacing move is refused; on Unix the icon folder is made read-only. False as root (nothing to simulate).
    private bool FailIconWrite(Action action)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Icon, FileAttributes.ReadOnly);
            try { action(); }
            finally { File.SetAttributes(Icon, FileAttributes.Normal); }
            return true;
        }
        if (Environment.UserName == "root") return false;
        var folder = Path.GetDirectoryName(Icon)!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { action(); }
        finally { File.SetUnixFileMode(folder, mode); }
        return true;
    }

    [Fact]
    public void MissingOrUnreadableAssetDoesNotBlockAnything()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        var asset = DesktopEntry.AssetPath(Program);
        var locked = false;
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(asset, FileMode.Open, FileAccess.Read, FileShare.None);
            UnreadableAssetChecks();
            locked = true;
        }
        else if (Environment.UserName != "root")
        {
            File.SetUnixFileMode(asset, UnixFileMode.None);
            try { UnreadableAssetChecks(); }
            finally { File.SetUnixFileMode(asset, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            locked = true;
        }
        if (!locked) DesktopEntry.Uninstall(Variables); // root: the asset cannot be made unreadable
        Assert.False(File.Exists(Icon));

        // A missing asset: the entry falls back to the generic icon, uninstall removes it.
        File.Delete(asset);
        DesktopEntry.Install(Program, Variables);
        Assert.Equal(DesktopEntry.Content(Program, false), File.ReadAllText(Entry));
        AssertRemoval([Entry], null, DesktopEntry.Uninstall(Variables));
    }

    // The asset cannot be read: install keeps the icon that is ours and its hash; uninstall never reads the asset.
    private void UnreadableAssetChecks()
    {
        Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables));
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([1, 2, 3])]), File.ReadAllText(Entry));
        AssertRemoval([Icon, Entry], null, DesktopEntry.Uninstall(Variables));
    }

    [Fact]
    public void RecordedHashListIsTolerantAndBounded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Icon)!);
        File.WriteAllBytes(Icon, [7]);
        var hash = Sha([7]);
        void Own(string value) => File.WriteAllText(Entry,
            "[Desktop Entry]\nName=X\nIcon=basenavigator\nX-BaseNavigator-Icon-SHA256=" + value + "\nX-BaseNavigator-Owner=true\n");

        // Spaces, empty items, upper case and a missing trailing ';' are tolerated; non-hex items are ignored.
        Own(" zz ; ;" + new string('g', 64) + "; " + hash.ToUpperInvariant() + " ");
        AssertRemoval([Icon, Entry], null, DesktopEntry.Uninstall(Variables));

        // Only the first MaxIconHashes items count.
        File.WriteAllBytes(Icon, [7]);
        Own(string.Concat(Enumerable.Repeat(Sha([0]) + ";", DesktopEntry.MaxIconHashes)) + hash + ";");
        AssertRemoval([Entry], "Значок " + Icon + " изменён — оставлен.", DesktopEntry.Uninstall(Variables));
        Assert.True(File.Exists(Icon));

        // A truncated hash is not a hash.
        Own(hash[..63] + ";");
        AssertRemoval([Entry], null, DesktopEntry.Uninstall(Variables));
        Assert.True(File.Exists(Icon));
    }

    [Fact]
    public void UnknownFileTypeIsRefusedNotOpened()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        var probe = DesktopEntry.TypeProbe;
        try
        {
            DesktopEntry.TypeProbe = _ => DesktopEntry.UnknownType; // statx failed (not ENOENT) or is unavailable
            var problem = "Не удалось определить тип файла " + Entry + "; БазоНавигатор его не изменяет.";
            Assert.Equal((false, problem), DesktopEntryCommand.State(Variables));
            Assert.False(DesktopEntry.IsOwn(Entry));
            Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
            Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => DesktopEntry.Uninstall(Variables)).Message);
            Assert.True(File.Exists(Entry));

            // Only the icon is of unknown type: install refuses, uninstall removes our entry and keeps the icon.
            DesktopEntry.TypeProbe = path => path == Icon ? DesktopEntry.UnknownType : DesktopEntry.RegularType;
            Assert.Equal("Не удалось определить тип файла " + Icon + "; БазоНавигатор его не изменяет.",
                Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
            AssertRemoval([Entry], "Значок " + Icon + " — тип файла не определён — оставлен.", DesktopEntry.Uninstall(Variables));
            Assert.True(File.Exists(Icon));

            // Unknown type of a path that does not exist is simply absent; a FIFO type is a non-regular file.
            File.Delete(Icon);
            DesktopEntry.TypeProbe = _ => DesktopEntry.UnknownType;
            Assert.Equal((false, (string?)null), DesktopEntryCommand.State(Variables));
            DesktopEntry.TypeProbe = path => File.Exists(path) ? 0x1000 : DesktopEntry.AbsentType; // S_IFIFO
            Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables)); // nothing exists yet
            Assert.Equal((false, "Файл " + Entry + " — не обычный файл; БазоНавигатор его не изменяет."), DesktopEntryCommand.State(Variables));
        }
        finally { DesktopEntry.TypeProbe = probe; }
    }

    [Fact]
    public void MissingAssetGivesFallbackIconAndNoIconFile()
    {
        DesktopEntry.Install(Program, Variables);
        Assert.Contains("\nIcon=application-x-executable\n", File.ReadAllText(Entry));
        Assert.False(File.Exists(Icon));
        Assert.Equal([Entry], DesktopEntry.Uninstall(Variables).Removed);
        Assert.False(File.Exists(Entry));
    }

    [Fact]
    public void ForeignEntryIsNeitherOverwrittenNorRemoved()
    {
        Asset([1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        const string foreign = "[Desktop Entry]\nType=Application\nName=Other\nExec=/usr/bin/other\n";
        File.WriteAllText(Entry, foreign);
        var error = Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables));
        Assert.Contains("не БазоНавигатором", error.Message);
        Assert.Equal(foreign, File.ReadAllText(Entry));
        Assert.False(File.Exists(Icon));
        Assert.Empty(DesktopEntry.Uninstall(Variables).Removed);
        Assert.Equal(foreign, File.ReadAllText(Entry));
    }

    [Fact]
    public void UninstallRemovesOwnFilesOnly()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        Assert.Equal([Icon, Entry], DesktopEntry.Uninstall(Variables).Removed);
        Assert.False(File.Exists(Entry)); Assert.False(File.Exists(Icon));
        Assert.True(File.Exists(DesktopEntry.AssetPath(Program)));

        // A different icon without our entry is foreign and stays.
        Directory.CreateDirectory(Path.GetDirectoryName(Icon)!);
        File.WriteAllBytes(Icon, [9]);
        Assert.Empty(DesktopEntry.Uninstall(Variables).Removed);
        Assert.Equal([9], File.ReadAllBytes(Icon));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public void IconOfAnEarlierBuildIsOursByRecordedHash()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        Assert.Contains("\nX-BaseNavigator-Icon-SHA256=" + Sha([1, 2, 3]) + ";\n", File.ReadAllText(Entry));
        Asset([4, 5]); // the program was updated with another picture; the installed icon is untouched
        Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables));
        Assert.Equal([4, 5], File.ReadAllBytes(Icon));
        Assert.Equal(DesktopEntry.Content(Program, true, [Sha([4, 5]), Sha([1, 2, 3])]), File.ReadAllText(Entry));

        Asset([6]); // and again: uninstall removes the untouched icon of the earlier build
        var (removed, note) = DesktopEntry.Uninstall(Variables);
        Assert.Equal([Icon, Entry], removed);
        Assert.Null(note);
        Assert.False(File.Exists(Icon));
    }

    [Fact]
    public void UserModifiedIconIsKeptOnInstallAndUninstall()
    {
        Asset([1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        File.WriteAllBytes(Icon, [7]);
        Assert.Equal((Entry, "Значок " + Icon + " не наш — оставлен."), DesktopEntry.Install(Program, Variables));
        Assert.Equal([7], File.ReadAllBytes(Icon));
        Assert.DoesNotContain(DesktopEntry.IconHashKey, File.ReadAllText(Entry));
        Assert.Equal("Значок " + Icon + " не наш — оставлен.", DesktopEntry.Install(Program, Variables).Note);
        Assert.Equal([7], File.ReadAllBytes(Icon));
        // The entry no longer records a hash, so uninstall keeps the icon silently.
        var (removed, note) = DesktopEntry.Uninstall(Variables);
        Assert.Equal([Entry], removed);
        Assert.Null(note);
        Assert.Equal([7], File.ReadAllBytes(Icon));

        // Modified after install, removed right away: kept, the command reports the note.
        File.Delete(Icon);
        DesktopEntry.Install(Program, Variables);
        File.WriteAllBytes(Icon, [8]);
        var result = DesktopEntryCommand.Apply(DesktopCommand.Uninstall, Program, Variables, (_, _) => true);
        Assert.Null(result.Error);
        Assert.Equal(["Удалён файл: " + Entry, "Значок " + Icon + " изменён — оставлен."], result.Lines);
        Assert.Equal("Значок " + Icon + " изменён — оставлен.", result.Note);
        Assert.Equal([8], File.ReadAllBytes(Icon));
        Assert.False(File.Exists(Entry));
    }

    [Fact]
    public void ForeignIconStaysOnEveryInstall()
    {
        Asset([1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(Icon)!);
        File.WriteAllBytes(Icon, [9]);
        var installed = Command(DesktopCommand.Install);
        Assert.Equal(0, installed.Code);
        Assert.Equal(["Ярлык в меню приложений: " + Entry, "Значок " + Icon + " не наш — оставлен."], installed.Output.Trim().Split(Environment.NewLine));
        Assert.Equal(DesktopEntry.Content(Program, true), File.ReadAllText(Entry));
        // Reinstall: our entry exists and says Icon=basenavigator, but no hash was recorded, so the icon still stays.
        Assert.Equal("Значок " + Icon + " не наш — оставлен.", DesktopEntryCommand.Apply(DesktopCommand.Install, Program, Variables, (_, _) => true).Note);
        Assert.Equal([9], File.ReadAllBytes(Icon));

        // Even an icon identical to the shipped asset is not ours: only a hash our entry recorded makes it ours.
        File.Delete(Entry);
        File.WriteAllBytes(Icon, [1, 2, 3]);
        Assert.Equal((Entry, "Значок " + Icon + " не наш — оставлен."), DesktopEntry.Install(Program, Variables));
        Assert.Equal(DesktopEntry.Content(Program, true), File.ReadAllText(Entry));
        Assert.Equal("Значок " + Icon + " не наш — оставлен.", DesktopEntry.Install(Program, Variables).Note);
        AssertRemoval([Entry], null, DesktopEntry.Uninstall(Variables));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon));

        // An icon larger than the bound is not hashed and not ours, even when our entry records a hash.
        File.Delete(Icon);
        DesktopEntry.Install(Program, Variables);
        using (var big = File.Create(Icon)) big.SetLength(DesktopEntry.MaxIconSize + 1L);
        Assert.Equal("Значок " + Icon + " не наш — оставлен.", DesktopEntry.Install(Program, Variables).Note);
        Assert.Equal(DesktopEntry.MaxIconSize + 1L, new FileInfo(Icon).Length);
        Assert.Equal([Entry], DesktopEntry.Uninstall(Variables).Removed);
    }

    [Fact]
    public void RejectedPathChangesNothing()
    {
        var program = Path.Combine(directory, "a=b", "BaseNavigator");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(program)!, "Assets"));
        File.WriteAllBytes(DesktopEntry.AssetPath(program), [1, 2, 3]);
        var refused = DesktopEntryCommand.Apply(DesktopCommand.Install, program, Variables, (_, _) => true);
        Assert.StartsWith("Ярлык в меню приложений не создан: ", refused.Error);
        Assert.False(File.Exists(Icon));
        Assert.False(File.Exists(Entry));
    }

    [Fact]
    public void NonRegularFileAtTheEntryOrIconPathIsRefused()
    {
        Asset([1, 2, 3]);
        Directory.CreateDirectory(Entry); // a directory with the entry's name
        var problem = "Файл " + Entry + " — не обычный файл; БазоНавигатор его не изменяет.";
        Assert.Equal((false, problem), DesktopEntryCommand.State(Variables));
        Assert.False(DesktopEntry.IsOwn(Entry));
        Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
        var uninstall = Command(DesktopCommand.Uninstall);
        Assert.Equal(1, uninstall.Code);
        Assert.Equal("Ярлык в меню приложений не удалён: " + problem, uninstall.Error.Trim());
        Directory.Delete(Entry);

        // A directory at the icon path: install refuses, uninstall removes our entry and keeps it.
        DesktopEntry.Install(Program, Variables);
        File.Delete(Icon);
        Directory.CreateDirectory(Icon);
        Assert.Contains("не обычный файл", Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
        var removed = Command(DesktopCommand.Uninstall);
        Assert.Equal(0, removed.Code);
        Assert.Equal(["Удалён файл: " + Entry, "Значок " + Icon + " — не обычный файл — оставлен."], removed.Output.Trim().Split(Environment.NewLine));
        Assert.True(Directory.Exists(Icon));
        Assert.Equal(0, Command(DesktopCommand.Uninstall).Code); // nothing of ours: «не найден»
    }

    [Fact]
    public async Task FifoAtTheEntryOrIconPathDoesNotBlock()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Watchdog: a call that opened the FIFO would block forever; the test fails instead of hanging.
        var run = Task.Run(FifoChecks);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        await run;
    }

    private void FifoChecks()
    {
        Asset([1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        Assert.True(MakeFifo(Entry), "mkfifo failed");
        var problem = "Файл " + Entry + " — не обычный файл; БазоНавигатор его не изменяет.";
        Assert.Equal((false, problem), DesktopEntryCommand.State(Variables));
        Assert.False(DesktopEntry.IsOwn(Entry));
        Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
        Assert.Equal(problem, Assert.Throws<InvalidOperationException>(() => DesktopEntry.Uninstall(Variables)).Message);
        File.Delete(Entry);

        DesktopEntry.Install(Program, Variables);
        File.Delete(Icon);
        Assert.True(MakeFifo(Icon));
        Assert.Contains("не обычный файл", Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
        var (removed, note) = DesktopEntry.Uninstall(Variables);
        Assert.Equal([Entry], removed);
        Assert.Equal("Значок " + Icon + " — не обычный файл — оставлен.", note);
        Assert.True(File.Exists(Icon));
    }

    private static bool MakeFifo(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo") { ArgumentList = { path }, UseShellExecute = false });
            if (process is null || !process.WaitForExit(10000)) return false;
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // mkfifo is missing: the caller's assertion fails on Linux
    }

    private static bool FileLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        // Only Windows without the right to create links skips; on Unix a failed setup fails the test.
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void LinkedEntryOrIconIsRefusedAndNothingChanges()
    {
        Asset([1, 2, 3]);
        var outside = Path.Combine(directory, "outside.desktop");
        File.WriteAllText(outside, DesktopEntry.Content(Program, true));
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        if (!FileLink(Entry, outside)) return;
        foreach (var command in new[] { DesktopCommand.Install, DesktopCommand.Uninstall })
        {
            var refused = Command(command);
            Assert.Equal(1, refused.Code);
            Assert.Contains("символическая ссылка", refused.Error);
        }
        Assert.Equal((false, "Файл " + Entry + " — символическая ссылка; БазоНавигатор его не изменяет."), DesktopEntryCommand.State(Variables));
        Assert.Equal(DesktopEntry.Content(Program, true), File.ReadAllText(outside));
        Assert.NotNull(new FileInfo(Entry).LinkTarget);
        Assert.False(File.Exists(Icon));

        // A broken link is refused too.
        File.Delete(outside);
        Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables));
        File.Delete(Entry);

        // The icon file itself as a link: install refuses; uninstall removes our entry and keeps the link.
        var picture = Path.Combine(directory, "outside.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        DesktopEntry.Install(Program, Variables);
        File.Delete(Icon);
        Assert.True(FileLink(Icon, picture));
        Assert.Contains("символическая ссылка", Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables)).Message);
        var removed = Command(DesktopCommand.Uninstall);
        Assert.Equal(0, removed.Code);
        Assert.Equal(["Удалён файл: " + Entry, "Значок " + Icon + " — символическая ссылка — оставлен."], removed.Output.Trim().Split(Environment.NewLine));
        Assert.NotNull(new FileInfo(Icon).LinkTarget);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(picture));
    }

    [Fact]
    public void LinkedParentFolderIsAccepted()
    {
        var real = Path.Combine(directory, "dotfiles", "applications");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(Data);
        try { Directory.CreateSymbolicLink(Path.GetDirectoryName(Entry)!, real); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return; } // Windows without the right to create links.
        Assert.Equal((Entry, (string?)null), DesktopEntry.Install(Program, Variables));
        Assert.True(DesktopEntry.IsOwn(Path.Combine(real, DesktopEntry.FileName)));
        Assert.Equal([Entry], DesktopEntry.Uninstall(Variables).Removed);
        Assert.Empty(Directory.GetFiles(real));
    }

    [Fact]
    public void OwnerKeyCountsOnlyInTheDesktopEntryGroupOfABoundedFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "[Desktop Entry]\nName=Other\n[Desktop Action Own]\nX-BaseNavigator-Owner=true\n");
        Assert.False(DesktopEntry.IsOwn(Entry));
        Assert.Throws<InvalidOperationException>(() => DesktopEntry.Install(Program, Variables));
        Assert.Empty(DesktopEntry.Uninstall(Variables).Removed);
        Assert.True(File.Exists(Entry));

        File.WriteAllText(Entry, "# comment\r\n[Desktop Entry]\r\nName=Own\r\nX-BaseNavigator-Owner=true\r\n[Desktop Action A]\r\nName=A\r\n",
            new System.Text.UTF8Encoding(true));
        Assert.Equal((byte)0xEF, File.ReadAllBytes(Entry)[0]);
        Assert.True(DesktopEntry.IsOwn(Entry));
        File.WriteAllText(Entry, "X-BaseNavigator-Owner=true\n[Desktop Entry]\nName=Other\n");
        Assert.False(DesktopEntry.IsOwn(Entry));

        // Larger than the bound: not ours, even with the key in the group.
        File.WriteAllText(Entry, "[Desktop Entry]\nX-BaseNavigator-Owner=true\n# " + new string('x', DesktopEntry.MaxEntrySize) + "\n");
        Assert.False(DesktopEntry.IsOwn(Entry));
        Assert.Contains("создан не БазоНавигатором", DesktopEntryCommand.State(Variables).Problem);
        var exact = "[Desktop Entry]\nX-BaseNavigator-Owner=true\n#";
        File.WriteAllText(Entry, exact + new string('x', DesktopEntry.MaxEntrySize - exact.Length));
        Assert.Equal(DesktopEntry.MaxEntrySize, new FileInfo(Entry).Length);
        Assert.True(DesktopEntry.IsOwn(Entry));
    }

    [Fact]
    public void IdenticalIconWithoutOurEntryIsKeptAndNothingInstalledIsFine()
    {
        Assert.Empty(DesktopEntry.Uninstall(Variables).Removed);
        Asset([1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(Icon)!);
        File.WriteAllBytes(Icon, [1, 2, 3]);
        Assert.Empty(DesktopEntry.Uninstall(Variables).Removed);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon));

        // A foreign entry with an icon identical to ours: both stay, and the command says why.
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "[Desktop Entry]\nName=Mine\nIcon=basenavigator\n");
        var foreign = Command(DesktopCommand.Uninstall);
        Assert.Equal(1, foreign.Code);
        Assert.Equal("Файл " + Entry + " создан не БазоНавигатором; удалите или переименуйте его, чтобы создать ярлык.", foreign.Error.Trim());
        Assert.Equal("", foreign.Output);
        Assert.Empty(foreign.Tools);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Icon));
        Assert.Equal("[Desktop Entry]\nName=Mine\nIcon=basenavigator\n", File.ReadAllText(Entry));
    }

    private (int Code, string Output, string Error, List<string> Tools) Command(DesktopCommand command, bool linux = true, string? executable = null)
    {
        var output = new StringWriter(); var error = new StringWriter(); var tools = new List<string>();
        var code = DesktopEntryCommand.Run(command, executable ?? Program, linux, Variables, output, error,
            (tool, args) => { tools.Add(tool + " " + string.Join(" ", args)); return false; });
        return (code, output.ToString(), error.ToString(), tools);
    }

    [Fact]
    public void CommandRefusesOutsideLinuxAndForeignExecutables()
    {
        foreach (var command in new[] { DesktopCommand.Install, DesktopCommand.Uninstall })
        {
            var refused = Command(command, linux: false);
            Assert.Equal(2, refused.Code);
            Assert.Equal("Ярлык в меню приложений создаётся только в Linux.", refused.Error.Trim());
            Assert.Equal("", refused.Output);
            var renamed = Command(command, executable: Path.Combine(directory, "StartManager"));
            Assert.Equal(1, renamed.Code);
            Assert.Equal("Создавайте ярлыки из установленной сборки программы.", renamed.Error.Trim());
            Assert.Equal(1, Command(command, executable: "").Code);
        }
        Assert.False(Directory.Exists(Data));
        Assert.Throws<ArgumentOutOfRangeException>(() => Command(DesktopCommand.None));
    }

    [Fact]
    public void CommandInstallsUninstallsAndRefreshesCaches()
    {
        Asset([1, 2, 3]);
        var installed = Command(DesktopCommand.Install);
        Assert.Equal(0, installed.Code);
        Assert.Equal("Ярлык в меню приложений: " + Entry, installed.Output.Trim());
        Assert.True(DesktopEntry.IsOwn(Entry));
        Assert.Equal(["update-desktop-database " + Path.Combine(Data, "applications"),
            "gtk-update-icon-cache -f -t " + Path.Combine(Data, "icons", "hicolor")], installed.Tools);
        // Upper-case or .exe name of the installed program is accepted as in ShortcutWriter.
        Assert.Equal(0, Command(DesktopCommand.Install, executable: Path.Combine(Path.GetDirectoryName(Program)!, "BaseNavigator.exe")).Code);

        var removed = Command(DesktopCommand.Uninstall);
        Assert.Equal(0, removed.Code);
        Assert.Equal(["Удалён файл: " + Icon, "Удалён файл: " + Entry], removed.Output.Trim().Split(Environment.NewLine));
        Assert.Equal(2, removed.Tools.Count);

        var nothing = Command(DesktopCommand.Uninstall);
        Assert.Equal(0, nothing.Code);
        Assert.Equal("Ярлык в меню приложений не найден.", nothing.Output.Trim());
        Assert.Empty(nothing.Tools);
    }

    [Fact]
    public void CommandReportsForeignEntryAndMissingHome()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "[Desktop Entry]\nName=Чужой\n");
        var foreign = Command(DesktopCommand.Install);
        Assert.Equal(1, foreign.Code);
        Assert.StartsWith("Ярлык в меню приложений не создан: ", foreign.Error);
        Assert.Contains("создан не БазоНавигатором", foreign.Error);
        Assert.Empty(foreign.Tools);
        Assert.Equal("[Desktop Entry]\nName=Чужой\n", File.ReadAllText(Entry));

        var error = new StringWriter();
        Assert.Equal(1, DesktopEntryCommand.Run(DesktopCommand.Uninstall, Program, true, _ => null, new StringWriter(), error));
        Assert.StartsWith("Ярлык в меню приложений не удалён: ", error.ToString());
        Assert.Contains("HOME", error.ToString());
    }

    [Fact]
    public void ApplyIsTheCommandWithoutConsoleAndStateFollowsTheEntry()
    {
        var tools = new List<string>();
        bool Refresh(string tool, string[] _) { tools.Add(tool); return false; }
        Assert.Equal((false, (string?)null), DesktopEntryCommand.State(Variables));

        var installed = DesktopEntryCommand.Apply(DesktopCommand.Install, Program, Variables, Refresh);
        Assert.Null(installed.Error);
        Assert.Equal(["Ярлык в меню приложений: " + Entry], installed.Lines);
        Assert.Equal(["update-desktop-database"], tools);
        Assert.Equal((true, (string?)null), DesktopEntryCommand.State(Variables));

        var renamed = DesktopEntryCommand.Apply(DesktopCommand.Uninstall, Path.Combine(directory, "StartManager"), Variables, Refresh);
        Assert.Equal("Создавайте ярлыки из установленной сборки программы.", renamed.Error);
        Assert.Empty(renamed.Lines);
        Assert.True(DesktopEntry.IsOwn(Entry));
        Assert.Equal("Ярлык в меню приложений не удалён: не найден путь к программе.",
            DesktopEntryCommand.Apply(DesktopCommand.Uninstall, null, Variables, Refresh).Error);

        tools.Clear();
        var removed = DesktopEntryCommand.Apply(DesktopCommand.Uninstall, Program, Variables, Refresh);
        Assert.Null(removed.Error);
        Assert.Equal(["Удалён файл: " + Entry], removed.Lines);
        Assert.Equal(["update-desktop-database"], tools);
        Assert.Equal((false, (string?)null), DesktopEntryCommand.State(Variables));
        Assert.Equal([DesktopEntryCommand.NotFound], DesktopEntryCommand.Apply(DesktopCommand.Uninstall, Program, Variables, Refresh).Lines);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopEntryCommand.Apply(DesktopCommand.None, Program, Variables, Refresh));
    }

    [Fact]
    public void StateReportsForeignEntryAndMissingHomeAsProblem()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "[Desktop Entry]\nName=Чужой\n");
        Assert.Equal((false, "Файл " + Entry + " создан не БазоНавигатором; удалите или переименуйте его, чтобы создать ярлык."),
            DesktopEntryCommand.State(Variables));
        var missing = DesktopEntryCommand.State(_ => null);
        Assert.False(missing.Installed);
        Assert.Contains("HOME", missing.Problem);
    }

    [Fact]
    public void RefreshSkipsMissingFoldersAndMissingToolsAreIgnored()
    {
        var tools = new List<string>();
        DesktopEntryCommand.RefreshCaches(Data, (tool, _) => { tools.Add(tool); return true; });
        Assert.Empty(tools);
        Assert.False(DesktopEntryCommand.RunQuietly("basenavigator-missing-tool-" + Guid.NewGuid().ToString("N"), ["x"]));
    }
}
