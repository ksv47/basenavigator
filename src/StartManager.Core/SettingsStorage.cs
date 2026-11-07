namespace StartManager.Core;

/// <summary>Chooses a profile for the next launch; never moves or removes profile data.</summary>
public sealed class SettingsStorage
{
    public SettingsStorage(string? applicationDirectory = null, string? normalDirectory = null)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        NormalDirectory = Path.GetFullPath(normalDirectory ?? Path.Combine(
            Environment.GetFolderPath(OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "BaseNavigator"));
    }

    public string ApplicationDirectory { get; }
    public string NormalDirectory { get; }
    public string PortableDirectory => Path.Combine(ApplicationDirectory, "Settings");
    public string MarkerPath => Path.Combine(ApplicationDirectory, "BaseNavigator.portable");
    public string DirectoryFor(bool portable) => portable ? PortableDirectory : NormalDirectory;
    public string SettingsPath => Path.Combine(DirectoryFor(ReadPortable()), "library.json");

    public bool ReadPortable()
    {
        try
        {
            if ((File.GetAttributes(MarkerPath) & FileAttributes.Directory) != 0)
                throw new IOException("Путь переключателя портативного режима занят каталогом: " + MarkerPath);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public void SaveMode(bool portable, bool expectedPortable)
    {
        if (ReadPortable() != expectedPortable)
            throw new IOException("Режим изменён другим экземпляром программы. Закройте и снова откройте выбор режима.");
        if (portable == expectedPortable) return;
        using var guard = new FileStream(MarkerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ReadPortable() != expectedPortable)
            throw new IOException("Режим изменён другим экземпляром программы. Закройте и снова откройте выбор режима.");
        var target = DirectoryFor(portable);
        Directory.CreateDirectory(target);
        // Check the destination before changing the mode. Keep both existing profiles intact.
        using (new FileStream(Path.Combine(target, ".write-test-" + Guid.NewGuid().ToString("N")),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        if (portable)
        {
            using var marker = new FileStream(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        else File.Delete(MarkerPath);
    }
}
