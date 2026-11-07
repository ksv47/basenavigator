using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StartManager.Core;

public enum DiagnosticLevel { Off, Error, Warning, Info, Debug }

public static class DiagnosticPreferences
{
    public static DiagnosticLevel Read(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("Level", out var level) && level.ValueKind == JsonValueKind.String &&
            TryLevel(level.GetString(), out var result)) return result;
        return DiagnosticLevel.Info;
    }

    public static bool TryLevel(string? text, out DiagnosticLevel level)
    {
        level = text?.ToLowerInvariant() switch
        { "off" => DiagnosticLevel.Off, "error" => DiagnosticLevel.Error, "warning" => DiagnosticLevel.Warning,
          "info" => DiagnosticLevel.Info, "debug" => DiagnosticLevel.Debug, _ => (DiagnosticLevel)(-1) };
        return Enum.IsDefined(level);
    }

    public static JsonElement WithLevel(JsonElement? original, DiagnosticLevel level)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        var value = original is { ValueKind: JsonValueKind.Object } element
            ? (JsonObject)JsonNode.Parse(element.GetRawText())! : new JsonObject();
        value["Level"] = level.ToString().ToLowerInvariant();
        return JsonSerializer.SerializeToElement(value);
    }
}

// The queue is bounded and disk I/O runs on a background thread. Diagnostic failures
// never replace the application error. Each process owns a unique set of log files.
public sealed class DiagnosticLog : IDisposable
{
    private readonly BlockingCollection<object> queue = new(256);
    private readonly Thread worker;
    private readonly int maxBytes;
    private readonly string prefix;
    private readonly string version;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private long dropped;
    private int level, disposed;
    private string? failure;
    public string DirectoryPath { get; }
    public string? Failure => Volatile.Read(ref failure);
    public long DroppedRecords => Interlocked.Read(ref dropped);
    public DiagnosticLevel Level { get => (DiagnosticLevel)Volatile.Read(ref level); set => Volatile.Write(ref level, (int)value); }
    public static string DefaultDirectory => Path.Combine(Path.GetDirectoryName(LibraryStore.DefaultPath)!, "logs");

    public DiagnosticLog(string directory, DiagnosticLevel level, string version, int maxBytes = 1024 * 1024)
    {
        DirectoryPath = directory; Level = level; this.version = version;
        this.maxBytes = Math.Max(1024, maxBytes);
        prefix = $"bn-{Environment.ProcessId}-{Guid.NewGuid():N}";
        worker = new Thread(Run) { IsBackground = true, Name = "Diagnostic journal" };
        worker.Start();
    }

    public void Write(DiagnosticLevel severity, string code, Exception? error = null, int enqueueTimeout = 0) => Record(severity, code, error, enqueueTimeout, null);

    /// <summary>«backup.lock.kept»: a lock the backup window set could not be removed. Typed fields only, so nothing but these can reach
    /// the journal (Codex re-check 3, 20.10): the base's ID, the reason, and the private file name kept aside only when it is exactly
    /// the name <see cref="BackupLock"/> makes (<c>.bn-lock-&lt;32 hex&gt;.cdn</c>); any other name is left out.</summary>
    public void LockKept(Guid baseId, LockKeptReason reason, string? privateFileName)
    {
        var data = new SortedDictionary<string, string>(StringComparer.Ordinal)
        { ["baseId"] = baseId.ToString("D"), ["reason"] = reason.ToString().ToLowerInvariant() };
        if (privateFileName is not null && BackupLock.IsPrivateName(privateFileName)) data["keptFile"] = privateFileName;
        Record(DiagnosticLevel.Warning, "backup.lock.kept", null, 0, data);
    }

    /// <summary>«backup.rawlog.kept»: a temporary 1C log folder stayed after an exception (Grok audit round 2, 20.10). Only its name is
    /// written, and only when it is a folder «bn-1c-…» right in the temporary folder of the user; the path above it (with the user's
    /// name) and anything else are left out.</summary>
    public void RawLogKept(string? folder)
    {
        var data = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (RawLogName(folder) is { } name) data["keptFolder"] = name;
        Record(DiagnosticLevel.Warning, "backup.rawlog.kept", null, 0, data);
    }

    /// <summary>«passwords.migration.retired»: the start removed saved passwords of an earlier build; only their number of bases.</summary>
    public void PasswordsRetired(int bases) =>
        Record(DiagnosticLevel.Warning, "passwords.migration.retired", null, 0,
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["bases"] = bases.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    internal static string? RawLogName(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder)) return null;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), temp, comparison) &&
            name.StartsWith("bn-1c-", StringComparison.Ordinal) && name.Length is > 6 and <= 64 &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? name : null;
    }

    private void Record(DiagnosticLevel severity, string code, Exception? error, int enqueueTimeout, SortedDictionary<string, string>? fields)
    {
        if (severity == DiagnosticLevel.Off || severity > Level || Volatile.Read(ref disposed) != 0) return;
        try
        {
            // Only constant event identifiers; never messages, paths, arguments or exception.ToString().
            if (code.Length > 100 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) return;
            var errors = new List<object>();
            for (var ex = error; ex is not null && errors.Count < 5; ex = ex.InnerException)
                errors.Add(new { type = ex.GetType().FullName, hresult = $"0x{ex.HResult:X8}",
                    nativeCode = ex is System.ComponentModel.Win32Exception win32 ? (int?)win32.NativeErrorCode : null,
                    stack = new StackTrace(ex, false).GetFrames().Take(24).Select(f =>
                    { var m = f.GetMethod(); return m?.DeclaringType?.FullName + "." + m?.Name; }).ToArray() });
            var record = fields is null
                ? JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, level = severity.ToString(), code, errors })
                : JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, level = severity.ToString(), code, errors, data = fields });
            if (!queue.TryAdd(record, enqueueTimeout))
            {
                Interlocked.Increment(ref dropped);
                Interlocked.CompareExchange(ref failure, "QueueOverflow", null);
            }
        }
        catch (Exception ex) { SetFailure(ex); }
    }

    public bool Flush(int milliseconds = 500)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // True also means that no records were lost earlier in this session.
        var timer = Stopwatch.StartNew();
        try { return queue.TryAdd(barrier, milliseconds) &&
            barrier.Task.Wait(Math.Max(0, milliseconds - (int)timer.ElapsedMilliseconds)) && barrier.Task.Result; }
        catch (Exception ex) { SetFailure(ex); return false; }
    }

    private void SetFailure(Exception ex) => Interlocked.CompareExchange(ref failure, $"{ex.GetType().Name} (0x{ex.HResult:X8})", null);

    private void Run()
    {
        StreamWriter? output = null;
        var part = 0;
        var replacePart = false;
        long reportedDropped = 0;
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                if (item is TaskCompletionSource<bool> barrier) { barrier.TrySetResult(Failure is null); continue; }
                try
                {
                    if (output is null)
                    {
                        Directory.CreateDirectory(DirectoryPath);
                        try { Cleanup(); } catch { /* Concurrent retention or a denied old file must not lose the new event. */ }
                        if (replacePart) File.Delete(Path.Combine(DirectoryPath, $"{prefix}-{part}.log"));
                        output = Open(part);
                        replacePart = false;
                    }
                    if (output.BaseStream.Length >= maxBytes)
                    {
                        output.Dispose(); output = null;
                        part = (part + 1) % 4;
                        replacePart = true;
                        var old = Path.Combine(DirectoryPath, $"{prefix}-{part}.log");
                        if (File.Exists(old)) File.Delete(old);
                        output = Open(part);
                        replacePart = false;
                    }
                    var lost = DroppedRecords;
                    if (lost > reportedDropped)
                    {
                        output.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, level = "Warning", code = "logging.records_dropped", total = lost }));
                        reportedDropped = lost;
                    }
                    output.WriteLine((string)item); // AutoFlush makes the last completed stage useful after a fatal crash.
                }
                catch (Exception ex)
                {
                    SetFailure(ex); Interlocked.Increment(ref dropped);
                    // Preserve the completed part after a write failure; retry rotation after a sharing failure.
                    if (output is not null) part = (part + 1) % 4;
                    try { output?.Dispose(); } catch { }
                    output = null; replacePart = true;
                }
            }
        }
        catch (Exception ex) { SetFailure(ex); }
        finally { try { output?.Dispose(); } catch (Exception ex) { SetFailure(ex); } }
    }

    private StreamWriter Open(int part)
    {
        var stream = new FileStream(Path.Combine(DirectoryPath, $"{prefix}-{part}.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new { utc = started, code = "session", version, dropped = DroppedRecords,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), process = Environment.ProcessId }));
            return writer;
        }
        catch { writer.Dispose(); throw; }
    }

    private void Cleanup()
    {
        // Retain the 20 newest inactive files plus all live processes' files. Never touch unrelated files.
        var inactive = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "bn-*.log"))
        {
            var file = new FileInfo(path);
            var bits = Path.GetFileNameWithoutExtension(path).Split('-');
            if (bits.Length != 4 || !int.TryParse(bits[1], out var pid) || !Guid.TryParseExact(bits[2], "N", out _) ||
                !int.TryParse(bits[3], out var number) || number is < 0 or > 3 || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited) continue; }
            catch (ArgumentException) { }
            catch { continue; }
            inactive.Add(file);
        }
        foreach (var file in inactive.OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
            try { file.Delete(); } catch { /* Retention failure must not prevent new diagnostics. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.CompleteAdding();
        worker.Join(1000); // A disconnected network log directory cannot hold shutdown indefinitely.
    }
}
