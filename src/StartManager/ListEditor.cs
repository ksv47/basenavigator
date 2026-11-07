using Avalonia.Platform.Storage;

namespace StartManager;

/// <summary>A list file in a text editor: «Сервис → Список ИБ ▸ Открыть в редакторе», the common lists of the settings and the line
/// under the properties of a base.</summary>
internal static class ListEditor
{
    /// <summary>Windows opens .v8i with 1CEStart, so the editor is named, as the original does; elsewhere the default application of
    /// the file opens it. False when the system refused to open it; a failure to start is thrown to the caller, which says it.</summary>
    public static async Task<bool> Open(ILauncher launcher, string path)
    {
        if (!OperatingSystem.IsWindows()) return await launcher.LaunchFileInfoAsync(new FileInfo(path));
        using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false })) { }
        return true;
    }
}
