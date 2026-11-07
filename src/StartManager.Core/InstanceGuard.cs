namespace StartManager.Core;

/// <summary>Shared leases for ordinary launches, an exclusive lease for single-instance mode.</summary>
public static class InstanceGuard
{
    public static FileStream Acquire(string settingsPath, bool exclusive)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        var folder = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Укажите файл настроек, а не корень диска.");
        Directory.CreateDirectory(folder);
        // Do not unlink this file: another process can already hold a lease on its inode.
        return new FileStream(fullPath + ".instance", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite);
    }

    // Windows: a sharing or lock violation (Win32 code in the HRESULT). Elsewhere .NET puts the raw errno into HResult:
    // a held lock is EWOULDBLOCK (11), and a CIFS share answers EBUSY (16) for a file another computer holds.
    public static bool IsConflict(Exception error) => error is IOException io &&
        (OperatingSystem.IsWindows() ? (io.HResult & 0xffff) is 32 or 33 : io.HResult is 11 or 16);
}
