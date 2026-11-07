using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Сервис → Список информационных баз»: bases that are gone, a changed drive letter, a copy of a local list.
public sealed class ListMaintenanceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-list-" + Guid.NewGuid().ToString("N"));
    public ListMaintenanceTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static BaseEntry File(string location) => new() { Name = "Ф " + location, Kind = ConnectionKind.File, Location = location };

    [Fact] public void OnlyFileBasesWithoutTheirFileAreMissing()
    {
        var present = Path.Combine(folder, "present"); Directory.CreateDirectory(present);
        System.IO.File.WriteAllText(Path.Combine(present, "1Cv8.1CD"), "base");
        var empty = Path.Combine(folder, "empty"); Directory.CreateDirectory(empty);
        var bases = new[]
        {
            File(present), File(empty), File(Path.Combine(folder, "gone")),
            new BaseEntry { Name = "S", Kind = ConnectionKind.Server, Location = @"srv\base" },
            new BaseEntry { Name = "W", Kind = ConnectionKind.Web, Location = "https://srv/erp" },
        };
        Assert.Equal([empty, Path.Combine(folder, "gone")], ListMaintenance.Missing(bases).Select(x => x.Location));
        Assert.True(BaseInfo.Read(File(empty), "").BaseMissing);
        Assert.False(BaseInfo.Read(File(present), "").BaseMissing);
        Assert.False(BaseInfo.Read(bases[3], "").BaseMissing);
    }

    [Fact] public void PathsOfAnotherSystemAndRelativePathsAreNotMissing()
    {
        var foreign = OperatingSystem.IsWindows() ? "/home/user/bases/buh" : @"C:\bases\buh";
        var unc = @"\\server\share\base";
        var bases = new[] { File(foreign), File("relative/base"), File(unc) };
        Assert.Equal(ListMaintenance.Presence.Unknown, ListMaintenance.Check(foreign));
        Assert.Equal(ListMaintenance.Presence.Unknown, ListMaintenance.Check("relative/base"));
        Assert.DoesNotContain(ListMaintenance.Missing(bases), x => x.Location == foreign || x.Location == "relative/base");
        Assert.False(BaseInfo.Read(File(foreign), "").BaseMissing);
        Assert.Contains("другой операционной системы", BaseInfo.Read(File(foreign), "").Note);
        if (!OperatingSystem.IsWindows()) Assert.Equal(ListMaintenance.Presence.Unknown, ListMaintenance.Check(unc));
    }

    [Fact] public void UnreadableFolderIsNotMissing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var locked = Path.Combine(folder, "locked"); Directory.CreateDirectory(locked);
        System.IO.File.WriteAllText(Path.Combine(locked, "1Cv8.1CD"), "base");
        System.IO.File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // root reads any folder: then the file is simply found.
            Assert.NotEqual(ListMaintenance.Presence.Missing, ListMaintenance.Check(locked));
            Assert.Empty(ListMaintenance.Missing([File(locked)]));
        }
        finally { System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact] public void BaseUnderAnUnreadableParentIsNotMissing()
    {
        var parent = Path.Combine(folder, "closed"); var base_ = Path.Combine(parent, "base");
        Directory.CreateDirectory(base_); System.IO.File.WriteAllText(Path.Combine(base_, "1Cv8.1CD"), "base");
        var gone = Path.Combine(folder, "open", "gone"); Directory.CreateDirectory(Path.Combine(folder, "open"));
        void Close(bool closed)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(parent); var acl = info.GetAccessControl();
                // Inherited «deny read», as a folder closed for this user: the base folder itself becomes invisible.
                var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.Read,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
                if (closed) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
                info.SetAccessControl(acl);
            }
            else System.IO.File.SetUnixFileMode(parent, closed ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Close(true);
        try
        {
            // root and the administrators' bypass may still see the base: then it is found, never missing.
            Assert.NotEqual(ListMaintenance.Presence.Missing, ListMaintenance.Check(base_));
            Assert.Empty(ListMaintenance.Missing([File(base_)]));
        }
        finally { Close(false); }
        Assert.Equal(ListMaintenance.Presence.Missing, ListMaintenance.Check(gone));
    }

    [Fact] public void CopyNeverReplacesAFileOfTheProgram()
    {
        var profile = Path.Combine(folder, "library.json");
        new LibraryStore(profile).Save(new Library());
        var before = System.IO.File.ReadAllBytes(profile);
        var own = new[] { profile, profile + ".bak" };
        Assert.Throws<ArgumentException>(() => ListMaintenance.Export(profile, [1, 2, 3], own));
        Assert.Throws<ArgumentException>(() => ListMaintenance.Export(Path.Combine(folder, ".", "library.json"), [1, 2, 3], own));
        var link = Path.Combine(folder, "link");
        try { Directory.CreateSymbolicLink(link, folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { link = null; } // Windows without the right to create links.
        if (link is not null) Assert.Throws<ArgumentException>(() => ListMaintenance.Export(Path.Combine(link, "library.json"), [1, 2, 3], own));
        Assert.Equal(before, System.IO.File.ReadAllBytes(profile));
        new LibraryStore(profile).Load();
    }

    [Theory]
    [InlineData("Original]\r\n[Injected", "")]
    [InlineData("Имя", "8.3\r\nApp=WebClient")]
    public void ExportRefusesLineBreaks(string name, string version)
    {
        var library = new Library { Bases = [new BaseEntry { Name = name, Kind = ConnectionKind.File, Location = @"C:\test", PlatformVersion = version }] };
        Assert.Contains("перевод строки", Assert.Throws<ArgumentException>(() => ListMaintenance.ToV8i(library)).Message);
    }

    [Fact] public void ExportGivesANewRecordVersion83() =>
        Assert.Contains("Version=8.3\r\n", ListMaintenance.ToV8i(new Library { Bases = [File(@"C:\test")] }));

    [Fact] public void DriveLetterChangesOnlyFileBasesOnThatDrive()
    {
        var library = new Library { Bases = [File(@"X:\bases\a"), File("x:/bases/b"), File(@"Y:\c"), File(@"\\X:\share"), File("X:"),
            File(@"XY:\odd"), new BaseEntry { Name = "S", Kind = ConnectionKind.Server, Location = @"X:\srv" }] };
        var (next, changed) = ListMaintenance.ReplaceDrive(library, 'x', 'D');
        Assert.Equal(3, changed);
        Assert.Equal([@"D:\bases\a", "D:/bases/b", @"Y:\c", @"\\X:\share", "D:", @"XY:\odd", @"X:\srv"], next.Bases.Select(x => x.Location));
        Assert.Equal(library.Bases.Select(x => x.Id), next.Bases.Select(x => x.Id));
        Assert.Equal(3, ListMaintenance.CountOnDrive(library.Bases, 'X'));
        Assert.Throws<ArgumentException>(() => ListMaintenance.ReplaceDrive(library, 'X', 'x'));
        Assert.Throws<ArgumentException>(() => ListMaintenance.ReplaceDrive(library, '1', 'D'));
    }

    [Fact] public void LocalListIsExportedAsAV8iThatReadsBack()
    {
        var entry = new BaseEntry { Name = "Бухгалтерия [2026]", Kind = ConnectionKind.File, Location = @"C:\bases\buh; test", Group = "Работа/Отдел", PlatformVersion = "8.3.27" };
        var server = new BaseEntry { Name = "ЗУП", Kind = ConnectionKind.Server, Location = @"srv\zup" };
        var web = new BaseEntry { Name = "Демо", Kind = ConnectionKind.Web, Location = "https://srv/demo" };
        var library = new Library { Bases = [entry, server, web], Groups = ["Пустая"] };
        var text = ListMaintenance.ToV8i(library);
        Assert.Equal(text, ListMaintenance.ToV8i(library));
        var path = Path.Combine(folder, "out.v8i");
        ListMaintenance.Export(path, ListMaintenance.Utf8WithBom(text));
        Assert.Equal([0xEF, 0xBB, 0xBF], System.IO.File.ReadAllBytes(path)[..3]);
        var store = new V8iStore(path); var loaded = store.Load(new());
        Assert.Empty(store.Warnings);
        Assert.Equal(["Бухгалтерия [2026]", "ЗУП", "Демо"], loaded.Bases.Select(x => x.Name));
        Assert.Equal(library.Bases.Select(x => (x.Id, x.Kind, x.Location, x.Group)), loaded.Bases.Select(x => (x.Id, x.Kind, x.Location, x.Group)));
        Assert.Equal("8.3.27", loaded.Bases[0].PlatformVersion);
        Assert.Contains("Пустая", loaded.Groups); Assert.Contains("Работа", loaded.Groups); Assert.Contains("Работа/Отдел", loaded.Groups);
        // A second export replaces the chosen file.
        ListMaintenance.Export(path, Encoding.UTF8.GetBytes("x"));
        Assert.Equal("x", System.IO.File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }
}
