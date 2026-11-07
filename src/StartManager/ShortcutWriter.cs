using System.Runtime.InteropServices;
using StartManager.Core;

namespace StartManager;

internal static class ShortcutWriter
{
    public static void Write(string path, ShortcutRequest request, string name)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь к программе.");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("BaseNavigator", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Создавайте ярлыки из установленной сборки программы.");
        var windows = OperatingSystem.IsWindows();
        if (!Path.GetExtension(path).Equals(windows ? ".lnk" : ".desktop", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Неверное расширение ярлыка.");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            if (windows) WriteWindows(temp, executable, request.Arguments());
            else
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                var safeName = new string(name.Where(c => !char.IsControl(c)).ToArray()).Replace("\\", "\\\\");
                var icon = Path.Combine(Path.GetDirectoryName(executable)!, "Assets", "basenavigator.png");
                var iconValue = File.Exists(icon) ? icon.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") : "application-x-executable";
                File.WriteAllText(temp, "[Desktop Entry]\nType=Application\nVersion=1.0\nName=" + safeName + "\nExec=" + ShortcutRequest.DesktopExec(executable, request.Arguments()) + "\nTerminal=false\nIcon=" + iconValue + "\n");
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string executable, string[] args)
    {
        object? shell = null; object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host недоступен."));
            dynamic host = shell!;
            shortcut = host.CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = executable;
            link.Arguments = string.Join(" ", args.Select(ShortcutRequest.WindowsQuote));
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.IconLocation = executable + ",0";
            link.Description = "БазоНавигатор · запуск базы";
            link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
