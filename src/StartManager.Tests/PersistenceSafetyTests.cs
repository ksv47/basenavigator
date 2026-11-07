using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;
public sealed class PersistenceSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-safe-" + Guid.NewGuid().ToString("N"));
    public PersistenceSafetyTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateHardLinkW(string link,string existing,IntPtr reserved);
    [DllImport("libc", SetLastError=true)] private static extern int link(string existing,string target);

    [Theory] [InlineData(false)] [InlineData(true)]
    public void BackupReplacesHardLinkWithoutWritingToItsTarget(bool activity)
    {
        var path=Path.Combine(root,"library.json");
        var store=new LibraryStore(path); store.Save(new Library());
        var history=new ActivityStore(path);
        if(activity)history.SetFavorite("test",Guid.NewGuid(),true);
        var destination=activity?history.PathName:path;
        var victim=Path.Combine(root,"unrelated.txt");File.WriteAllText(victim,"preserve me");
        Assert.True(OperatingSystem.IsWindows()?CreateHardLinkW(destination+".bak",victim,IntPtr.Zero):link(victim,destination+".bak")==0);
        var before=File.ReadAllBytes(destination);
        if(activity)history.SetFavorite("test",Guid.NewGuid(),true);else store.Save(new Library{Executable="future-client"});
        Assert.Equal("preserve me",File.ReadAllText(victim));
        Assert.Equal(before,File.ReadAllBytes(destination+".bak"));
        Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }

    [Theory] [InlineData("json")] [InlineData("activity")] [InlineData("v8i")]
    public void ReplacementAndBackupPreserveExplicitPermissions(string kind)
    {
        var path=Path.Combine(root,"library.json");
        var store=new LibraryStore(path);store.Save(new Library());
        var history=new ActivityStore(path);
        if(kind=="activity"){history.SetFavorite("test",Guid.NewGuid(),true);path=history.PathName;}
        V8iStore? shared=null;Library? library=null;
        if(kind=="v8i") {path=Path.Combine(root,"list.v8i");File.WriteAllText(path,"[Original]\nConnect=Srvr=server;Ref=base;\n");shared=new V8iStore(path);library=shared.Load(new Library());}
        if(OperatingSystem.IsWindows())
        {
            var security=new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid,null),FileSystemRights.Read,AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        if(kind=="activity")history.SetFavorite("test",Guid.NewGuid(),true);
        else if(shared is not null && library is not null)shared.Save(library with {Bases=[library.Bases[0] with {Name="Changed"}]});
        else store.Save(new Library{Executable="future-client"});
        foreach(var target in new[]{path,path+".bak"})
        {
            if(OperatingSystem.IsWindows())
            {
                var rules=new FileInfo(target).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>();
                Assert.Contains(rules,r=>OperatingSystem.IsWindows() && r.AccessControlType==AccessControlType.Deny && r.IdentityReference.Value=="S-1-5-32-546" && (r.FileSystemRights&FileSystemRights.Read)==FileSystemRights.Read);
            }
            else Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(target));
        }
    }
}
