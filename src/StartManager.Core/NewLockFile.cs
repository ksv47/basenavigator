using System.Runtime.InteropServices;

namespace StartManager.Core;

/// <summary>A lock file 1Cv8.cdn this process makes (the button «Установить блокировку ИБ»): created exclusively, and removed after a
/// failed write only as the very file it made, never by the name 1Cv8.cdn (Codex re-check 5 and 6, 20.10). Windows: the handle is
/// opened as 1Cv8.cdn with DELETE access and no sharing, so nobody can rename, delete or open the file while it is held, and it is
/// deleted through the handle (FileDispositionInfo). Unix: the file is written under a fresh private name in the same folder
/// («.bn-lock-», 32 hexadecimal digits, «.cdn», as <see cref="BackupLock.IsPrivateName"/>) and published by link(2) only after the
/// write, which fails when 1Cv8.cdn exists; a failed write removes only that private name, 1Cv8.cdn is never touched. No rename(2):
/// it would replace a lock another program made meanwhile.</summary>
internal sealed class NewLockFile : IDisposable
{
    private NewLockFile(string path, string? hidden, FileStream stream) { PathName = path; Hidden = hidden; Stream = stream; }
    public string PathName { get; }
    /// <summary>Unix: the private name the file is written under until <see cref="Publish"/>; Windows: null (the file is 1Cv8.cdn).</summary>
    public string? Hidden { get; }
    public FileStream Stream { get; }
    /// <summary>Tests: runs right before a failed file is removed (it may throw, or make a file of the name 1Cv8.cdn).</summary>
    internal static Action<string>? BeforeDiscard;
    /// <summary>Tests: runs right before the private name is removed after the link (Unix).</summary>
    internal static Action<string>? Unlinking;

    /// <summary>The new file; null when a file of that name exists (another program was first). Unix: that is found by
    /// <see cref="Publish"/>.</summary>
    public static NewLockFile? Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(path, GenericWrite | Delete, 0, IntPtr.Zero, CreateNewDisposition, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 80 or 183) return null; // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
                throw new IOException(new System.ComponentModel.Win32Exception(error).Message, error);
            }
            return new(path, null, new FileStream(handle, FileAccess.Write, 1));
        }
        var hidden = Path.Combine(Path.GetDirectoryName(path)!, BackupLock.NewPrivateName());
        return new(path, hidden, new FileStream(hidden, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1));
    }

    /// <summary>The written and flushed file made 1Cv8.cdn. Windows: it is already. Unix: link(2) to 1Cv8.cdn, then the private name
    /// removed; when 1Cv8.cdn appeared meanwhile (EEXIST), or the file system has no hard links, the private name is removed and the
    /// lock is not set. On NFS link(2) may fail although the server made the link: after any failure 1Cv8.cdn holding exactly
    /// <paramref name="written"/> (with our random code) is our lock and is set; one that cannot be read is told as possibly ours.
    /// 1Cv8.cdn is never removed or changed here (Codex re-check 7, 20.10). Call after the write and Flush(true): the stream is closed. <see cref="LockPublished.Set"/> with a text: the lock is
    /// set, but the private name stays (it is named).</summary>
    public (LockPublished Result, string? Text) Publish(byte[] written)
    {
        if (Hidden is null) return (LockPublished.Set, null);
        // The written file is closed first: .NET holds FileShare.None on Unix by flock(LOCK_EX), and after the link 1Cv8.cdn is the
        // same inode — reading it while open fails (EAGAIN) for us and for 1C (Linux VM run, 20.10).
        Close();
        int error; string reason;
        try
        {
            error = BackupLock.Link(Hidden, PathName);
            // Only the errors of a file system without hard links say so; EPERM may be either rights or that; any other is told as it
            // is (Grok audit round 2, Codex re-check 10).
            var why = new System.ComponentModel.Win32Exception(error).Message;
            reason = error switch
            {
                18 or 95 => $"{NotLinked}: файловая система каталога базы не поддерживает жёсткие ссылки ({why}).", // EXDEV, EOPNOTSUPP/ENOTSUP
                1 => $"{NotLinked}: нет прав или файловая система каталога базы не поддерживает жёсткие ссылки ({why}).", // EPERM
                _ => $"{NotLinked} ({why})."
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { error = -1; reason = NotLinked + " (link недоступен)."; }
        if (error != 0)
        {
            var ours = Holds(written);
            if (ours is null)
                return (LockPublished.Failed, Joined(reason + $" Возможно, файл {PathName} всё же создан этой попыткой: проверьте его и при " +
                    "необходимости удалите командой «Разблокировать информационную базу».", Discard()));
            if (ours == false) return error == 17 ? (LockPublished.Taken, Discard()) : (LockPublished.Failed, Joined(reason, Discard())); // EEXIST
        }
        // Both names are the same file now; the lock is set. A private name that cannot be removed stays as a second name: told.
        try { Unlinking?.Invoke(Hidden); File.Delete(Hidden); return (LockPublished.Set, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (LockPublished.Set, $"Второе имя того же файла {Hidden} не удалено: удалите его вручную — в нём код доступа."); }
    }

    // Whether 1Cv8.cdn holds exactly these bytes: false when it is absent or another file, null when it cannot be read.
    private bool? Holds(byte[] written)
    {
        try { return File.ReadAllBytes(PathName).AsSpan().SequenceEqual(written); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private const string NotLinked = "файл блокировки не удалось сделать файлом 1Cv8.cdn";
    private static string Joined(string reason, string? kept) => kept is null ? reason : reason + " " + kept;

    /// <summary>Removes the file made after its write failed (Unix: its private name only); null when it is gone, otherwise the text
    /// that it stays.</summary>
    public string? Discard()
    {
        var gone = false;
        try
        {
            BeforeDiscard?.Invoke(Hidden ?? PathName);
            if (Hidden is not null)
            {
                // Nobody else knows the private name: removing it by name removes our file only.
                File.Delete(Hidden);
                gone = true;
            }
            else
            {
                byte delete = 1;
                gone = SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref delete, 1); // FileDispositionInfo
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException) { }
        finally { Close(); }
        if (gone) return null;
        return Hidden is null
            ? $"Файл блокировки {PathName} остался: удалите его командой «Разблокировать информационную базу»."
            : $"Файл блокировки {Hidden} остался: удалите его вручную — в нём код доступа.";
    }

    private void Close()
    {
        try { Stream.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => Close();

    private const uint GenericWrite = 0x40000000, Delete = 0x00010000, CreateNewDisposition = 1, FileAttributeNormal = 0x80;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, ref byte info, uint size);
}

/// <summary>What <see cref="NewLockFile.Publish"/> did: the lock is set, another program made 1Cv8.cdn first (Taken), or it could not be
/// published (Failed).</summary>
internal enum LockPublished { Set, Taken, Failed }
