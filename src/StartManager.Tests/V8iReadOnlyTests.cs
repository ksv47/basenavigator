using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>A shared list that cannot be written is found on loading; a change of the file is refused, local settings are kept.</summary>
public sealed class V8iReadOnlyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "v8i-readonly-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "ibases.v8i");
    public V8iReadOnlyTests() => Directory.CreateDirectory(directory);
    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.GetFiles(directory)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }

    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string List = "[Бухгалтерия]\r\nConnect=File=\"C:\\buh\";\r\nID=" + A + "\r\nFolder=/\r\nCustom=a\r\n";
    // root writes whatever the permissions say: the refusal cannot be shown under it.
    private static bool Privileged => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    private (V8iStore Store, Library Library) Open()
    {
        var store = new V8iStore(PathName); return (store, store.Load(new()));
    }
    private void Write() => File.WriteAllText(PathName, List, new UTF8Encoding(false));
    private static BaseEntry Base(Library library) => library.Bases.Single(x => x.Id == Guid.Parse(A));

    [Fact] public void WritableListIsNotReadOnlyAndLeavesNoProbe()
    {
        Write();
        var (store, _) = Open();
        Assert.Null(store.ReadOnly);
        Assert.Equal([PathName], Directory.GetFiles(directory));
    }

    [Fact] public void ReadOnlyFileRefusesChangeOfListButKeepsLocalSettings()
    {
        if (Privileged) return;
        Write();
        File.SetAttributes(PathName, FileAttributes.ReadOnly);
        var (store, library) = Open();
        Assert.NotNull(store.ReadOnly);
        var before = File.ReadAllBytes(PathName);

        var renamed = library with { Bases = [Base(library) with { Name = "Новое имя" }] };
        var error = Assert.Throws<ListReadOnlyException>(() => store.Save(renamed));
        Assert.Contains("только для чтения", error.Message);
        Assert.Equal(before, File.ReadAllBytes(PathName));
        Assert.False(File.Exists(PathName + ".bak"));

        // A comment lives in the local settings: the file is not rewritten, so it is saved.
        var saved = store.Save(library with { Bases = [Base(library) with { Comment = "заметка" }] });
        Assert.Equal("заметка", Base(saved).Comment);
        Assert.Equal(before, File.ReadAllBytes(PathName));

        Assert.Throws<ListReadOnlyException>(() => store.Restore(Encoding.UTF8.GetBytes("[Другая]\r\nConnect=File=\"C:\\x\";\r\n")));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Fact] public void ListWrittenAgainAfterTheRightIsBackAndReloaded()
    {
        if (Privileged) return;
        Write();
        File.SetAttributes(PathName, FileAttributes.ReadOnly);
        var (store, _) = Open();
        Assert.NotNull(store.ReadOnly);
        File.SetAttributes(PathName, FileAttributes.Normal);
        var (fresh, library) = Open();
        Assert.Null(fresh.ReadOnly);
        fresh.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] });
        Assert.Contains("[Новое имя]", File.ReadAllText(PathName));
        Assert.Contains("Custom=a", File.ReadAllText(PathName));
    }

    // Windows: the folder is closed by a deny rule for this user, removed again before the test ends.
    private void WithoutFolderRight(string folder, Action check)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { check(); } finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return;
        }
        var info = new DirectoryInfo(folder); var security = info.GetAccessControl();
        var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(rule); info.SetAccessControl(security);
        try { check(); }
        finally { security.RemoveAccessRule(rule); info.SetAccessControl(security); }
    }

    [Fact] public void FolderWithoutWriteRightMakesListReadOnly()
    {
        if (Privileged) return;
        Write();
        WithoutFolderRight(directory, () =>
        {
            var (store, library) = Open();
            Assert.Contains("папку", store.ReadOnly);
            Assert.Throws<ListReadOnlyException>(() => store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] }));
            Assert.Equal(List, File.ReadAllText(PathName));
        });
    }

    [Fact] public void BusyFileDoesNotHideAClosedFolder()
    {
        if (Privileged) return;
        Write();
        WithoutFolderRight(directory, () =>
        {
            using var held = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Contains("папку", ListAccess.ReadOnlyReason(PathName));
        });
    }

    [Fact] public void ProtectedBackupMakesListReadOnlyOnWindows()
    {
        // Linux replaces the .bak by renaming in the folder: its own mode does not matter there.
        if (!OperatingSystem.IsWindows()) return;
        Write();
        File.WriteAllText(PathName + ".bak", "old");
        File.SetAttributes(PathName + ".bak", FileAttributes.ReadOnly);
        var (store, library) = Open();
        Assert.Contains(".bak", store.ReadOnly);
        Assert.Throws<ListReadOnlyException>(() => store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] }));
        Assert.Equal("old", File.ReadAllText(PathName + ".bak"));
        Assert.Equal(List, File.ReadAllText(PathName));
    }

    // Windows: deny rules for this user on the .bak and its folder, removed again before the test ends.
    private void WithDenied(System.Security.AccessControl.FileSystemRights onBackup, System.Security.AccessControl.FileSystemRights onFolder, Action check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var file = new FileInfo(PathName + ".bak"); var fileSecurity = file.GetAccessControl();
        var folder = new DirectoryInfo(directory); var folderSecurity = folder.GetAccessControl();
        var fileRule = new System.Security.AccessControl.FileSystemAccessRule(user, onBackup, System.Security.AccessControl.AccessControlType.Deny);
        var folderRule = onFolder == 0 ? null : new System.Security.AccessControl.FileSystemAccessRule(user, onFolder, System.Security.AccessControl.AccessControlType.Deny);
        fileSecurity.AddAccessRule(fileRule); file.SetAccessControl(fileSecurity);
        if (folderRule is not null) { folderSecurity.AddAccessRule(folderRule); folder.SetAccessControl(folderSecurity); }
        try { check(); }
        finally
        {
            if (folderRule is not null) { folderSecurity = folder.GetAccessControl(); folderSecurity.RemoveAccessRule(folderRule); folder.SetAccessControl(folderSecurity); }
            fileSecurity = file.GetAccessControl(); fileSecurity.RemoveAccessRule(fileRule); file.SetAccessControl(fileSecurity);
        }
    }

    [Fact] public void BackupWithoutWriteRightButReplaceableIsNotReadOnly()
    {
        // The save replaces the .bak by name, it never writes into the old copy: a write refusal on it is no obstacle.
        if (!OperatingSystem.IsWindows()) return;
        Write();
        File.WriteAllText(PathName + ".bak", "old");
        WithDenied(System.Security.AccessControl.FileSystemRights.WriteData, 0, () =>
        {
            var (store, library) = Open();
            Assert.Null(store.ReadOnly);
            store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] });
            Assert.Contains("[Новое имя]", File.ReadAllText(PathName));
        });
        Assert.Equal(List, File.ReadAllText(PathName + ".bak"));
    }

    [Fact] public void BackupThatCannotBeReplacedMakesListReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write();
        File.WriteAllText(PathName + ".bak", "old");
        WithDenied(System.Security.AccessControl.FileSystemRights.Delete, System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, () =>
        {
            var (store, library) = Open();
            Assert.Contains("замену копии .bak", store.ReadOnly);
            Assert.Throws<ListReadOnlyException>(() => store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] }));
        });
        Assert.Equal("old", File.ReadAllText(PathName + ".bak"));
        Assert.Equal(List, File.ReadAllText(PathName));
    }

    // A marker left by a rolled-back write: «before» is the text on disk, so the list loads.
    private void RolledBackMarker(byte[] current) => File.WriteAllLines(PathName + ".writing", ["BaseNavigator write in progress",
        "before=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)),
        "after=" + new string('A', 64), "backup=" + PathName + ".bak"]);

    [Fact] public void ProtectedMarkerMakesListReadOnlyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write();
        RolledBackMarker(File.ReadAllBytes(PathName));
        File.SetAttributes(PathName + ".writing", FileAttributes.ReadOnly);
        var (store, library) = Open();
        Assert.Contains(".writing", store.ReadOnly);
        Assert.Throws<ListReadOnlyException>(() => store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] }));
        Assert.False(File.Exists(PathName + ".bak"));
        Assert.Equal(List, File.ReadAllText(PathName));
    }

    [Fact] public void ClosedFolderBehindALinkMakesListReadOnly()
    {
        // Windows needs a privilege to make a symbolic link; the path logic is shared.
        if (OperatingSystem.IsWindows() || Privileged) return;
        var target = Path.Combine(directory, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "real.v8i"), List, new UTF8Encoding(false));
        var alias = Path.Combine(directory, "alias.v8i");
        File.CreateSymbolicLink(alias, Path.Combine(target, "real.v8i"));
        WithoutFolderRight(target, () =>
        {
            var store = new V8iStore(alias); var library = store.Load(new());
            Assert.Contains("ссылается", store.ReadOnly);
            Assert.Throws<ListReadOnlyException>(() => store.Save(library with { Bases = [Base(library) with { Name = "Новое имя" }] }));
            Assert.False(File.Exists(alias + ".bak"));
        });
    }

    [Fact] public void LocalSettingsOfABaseWithoutIdAreSavedWithoutWritingTheId()
    {
        if (Privileged) return;
        File.WriteAllText(PathName, string.Join(Environment.NewLine, "[Без ID]", "Connect=File=\"C:/base\";", "Folder=/", "Custom=keep", ""), new UTF8Encoding(false));
        File.SetAttributes(PathName, FileAttributes.ReadOnly);
        var (store, library) = Open();
        var before = File.ReadAllBytes(PathName);
        var entry = Assert.Single(library.Bases);
        // The properties window always asks to fix the ID; a read-only list keeps the computed one.
        var saved = store.Save(library with { Bases = [entry with { Comment = "заметка" }] }, ensureId: entry.Id);
        Assert.Equal("заметка", Assert.Single(saved.Bases).Comment);
        Assert.Equal(before, File.ReadAllBytes(PathName));
        Assert.False(store.HasPersistentId(entry.Id));
        Assert.Equal(entry.Id, Assert.Single(Open().Library.Bases).Id);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void InterruptedWriteIsNotRecoveredOnAReadOnlyList(bool restore, bool closedFolder)
    {
        if (Privileged) return;
        Write();
        var (store, library) = Open();
        var before = File.ReadAllBytes(PathName);
        store.Save(library with { Bases = [Base(library) with { Name = "Новое длинное название базы" }] });
        var after = File.ReadAllBytes(PathName);
        var torn = after[..(after.Length / 2)].Concat(before[(after.Length / 2)..]).ToArray();
        File.WriteAllBytes(PathName, torn);
        // The marker of a write that stopped half-way, as V8iTests writes it.
        File.WriteAllLines(PathName + ".writing", ["BaseNavigator write in progress",
            "before=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)),
            "after=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(after)),
            "backup=" + PathName + ".bak"]);
        // Either the file itself is read-only or it is writable in a folder where nothing can be made.
        if (!closedFolder) File.SetAttributes(PathName, FileAttributes.ReadOnly);
        void Check()
        {
            var seen = Assert.Throws<InterruptedWriteException>(() => new V8iStore(PathName).Load(new())).Current;
            var error = Assert.Throws<ListReadOnlyException>(() =>
            {
                if (restore) V8iStore.RecoverInterruptedWrite(PathName, seen); else V8iStore.DismissInterruptedWrite(PathName, seen);
            });
            Assert.Contains(".bak", error.Message);
            Assert.Equal(torn, File.ReadAllBytes(PathName));
            Assert.True(File.Exists(PathName + ".writing"));
        }
        if (closedFolder) WithoutFolderRight(directory, Check); else Check();
    }

    [Fact] public void BusyFileIsNotTakenForReadOnly()
    {
        Write();
        // Another program holds the file without sharing writes: the save reports it as busy, the list is not read-only.
        using (new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!OperatingSystem.IsWindows()) return; // Linux has no mandatory sharing locks.
            Assert.Null(ListAccess.ReadOnlyReason(PathName));
        }
    }
}
