namespace StartManager.Core;

/// <summary>What a shortcut start with --no-gui prints: the outcome of the start (success to the output, the error to the error
/// stream) and, on its own line of the output, the pending notice about removed passwords of an earlier build. Each is printed
/// whatever the other is (Codex re-check 4, N2); the notice stays pending until the main window shows it.</summary>
public static class ConsoleLaunch
{
    public const string Started = "Процесс 1С запущен.";

    public static void Report(string? error, string? passwordsNotice, TextWriter output, TextWriter errors)
    {
        if (error is null) output.WriteLine(Started);
        else errors.WriteLine(error);
        if (passwordsNotice is not null) output.WriteLine(passwordsNotice);
    }
}
