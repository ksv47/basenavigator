using System.Text.Json;
using StartManager.Core;

namespace StartManager;

internal static class AppLog
{
    private static DiagnosticLog? journal;
    private static DiagnosticLevel? commandLevel;
    public static string DirectoryPath => journal?.DirectoryPath ?? DiagnosticLog.DefaultDirectory;
    public static string? Failure => journal?.Failure;
    public static bool Overridden => commandLevel is not null;

    public static void Initialize(DiagnosticArguments arguments)
    {
        commandLevel = arguments.Level;
        var level = DiagnosticLevel.Info;
        Exception? failure = null;
        // Read only the diagnostic preference, tolerating a damaged profile. Do not save or validate the base list here.
        if (commandLevel is null)
            try
            {
                if (File.Exists(arguments.SettingsPath))
                {
                    using var reader = new StreamReader(arguments.SettingsPath, System.Text.Encoding.UTF8, true);
                    using var json = JsonDocument.Parse(reader.ReadToEnd());
                    if (json.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var property in json.RootElement.EnumerateObject())
                            if (property.Name.Equals("Diagnostics", StringComparison.OrdinalIgnoreCase)) level = DiagnosticPreferences.Read(property.Value);
                }
            }
            catch (Exception ex) { failure = ex; }
        var profileDirectory = Path.GetDirectoryName(Path.GetFullPath(arguments.SettingsPath))
            ?? throw new ArgumentException("Укажите файл настроек, а не корень диска.");
        journal = new DiagnosticLog(arguments.Directory ?? Path.Combine(profileDirectory, "logs"), commandLevel ?? level,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
        Info("startup.managed.enter");
        // The core of backups: a temporary 1C log folder that stays after an exception (its checked name only).
        BackupDiagnostics.RawLogKept = folder => journal?.RawLogKept(folder);
        if (failure is not null) Error("startup.preferences.read_failed", failure);
        journal.Flush();
    }

    public static void Configure(Library library)
    {
        if (journal is not null) journal.Level = commandLevel ?? DiagnosticPreferences.Read(library.Diagnostics);
        Info("logging.configuration.applied");
    }
    public static void Info(string code) => journal?.Write(DiagnosticLevel.Info, code);
    public static void Debug(string code) => journal?.Write(DiagnosticLevel.Debug, code);
    public static void Warning(string code, Exception? error = null) => journal?.Write(DiagnosticLevel.Warning, code, error);
    /// <summary>«backup.lock.kept» with its typed fields (<see cref="DiagnosticLog.LockKept"/>).</summary>
    public static void LockKept(Guid baseId, LockKeptReason reason, string? privateFileName) => journal?.LockKept(baseId, reason, privateFileName);
    /// <summary>«passwords.migration.retired» with the number of bases (<see cref="DiagnosticLog.PasswordsRetired"/>).</summary>
    public static void PasswordsRetired(int bases) => journal?.PasswordsRetired(bases);
    public static void Error(string code, Exception error) => journal?.Write(DiagnosticLevel.Error, code, error);
    public static void Stage(string code) { journal?.Write(DiagnosticLevel.Info, code, enqueueTimeout: 500); journal?.Flush(); }
    public static void Flush() => journal?.Flush(1000);
    public static void Close() { Info("shutdown.completed"); journal?.Dispose(); }
}
