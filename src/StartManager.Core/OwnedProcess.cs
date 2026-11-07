using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StartManager.Core;

/// <summary>
/// A child process the program has just started, ended only as that very process (Codex re-check 8, RC8-1). Windows: the Process
/// object from Process.Start holds a handle to it, and Kill goes through that handle. Linux: a number alone may already belong to
/// another process once the child has exited and been reaped (.NET reaps its children on SIGCHLD by itself), so the start time
/// (field 22 of /proc/&lt;pid&gt;/stat) is recorded at once, while the child surely is ours, and Kill opens a pidfd, checks the start
/// time again through it and only then signals the pidfd — never the number. No pidfd (an old kernel), no recorded start time: no
/// signal at all, the cancel is reported as not completed.
/// </summary>
internal sealed class OwnedProcess
{
    public enum Outcome { Signalled, Exited, Refused }

    /// <summary>The system calls of the Linux path; tests substitute one that sends no signal.</summary>
    internal interface ISystem
    {
        /// <summary>pidfd_open(pid, 0): the descriptor, or -1 with errno.</summary>
        int PidfdOpen(int pid, out int errno);
        /// <summary>pidfd_send_signal(fd, SIGKILL, NULL, 0): 0, or -1 with errno.</summary>
        int SendKill(int pidfd, out int errno);
        void Close(int fd);
        /// <summary>Field 22 of /proc/&lt;pid&gt;/stat, the start time in clock ticks since boot; null when it cannot be read.</summary>
        long? StartTime(int pid);
    }

    private const int Esrch = 3;
    private readonly Process? handle;
    private readonly ISystem? system;
    private readonly long? started;

    public int Id { get; }

    private OwnedProcess(int id, Process? handle, ISystem? system, long? started) { Id = id; this.handle = handle; this.system = system; this.started = started; }

    /// <summary>Right after Process.Start: on Linux the start time is read before anything can reap the child, and kept only if the
    /// child had not exited by then (afterwards its number may name another process).</summary>
    public static OwnedProcess Of(Process justStarted, ISystem? linux = null)
    {
        if (OperatingSystem.IsWindows() && linux is null) return new(justStarted.Id, justStarted, null, null);
        var system = linux ?? Linux.Instance;
        var started = system.StartTime(justStarted.Id);
        bool exited;
        try { exited = justStarted.HasExited; } catch (InvalidOperationException) { exited = true; }
        return new(justStarted.Id, null, system, exited ? null : started);
    }

    /// <summary>Tests: a second, independent identity of the same process, taken while this one is in use (on Windows the handle of
    /// the original keeps the number from being reused while the new handle is opened).</summary>
    internal OwnedProcess Twin()
    {
        if (handle is null) return new(Id, null, system, started);
        var other = Process.GetProcessById(Id);
        _ = other.SafeHandle;
        return new(Id, other, null, null);
    }

    /// <summary>Whether the process has exited; Windows and tests with a handle only.</summary>
    internal bool? HasExited => handle is null ? null : handle.HasExited;

    /// <summary>Tests: a process of a known number and start time under a substitute system.</summary>
    internal static OwnedProcess ForTest(int id, long? started, ISystem system) => new(id, null, system, started);

    /// <summary>Ends the process if it is still the one started; Exited when it is gone (nothing signalled), Refused when it could not
    /// be done safely (nothing signalled).</summary>
    public Outcome Kill()
    {
        if (handle is not null)
        {
            try { handle.Kill(entireProcessTree: false); return Outcome.Signalled; }
            catch (InvalidOperationException) { return Outcome.Exited; }
        }
        if (system is null || started is null) return Outcome.Refused;
        var fd = system.PidfdOpen(Id, out var errno);
        if (fd < 0) return errno == Esrch ? Outcome.Exited : Outcome.Refused;
        try
        {
            // The pidfd names the process that had the number when it was opened; the same start time proves it is ours.
            if (system.StartTime(Id) != started) return Outcome.Exited;
            if (system.SendKill(fd, out errno) == 0) return Outcome.Signalled;
            return errno == Esrch ? Outcome.Exited : Outcome.Refused;
        }
        finally { system.Close(fd); }
    }

    /// <summary>The end of a wait in TickCount64 milliseconds; a negative wait ends at once (Codex re-check P3-R2, 07.11).</summary>
    internal static long Deadline(long now, TimeSpan wait) => now + (long)Math.Max(0, wait.TotalMilliseconds);

    /// <summary>One poll's timeout: never negative (that would wait forever) and at most int.MaxValue, the rest in a further call.</summary>
    internal static int PollTimeout(long deadline, long now) => (int)Math.Clamp(deadline - now, 0, int.MaxValue);

    /// <summary>Field 22 of a /proc stat line: the fields after the name (which may hold spaces and parentheses) follow the last «)».</summary>
    internal static long? ParseStartTime(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After the name: state is field 3, so field 22 is at index 19.
        return fields.Length > 19 && long.TryParse(fields[19], out var value) ? value : null;
    }

    /// <summary>The Linux system calls; also used by ClientProcesses for processes it has found (07.11).</summary>
    internal sealed class Linux : ISystem
    {
        public static readonly Linux Instance = new();
        // The generic numbers of the newer system calls, the same on x86_64 and arm64.
        private const long SysPidfdOpen = 434, SysPidfdSendSignal = 424;
        private const int Sigkill = 9;

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long Syscall2(long number, int pid, uint flags);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long Syscall4(long number, int fd, int signal, IntPtr info, uint flags);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int CloseFd(int fd);

        public int PidfdOpen(int pid, out int errno)
        {
            try
            {
                var fd = Syscall2(SysPidfdOpen, pid, 0);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
                return (int)fd;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { errno = 38; /* ENOSYS */ return -1; }
        }

        public int SendKill(int pidfd, out int errno)
        {
            var result = Syscall4(SysPidfdSendSignal, pidfd, Sigkill, IntPtr.Zero, 0);
            errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            return (int)result;
        }

        public void Close(int fd) => CloseFd(fd);

        [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short Revents; }
        [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollFd fds, nuint count, int timeout);

        /// <summary>Waits (bounded) until the process of the pidfd exits: the pidfd becomes readable then, whoever its parent is.</summary>
        public bool WaitExit(int pidfd, TimeSpan wait)
        {
            const short PollIn = 1; const int Eintr = 4;
            var deadline = Deadline(Environment.TickCount64, wait);
            while (true)
            {
                var entry = new PollFd { Fd = pidfd, Events = PollIn };
                var result = Poll(ref entry, 1, PollTimeout(deadline, Environment.TickCount64));
                if (result > 0) return true;
                // A piece that timed out or a signal (EINTR): the wait goes on until the deadline; another error ends it.
                if ((result < 0 && Marshal.GetLastPInvokeError() != Eintr) || Environment.TickCount64 >= deadline) return false;
            }
        }

        public long? StartTime(int pid)
        {
            try { return ParseStartTime(File.ReadAllText($"/proc/{pid}/stat")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
