using System.Diagnostics;

namespace StartManager.Core;

/// <summary>
/// Report of <see cref="DesktopEntryCommand.Apply"/>: output lines, the error (null on success) and the note about a kept
/// icon (also among the lines), null when there is none.
/// </summary>
public sealed record DesktopEntryResult(IReadOnlyList<string> Lines, string? Error, string? Note = null);

/// <summary>
/// Console commands --install-desktop / --uninstall-desktop: the program's own entry in the Linux application menu
/// (<see cref="DesktopEntry"/>). Exit codes follow the program: 0 success, 2 not applicable on this OS (argument error),
/// 1 the entry was not written or removed.
/// </summary>
public static class DesktopEntryCommand
{
    public const string ProgramName = "BaseNavigator";
    public const string NotFound = "Ярлык в меню приложений не найден.";
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);

    /// <param name="executable">Path of the running program (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="linux">Whether the OS is Linux; injected for tests.</param>
    /// <param name="variable">Environment lookup (XDG_DATA_HOME, HOME).</param>
    /// <param name="refresh">Runs a cache tool best-effort; by default <see cref="RunQuietly"/>.</param>
    public static int Run(DesktopCommand command, string? executable, bool linux, Func<string, string?> variable,
        TextWriter output, TextWriter error, Func<string, string[], bool>? refresh = null)
    {
        if (command == DesktopCommand.None) throw new ArgumentOutOfRangeException(nameof(command));
        if (!linux)
        {
            error.WriteLine("Ярлык в меню приложений создаётся только в Linux.");
            return 2;
        }
        var result = Apply(command, executable, variable, refresh ?? RunQuietly);
        foreach (var line in result.Lines) output.WriteLine(line);
        if (result.Error is null) return 0;
        error.WriteLine(result.Error);
        return 1;
    }

    /// <summary>
    /// Creates or removes the entry and refreshes the caches best-effort: the shared part of the console command and
    /// the settings page, so both check the program name and report the same words. Linux is checked by the caller.
    /// </summary>
    /// <returns>Report lines; <see cref="DesktopEntryResult.Error"/> is null when the entry was written or removed (or absent).</returns>
    public static DesktopEntryResult Apply(DesktopCommand command, string? executable, Func<string, string?> variable,
        Func<string, string[], bool> refresh)
    {
        if (command == DesktopCommand.None) throw new ArgumentOutOfRangeException(nameof(command));
        var install = command == DesktopCommand.Install;
        var failure = install ? "Ярлык в меню приложений не создан: " : "Ярлык в меню приложений не удалён: ";
        if (string.IsNullOrEmpty(executable)) return new([], failure + "не найден путь к программе.");
        if (!Path.GetFileNameWithoutExtension(executable).Equals(ProgramName, StringComparison.OrdinalIgnoreCase))
            return new([], "Создавайте ярлыки из установленной сборки программы.");
        string data;
        string? note = null;
        var lines = new List<string>();
        try
        {
            data = DesktopEntry.DataHome(variable);
            if (install)
            {
                (var entry, note) = DesktopEntry.Install(executable, variable);
                lines.Add("Ярлык в меню приложений: " + entry);
                if (note is not null) lines.Add(note);
            }
            else
            {
                (var removed, note) = DesktopEntry.Uninstall(variable);
                if (removed.Count == 0)
                {
                    // Nothing of ours: a foreign file at the entry path is a problem the user must solve, not «not found».
                    var problem = State(variable).Problem;
                    return problem is null ? new([NotFound], null) : new([], problem);
                }
                lines.AddRange(removed.Select(path => "Удалён файл: " + path));
                if (note is not null) lines.Add(note);
            }
        }
        catch (IconNotWrittenException ex)
        {
            // The entry is already written: «не создан» would be wrong.
            RefreshCaches(DesktopEntry.DataHome(variable), refresh);
            return new(lines, "Ярлык в меню приложений обновлён, но значок не записан: " + LegacyImport.ErrorMessage(ex.Reason));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(lines, failure + LegacyImport.ErrorMessage(ex));
        }
        RefreshCaches(data, refresh);
        return new(lines, null, note);
    }

    /// <summary>
    /// Whether the own entry is installed; <c>Problem</c> instead when the data folder is unknown or unreadable, or when
    /// a foreign <see cref="DesktopEntry.FileName"/>, a symbolic link or a non-regular file occupies the place (<see cref="DesktopEntry.Install"/> would refuse it).
    /// </summary>
    public static (bool Installed, string? Problem) State(Func<string, string?> variable)
    {
        try
        {
            var entry = DesktopEntry.EntryPath(DesktopEntry.DataHome(variable));
            if (DesktopEntry.FileProblem(entry) is { } file) return (false, file);
            if (DesktopEntry.IsOwn(entry)) return (true, null);
            return File.Exists(entry)
                ? (false, "Файл " + entry + " создан не БазоНавигатором; удалите или переименуйте его, чтобы создать ярлык.")
                : (false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return (false, LegacyImport.ErrorMessage(ex));
        }
    }

    /// <summary>
    /// Asks the desktop to re-read the menu and the icon theme. Optional: most menus watch the folders themselves,
    /// so a missing tool or a failure is ignored.
    /// </summary>
    public static void RefreshCaches(string dataHome, Func<string, string[], bool> refresh)
    {
        var applications = Path.Combine(dataHome, "applications");
        if (Directory.Exists(applications)) refresh("update-desktop-database", [applications]);
        var icons = Path.Combine(dataHome, "icons", "hicolor");
        if (Directory.Exists(icons)) refresh("gtk-update-icon-cache", ["-f", "-t", icons]);
    }

    /// <summary>Runs a tool from PATH with a short timeout; false when it is missing, fails or hangs.</summary>
    public static bool RunQuietly(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(RefreshTimeout))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                process.WaitForExit(KillTimeout); // reap the child, so a long-lived settings window keeps no zombie
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }
}
