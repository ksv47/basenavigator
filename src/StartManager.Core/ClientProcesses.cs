using System.Diagnostics;

namespace StartManager.Core;

public sealed record ProcessKillResult(int Found, int Killed, IReadOnlyList<string> Failures)
{
    /// <summary>The status line after ending the confirmed processes: those not found anew exited by themselves before the
    /// confirmation; a process that could not be checked is among the failures, not among them (07.11).</summary>
    public string Status(int confirmed)
    {
        var gone = confirmed - Found;
        return $"Найдено процессов: {confirmed}. Завершено: {Killed}." +
            (gone > 0 ? $" Завершились сами до подтверждения: {gone}." : "") +
            (Failures.Count > 0 ? " Не удалось завершить: " + string.Join("; ", Failures.Take(3)) +
                (Failures.Count > 3 ? $" и ещё {Failures.Count - 3}." : "") : "");
    }
}

/// <summary>What the user confirmed: the number and the start time, so a reused number is not another process.</summary>
public sealed record ProcessIdentity(int Id, DateTime? Start);

/// <summary>The original «Завершение процессов "1С:Предприятие 8"» (the «Сервис» menu): forced end of hung clients.
/// Only the clients and the starter of the current user: the same Windows session, on Linux the same real UID.
/// The 1C server processes are not touched. Other systems find nothing.</summary>
public static class ClientProcesses
{
    public static readonly IReadOnlyList<string> ClientNames = ["1cv8", "1cv8c", "1cv8s", "1cestart"];

    public static List<Process> Find(IEnumerable<string>? names = null)
    {
        int session;
        using (var current = Process.GetCurrentProcess()) session = OperatingSystem.IsWindows() ? current.SessionId : -1;
        var user = OperatingSystem.IsLinux() ? LinuxUser(Environment.ProcessId) : null;
        var result = new List<Process>();
        foreach (var name in (names ?? ClientNames).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var process in Process.GetProcessesByName(name))
            {
                bool own;
                // The right to send a signal is no proof of ownership: root may end anyone's process.
                try { own = OperatingSystem.IsWindows() ? process.SessionId == session : user is not null && LinuxUser(process.Id) == user; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { own = false; }
                if (own && process.Id != Environment.ProcessId) result.Add(process); else process.Dispose();
            }
        // The starters first: a starter ended after the clients could still start a new client in between.
        return [.. result.OrderBy(x => Name(x).ToLowerInvariant() is "1cestart" or "1cv8s" ? 0 : 1)];
    }

    public static ProcessIdentity Identity(Process process)
    {
        try { return new(process.Id, process.StartTime); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return new(process.Id, null); }
    }

    /// <summary>Without a start time a process is only a number, which may have been reused: it is not ended.</summary>
    internal static bool Matches(IReadOnlyCollection<ProcessIdentity> confirmed, ProcessIdentity found) =>
        found.Start is not null && confirmed.Contains(found);

    /// <summary>A process held by a reference that keeps naming it while its number may be reused (Windows: an open handle;
    /// Linux: a pidfd). The start time is read after the reference was taken, and the signal goes through it.</summary>
    internal interface IHeld : IDisposable
    {
        /// <summary>Null when it cannot be read while the process is still there (a gone process gives no reference).</summary>
        DateTime? Start { get; }
        /// <summary>Exited when the process is already gone; Refused with the reason when it could not be signalled.</summary>
        OwnedProcess.Outcome Kill(out string? error);
        bool WaitForExit(TimeSpan wait);
    }

    /// <summary>Takes the reference: null when the process is gone (no refusal) or cannot be held safely (refusal set).</summary>
    internal delegate IHeld? Holder(int id, out string? refusal);

    /// <summary>After the confirmation: the confirmed processes found anew (the same number and start time) are ended, each
    /// waited a bounded time. Ended ones are gone, a number reused by another process and a client started meanwhile are left
    /// alone (P2-1 of the 0.4.0.2 audit, 07.11: the identity is checked and the signal sent through one reference, never by the
    /// number alone, so a number reused between the check and the signal is not hit).</summary>
    public static ProcessKillResult EndConfirmed(IReadOnlyCollection<ProcessIdentity> confirmed, IEnumerable<string>? names = null, TimeSpan? wait = null)
    {
        var found = new List<(int, string)>();
        foreach (var process in Find(names)) using (process) found.Add((process.Id, Name(process)));
        return EndConfirmed(confirmed, found, OperatingSystem.IsWindows() ? WindowsHeld.Open : LinuxHeld.Open, wait);
    }

    internal static ProcessKillResult EndConfirmed(IReadOnlyCollection<ProcessIdentity> confirmed, IEnumerable<(int Id, string Name)> found,
        Holder hold, TimeSpan? wait = null)
    {
        // The waits take whole milliseconds up to int.MaxValue (Process.WaitForExit, poll): another value is refused before any signal.
        if (wait is { } limit && (limit < TimeSpan.Zero || limit.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(wait));
        const string Unchecked = "не завершён: не удалось прочитать время запуска";
        var count = 0; var killed = 0; var failures = new List<string>();
        foreach (var (id, name) in found)
        {
            if (!confirmed.Any(x => x.Id == id)) continue;  // a client started after the question
            var label = $"{name} ({id})";
            // A start time not read (at the question or now) cannot prove the identity: nothing is signalled, and the process
            // is a failure, not one that exited by itself (Codex re-check P3-R1, 07.11).
            if (!confirmed.Any(x => x.Id == id && x.Start is not null)) { count++; failures.Add(label + ": " + Unchecked); continue; }
            using var held = hold(id, out var refusal);
            if (held is null)
            {
                if (refusal is not null) { count++; failures.Add(label + ": " + refusal); }
                continue;
            }
            var start = held.Start;
            if (start is null) { count++; failures.Add(label + ": " + Unchecked); continue; }
            if (!Matches(confirmed, new(id, start))) continue;
            count++;
            switch (held.Kill(out var error))
            {
                case OwnedProcess.Outcome.Exited: killed++; break;  // exited by itself: counts as ended
                case OwnedProcess.Outcome.Refused: failures.Add(label + ": " + error); break;
                default:
                    if (held.WaitForExit(wait ?? TimeSpan.FromSeconds(5))) killed++;
                    else failures.Add(label + ": не завершился за отведённое время");
                    break;
            }
        }
        return new(count, killed, failures);
    }

    /// <summary>Windows: the handle is opened once (Process.SafeHandle keeps it in the object), and the start time, the end and the
    /// wait all go through it; while it is open the number cannot pass to another process.</summary>
    private sealed class WindowsHeld(Process process) : IHeld
    {
        public static IHeld? Open(int id, out string? refusal)
        {
            refusal = null;
            Process process;
            try { process = Process.GetProcessById(id); } catch (ArgumentException) { return null; }
            try { _ = process.SafeHandle; return new WindowsHeld(process); }
            catch (InvalidOperationException) { process.Dispose(); return null; }
            catch (System.ComponentModel.Win32Exception ex) { process.Dispose(); refusal = ex.Message; return null; }
        }

        // A new object: its start time is not cached yet and is read through the handle just opened.
        public DateTime? Start => Identity(process).Start;

        public OwnedProcess.Outcome Kill(out string? error)
        {
            error = null;
            try { process.Kill(entireProcessTree: false); return OwnedProcess.Outcome.Signalled; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                if (Exited(process)) return OwnedProcess.Outcome.Exited;
                error = ex.Message; return OwnedProcess.Outcome.Refused;
            }
        }

        public bool WaitForExit(TimeSpan wait) => process.WaitForExit(wait);
        public void Dispose() => process.Dispose();
    }

    /// <summary>Linux: .NET signals and waits by number, so a pidfd is opened first. The start time is then read by the number: if it
    /// is the confirmed one, the pidfd (opened earlier) names the confirmed process, and the signal and the wait use the pidfd.
    /// No pidfd (a kernel before 5.3): nothing is signalled.</summary>
    private sealed class LinuxHeld(int pidfd, DateTime? start) : IHeld
    {
        private const int Esrch = 3;

        public static IHeld? Open(int id, out string? refusal)
        {
            refusal = null;
            var fd = OwnedProcess.Linux.Instance.PidfdOpen(id, out var errno);
            if (fd < 0)
            {
                if (errno != Esrch) refusal = "нет надёжной ссылки на процесс: " + new System.ComponentModel.Win32Exception(errno).Message;
                return null;
            }
            // A new object (Process caches its start time), read after the pidfd was opened.
            DateTime? start;
            try { using var process = Process.GetProcessById(id); start = Identity(process).Start; }
            catch (ArgumentException) { OwnedProcess.Linux.Instance.Close(fd); return null; }  // the number is free: gone
            // Unreadable: gone if the pidfd shows an exit, otherwise a process that cannot be checked.
            if (start is null && OwnedProcess.Linux.Instance.WaitExit(fd, TimeSpan.Zero)) { OwnedProcess.Linux.Instance.Close(fd); return null; }
            return new LinuxHeld(fd, start);
        }

        public DateTime? Start => start;

        public OwnedProcess.Outcome Kill(out string? error)
        {
            error = null;
            if (OwnedProcess.Linux.Instance.SendKill(pidfd, out var errno) == 0) return OwnedProcess.Outcome.Signalled;
            if (errno == Esrch) return OwnedProcess.Outcome.Exited;
            error = new System.ComponentModel.Win32Exception(errno).Message; return OwnedProcess.Outcome.Refused;
        }

        public bool WaitForExit(TimeSpan wait) => OwnedProcess.Linux.Instance.WaitExit(pidfd, wait);
        public void Dispose() => OwnedProcess.Linux.Instance.Close(pidfd);
    }

    private static int? LinuxUser(int pid)
    {
        try { return RealUid(File.ReadAllText($"/proc/{pid}/status")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The real UID from the text of /proc/&lt;pid&gt;/status («Uid: real effective saved fs»).</summary>
    public static int? RealUid(string status)
    {
        foreach (var line in status.Split('\n'))
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
                return int.TryParse(line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var uid) ? uid : null;
        return null;
    }

    public static string Name(Process process)
    {
        try { return process.ProcessName; } catch (InvalidOperationException) { return "процесс"; }
    }

    private static bool Exited(Process process) => Exited(() => process.HasExited);

    /// <summary>After a failed Kill: a process that is gone counts as ended; one whose state cannot be read (no right to
    /// query it) does not, and the loop goes on with the next process instead of stopping.</summary>
    internal static bool Exited(Func<bool> hasExited)
    {
        try { return hasExited(); }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
