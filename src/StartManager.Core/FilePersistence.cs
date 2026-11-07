using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace StartManager.Core;

internal static class FilePersistence
{
    // Apply permissions at creation, before any private bytes reach the temporary file.
    private static FileStream Create(string temporary, string? permissionsFrom)
    {
        if (permissionsFrom is not null && OperatingSystem.IsWindows())
        {
            var security = new FileInfo(permissionsFrom).GetAccessControl(AccessControlSections.Access);
            return new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.None, security);
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (permissionsFrom is not null && !OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(permissionsFrom);
            options.UnixCreateMode = mode;
            var stream = new FileStream(temporary, options);
            try
            {
                // Creation is subject to umask. Restore the exact mode on the open,
                // still-empty file, without ever exposing data with broader access.
                File.SetUnixFileMode(stream.SafeFileHandle, mode);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        return new FileStream(temporary, options);
    }

    /// <summary>The file a profile path names: links in the file name and in its folders are followed to the physical file,
    /// so the link the user made stays and the target gets the new text. Backups are not followed: a link there is replaced,
    /// never written through.</summary>
    internal static string Target(string path) => BaseDuplicate.Physical(path);

    /// <summary>The lock of a profile file, taken on the physical file: two names of one file share one lock.
    /// About 1.5 s of waiting for another window (closing the main window waits 2 s for its last write), then a clear reason.
    /// Other errors (no folder, too long a path) are not waited for and keep their own message.</summary>
    internal static FileStream Lock(string path)
    {
        var target = Target(path) + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (Held(ex) && attempt < 30) { Thread.Sleep(50); }
            catch (IOException ex) when (Held(ex)) { throw new IOException("Файл занят другим окном БазоНавигатора. Повторите через несколько секунд.", ex); }
        }
    }

    // Windows: a sharing or lock violation. Elsewhere .NET puts errno into HResult: a held flock is EWOULDBLOCK (11 on Linux);
    // other errors such as ELOOP of a link loop keep their own message.
    private static bool Held(IOException ex) => OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 32 or 33 : ex.HResult == 11;

    /// <param name="durable">The new name itself reaches the disk before the call returns: Windows renames with write-through,
    /// Linux syncs the folder after the rename (fsync(2): syncing the file does not sync its directory entry). A file system
    /// that cannot sync a folder (EINVAL, ENOTSUP) is left as it is; a network share may still cache the name.</param>
    internal static void Replace(string destination, byte[] bytes, string? permissionsFrom, Action? beforeReplace = null, bool durable = false)
    {
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = Create(temporary, permissionsFrom))
            {
                try { stream.Write(bytes); stream.Flush(true); }
                catch (ArgumentOutOfRangeException ex)
                { throw new IOException("Файл не записан: превышено ограничение размера файла или доступного объёма записи.", ex); }
            }
            beforeReplace?.Invoke();
            // Replace the entry, never open an existing backup link for writing.
            if (!durable) File.Move(temporary, destination, true);
            else if (OperatingSystem.IsWindows())
            {
                if (!MoveFileEx(Extended(temporary), Extended(Path.GetFullPath(destination)), 0x1 | 0x8)) // REPLACE_EXISTING | WRITE_THROUGH
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw new IOException(new Win32Exception(error).Message + " " + destination, unchecked((int)0x80070000) | error);
                }
            }
            else { File.Move(temporary, destination, true); SyncFolder(Path.GetDirectoryName(Path.GetFullPath(destination))!); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // The Win32 call does not get the long-path prefix .NET adds by itself.
    private static string Extended(string full) => full.Length < 260 || full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;

    private static void SyncFolder(string folder)
    {
        var descriptor = open(folder, 0); // O_RDONLY
        if (descriptor < 0) throw new ExplainedIOException("Каталог не открыт для синхронизации: " + folder, Marshal.GetLastPInvokeError());
        try
        {
            if (fsync(descriptor) != 0 && Marshal.GetLastPInvokeError() is var error && error is not (22 or 95)) // EINVAL, ENOTSUP
                throw new ExplainedIOException("Каталог не синхронизирован с диском: " + folder, error);
        }
        finally { close(descriptor); }
    }

    /// <summary>Windows: whether this process may replace the existing name as MoveFileEx does (DELETE on the file, or
    /// DELETE_CHILD on its folder). Nothing changes: the name itself, not a link target, is opened with every share and closed.
    /// 0 when allowed, otherwise the Win32 error.</summary>
    internal static int ReplaceAccess(string existing)
    {
        using var handle = CreateFile(Extended(Path.GetFullPath(existing)), 0x00010000, 0x7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // DELETE, share all, OPEN_EXISTING, OPEN_REPARSE_POINT
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string replacement, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
}
