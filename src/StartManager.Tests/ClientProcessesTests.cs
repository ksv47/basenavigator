using System.Diagnostics;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>A test with a real dummy process: a copy of a sleep program under a unique name. On Linux the program must keep
/// running under that name — a multi-call binary (rust-coreutils since Ubuntu 25.10, busybox) picks its tool by argv[0] and
/// exits at once (Linux VM, 07.11) — so the first candidate that does is used; none: the test is skipped with the reason.</summary>
public sealed class DummyProcessFactAttribute : FactAttribute
{
    internal static readonly string? Source;
    /// <summary>A probe that could not be cleaned up (its process not ended, its folder not deleted): the tests fail with it.</summary>
    internal static readonly string? Problem;
    private static readonly string reasons = "";

    static DummyProcessFactAttribute()
    {
        if (OperatingSystem.IsWindows()) { Source = Path.Combine(Environment.SystemDirectory, "ping.exe"); return; }
        var rejected = new List<string>();
        foreach (var program in new[] { "/usr/bin/gnusleep", "/bin/sleep", "/usr/bin/sleep" })
        {
            var (reason, cleanup) = Probe(program);
            Problem ??= cleanup;
            if (reason is null) { Source = program; break; }
            rejected.Add($"{program}: {reason}");
        }
        reasons = string.Join("; ", rejected);
    }

    public DummyProcessFactAttribute()
    {
        if (Source is null && Problem is null) Skip = "No sleep program here keeps running under another name: " + reasons;
    }

    /// <summary>Starts a program, retrying while a fresh copy is «text file busy» (ETXTBSY): a fork of a parallel test may still
    /// hold it open for writing.</summary>
    internal static Process Start(ProcessStartInfo start)
    {
        for (var attempt = 1; ; attempt++)
        {
            const int TextFileBusy = 26;
            try { return Process.Start(start)!; }
            catch (System.ComponentModel.Win32Exception ex) when (!OperatingSystem.IsWindows() && ex.NativeErrorCode == TextFileBusy && attempt < 20)
            { Thread.Sleep(50); }
        }
    }

    /// <summary>Null reason: a renamed copy keeps running. Otherwise why not — «incompatible» (the copy exits at once: a multi-call
    /// binary such as rust-coreutils or busybox) or «environment» (missing, not copied, not started). Cleanup: what was not undone.</summary>
    private static (string? Reason, string? Cleanup) Probe(string program)
    {
        if (!File.Exists(program)) return ("environment: missing", null);
        var directory = Path.Combine(Path.GetTempPath(), "bn-dummy-probe-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        string? reason, cleanup = null;
        try
        {
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, "bnprobe");
            File.Copy(program, copy);
            process = Start(new ProcessStartInfo(copy, "5") { UseShellExecute = false, RedirectStandardError = true });
            reason = process.WaitForExit(300)
                ? $"incompatible: the renamed copy exited at once, code {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}" : null;
        }
        catch (System.ComponentModel.Win32Exception ex) { reason = $"environment: not started (errno {ex.NativeErrorCode}): {ex.Message}"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reason = "environment: not copied: " + ex.Message; }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    if (!process.WaitForExit(5000)) cleanup = $"the probe process {process.Id} of {program} did not end";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { cleanup = $"the probe process of {program} was not ended: {ex.Message}"; }
                process.Dispose();
            }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanup ??= $"the probe folder {directory} was not deleted: {ex.Message}"; }
        }
        return (reason, cleanup);
    }
}

public sealed class ClientProcessesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BaseNavigator-processes-" + Guid.NewGuid().ToString("N"));
    // Linux keeps 15 characters of a process name.
    private readonly string name = "bn" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<Process> started = [];
    public ClientProcessesTests() => Directory.CreateDirectory(directory);
    public void Dispose()
    {
        foreach (var process in started) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); }
        try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A long-running harmless program under a unique name: only it can match.
    // Linux, a rare failure in full parallel runs (07.11; the cause is not proven, both races below are guarded):
    // - the fresh copy may be «text file busy» (ETXTBSY) while a fork of a parallel test still holds it open for writing;
    // - Process.Start may return before the child has its new name: exec closes the CLOEXEC pipe .NET waits on before the
    //   kernel renames the task, so the test waits until the process is listed under its name.
    private Process StartDummy()
    {
        if (DummyProcessFactAttribute.Problem is { } problem) Assert.Fail("the dummy probe was not cleaned up: " + problem);
        var source = DummyProcessFactAttribute.Source ?? throw new InvalidOperationException("no dummy program (the test should be skipped)");
        var copy = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (!File.Exists(copy)) File.Copy(source, copy);
        var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("-n"); start.ArgumentList.Add("60"); start.ArgumentList.Add("127.0.0.1"); }
        else start.ArgumentList.Add("60");
        var process = DummyProcessFactAttribute.Start(start);
        started.Add(process);
        var stopwatch = Stopwatch.StartNew();
        while (!Listed(process.Id) && !process.HasExited && stopwatch.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        if (process.HasExited)
            Assert.Fail($"the dummy {source} copied as {name} exited at once, code {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
        Assert.True(Listed(process.Id), $"the started process {process.Id} is not listed as {name}");
        return process;
    }

    private bool Listed(int id)
    {
        var found = ClientProcesses.Find([name]);
        try { return found.Any(x => x.Id == id); } finally { foreach (var process in found) process.Dispose(); }
    }

    [Fact] public void ClientNamesAreTheClientsAndTheStarterOnly()
    {
        Assert.Equal(["1cv8", "1cv8c", "1cv8s", "1cestart"], ClientProcesses.ClientNames);
        Assert.DoesNotContain(ClientProcesses.ClientNames, x => x.StartsWith('r'));
    }

    [DummyProcessFact] public void FoundProcessesAreEndedAndCounted()
    {
        var first = StartDummy(); var second = StartDummy();
        var found = ClientProcesses.Find([name, name.ToUpperInvariant()]);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), found.Select(x => x.Id).Order());
        var identities = found.Select(ClientProcesses.Identity).ToHashSet();
        foreach (var process in found) process.Dispose();
        var result = ClientProcesses.EndConfirmed(identities, [name, name.ToUpperInvariant()]);
        Assert.Equal((2, 2), (result.Found, result.Killed));
        Assert.Empty(result.Failures);
        Assert.True(first.WaitForExit(5000) && second.WaitForExit(5000));
        // Windows keeps an ended process listed while a handle to it is open (ours, and briefly the system's):
        // the handles go first, then the list is given a few seconds (a slow CI machine showed it, 04.10.2026).
        foreach (var process in started) process.Dispose();
        started.Clear();
        var stopwatch = Stopwatch.StartNew();
        while (ClientProcesses.Find([name]).Count > 0 && stopwatch.ElapsedMilliseconds < 5000) Thread.Sleep(100);
        Assert.Empty(ClientProcesses.Find([name]));
    }

    [DummyProcessFact] public void AProcessThatExitedBeforeTheEndIsNotFound()
    {
        var process = StartDummy();
        var identities = new HashSet<ProcessIdentity> { ClientProcesses.Identity(process) };
        process.Kill(); Assert.True(process.WaitForExit(5000));
        var result = ClientProcesses.EndConfirmed(identities, [name]);
        Assert.Equal((0, 0), (result.Found, result.Killed));
        Assert.Empty(result.Failures);
    }

    [Fact] public void TheOwnerIsTheRealUidOfTheProcess()
    {
        Assert.Equal(1000, ClientProcesses.RealUid("Name:\t1cv8c\nUmask:\t0002\nUid:\t1000\t0\t0\t0\nGid:\t1000\t1000\t1000\t1000\n"));
        Assert.Equal(0, ClientProcesses.RealUid("Uid:\t0\t0\t0\t0"));
        Assert.Null(ClientProcesses.RealUid("Name:\tx\nGid:\t1\n"));
        Assert.Null(ClientProcesses.RealUid("Uid:\tabc"));
    }

    [Fact] public void AnUnreadableStateIsAFailureNotAnEscape()
    {
        Assert.False(ClientProcesses.Exited(() => throw new System.ComponentModel.Win32Exception(5)));
        Assert.True(ClientProcesses.Exited(() => throw new InvalidOperationException()));
        Assert.True(ClientProcesses.Exited(() => true));
        Assert.False(ClientProcesses.Exited(() => false));
    }

    [DummyProcessFact] public void OnlyTheConfirmedProcessesAreEnded()
    {
        var confirmed = StartDummy(); var before = StartDummy();
        var identities = new HashSet<ProcessIdentity> { ClientProcesses.Identity(confirmed) };
        var later = StartDummy();
        // A reused number is another process: the same Id with another start time does not match.
        var other = ClientProcesses.EndConfirmed(new HashSet<ProcessIdentity> { new(confirmed.Id, confirmed.StartTime.AddSeconds(-30)) }, [name]);
        Assert.Equal((0, 0), (other.Found, other.Killed));
        Assert.False(confirmed.HasExited);
        var result = ClientProcesses.EndConfirmed(identities, [name]);
        Assert.Equal((1, 1), (result.Found, result.Killed));
        Assert.True(confirmed.WaitForExit(5000));
        Assert.False(before.HasExited); Assert.False(later.HasExited);
    }

    [Fact] public void AnUnknownStartTimeIsNeverConfirmed()
    {
        var start = new DateTime(2026, 9, 27, 1, 0, 0);
        Assert.False(ClientProcesses.Matches(new HashSet<ProcessIdentity> { new(42, null) }, new(42, null)));
        Assert.False(ClientProcesses.Matches(new HashSet<ProcessIdentity> { new(42, start) }, new(42, null)));
        Assert.True(ClientProcesses.Matches(new HashSet<ProcessIdentity> { new(42, start) }, new(42, start)));
    }

    [Fact] public void NothingFoundGivesAnEmptyResult()
    {
        var result = ClientProcesses.EndConfirmed(new HashSet<ProcessIdentity>(), [name]);
        Assert.Equal((0, 0), (result.Found, result.Killed));
    }

    // The system side substituted: what the reference shows and whether a signal was sent (P2-1, 07.11).
    private sealed class Held(List<string> calls, DateTime? start, OwnedProcess.Outcome outcome = OwnedProcess.Outcome.Signalled, bool exits = true) : ClientProcesses.IHeld
    {
        public DateTime? Start { get { calls.Add("start"); return start; } }
        public OwnedProcess.Outcome Kill(out string? error)
        {
            calls.Add("kill"); error = outcome == OwnedProcess.Outcome.Refused ? "Operation not permitted" : null; return outcome;
        }
        public bool WaitForExit(TimeSpan wait) { calls.Add("wait"); return exits; }
        public void Dispose() => calls.Add("close");
    }

    private static readonly DateTime Confirmed = new(2026, 11, 7, 10, 0, 0);

    private static (ProcessKillResult Result, List<string> Calls) End(Func<List<string>, ClientProcesses.IHeld?> held, string? refusal = null)
    {
        var calls = new List<string>();
        var result = ClientProcesses.EndConfirmed(new HashSet<ProcessIdentity> { new(42, Confirmed) }, [(43, "1cv8c"), (42, "1cv8c")],
            (int id, out string? why) => { calls.Add($"open {id}"); why = refusal; return held(calls); });
        return (result, calls);
    }

    [Fact] public void ANumberReusedAfterTheConfirmationIsNotSignalled()
    {
        // Between the confirmation and the end the client exited and its number went to another process: the reference taken
        // now shows the newcomer's start time, and nothing is signalled. A number not confirmed is not even opened.
        var (result, calls) = End(c => new Held(c, Confirmed.AddMilliseconds(10)));
        Assert.Equal(["open 42", "start", "close"], calls);
        Assert.Equal((0, 0, 0), (result.Found, result.Killed, result.Failures.Count));
    }

    [Fact] public void AnUnreadableStartTimeIsAFailureNotAnExit()
    {
        // Codex re-check P3-R1: read neither now (through the reference) nor at the question: nothing is signalled, and the
        // status line names the process among the failures instead of «завершились сами».
        const string Failure = "1cv8c (42): не завершён: не удалось прочитать время запуска";
        var (result, calls) = End(c => new Held(c, null));
        Assert.Equal(["open 42", "start", "close"], calls);
        Assert.Equal((1, 0), (result.Found, result.Killed));
        Assert.Equal([Failure], result.Failures);
        Assert.Equal($"Найдено процессов: 1. Завершено: 0. Не удалось завершить: {Failure}", result.Status(1));
        var opened = 0;
        var atQuestion = ClientProcesses.EndConfirmed(new HashSet<ProcessIdentity> { new(42, null) }, [(42, "1cv8c")],
            (int id, out string? why) => { opened++; why = null; return null; });
        Assert.Equal(0, opened);
        Assert.Equal((1, 0), (atQuestion.Found, atQuestion.Killed));
        Assert.Equal([Failure], atQuestion.Failures);
        Assert.DoesNotContain("сами", atQuestion.Status(1));
    }

    [Fact] public void TheStatusLineCountsTheProcessesGoneBeforeTheConfirmation()
    {
        Assert.Equal("Найдено процессов: 3. Завершено: 1. Завершились сами до подтверждения: 2.", new ProcessKillResult(1, 1, []).Status(3));
        Assert.Equal("Найдено процессов: 5. Завершено: 0. Не удалось завершить: a; b; c и ещё 2.",
            new ProcessKillResult(5, 0, ["a", "b", "c", "d", "e"]).Status(5));
    }

    [Fact] public void AWaitBeyondTheSupportedRangeIsRefusedBeforeAnySignal()
    {
        foreach (var wait in new[] { TimeSpan.FromDays(30), TimeSpan.FromMilliseconds(-1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => ClientProcesses.EndConfirmed(new HashSet<ProcessIdentity> { new(42, Confirmed) },
                [(42, "1cv8c")], (int id, out string? why) => throw new InvalidOperationException("opened"), wait));
    }

    [Fact] public void APollTimeoutIsNeverNegativeNorBeyondInt()
    {
        // Codex re-check P3-R2: a negative poll timeout waits forever; a long wait is cut into pieces of at most int.MaxValue.
        var month = (long)TimeSpan.FromDays(30).TotalMilliseconds;
        Assert.Equal(int.MaxValue, OwnedProcess.PollTimeout(OwnedProcess.Deadline(1000, TimeSpan.FromDays(30)), 1000));
        Assert.Equal(int.MaxValue, OwnedProcess.PollTimeout(OwnedProcess.Deadline(0, TimeSpan.MaxValue), 0));
        Assert.Equal(1, OwnedProcess.PollTimeout(OwnedProcess.Deadline(1000, TimeSpan.FromDays(30)), 1000 + month - 1));
        Assert.Equal(0, OwnedProcess.PollTimeout(1000, 2000));
        Assert.Equal(0, OwnedProcess.PollTimeout(OwnedProcess.Deadline(1000, TimeSpan.FromSeconds(-5)), 1000));
        Assert.Equal(5000, OwnedProcess.PollTimeout(OwnedProcess.Deadline(1000, TimeSpan.FromSeconds(5)), 1000));
    }

    [Fact] public void TheSignalGoesThroughTheReferenceWhoseIdentityWasChecked()
    {
        var (result, calls) = End(c => new Held(c, Confirmed));
        Assert.Equal(["open 42", "start", "kill", "wait", "close"], calls);
        Assert.Equal((1, 1, 0), (result.Found, result.Killed, result.Failures.Count));
        // Exited by itself before the signal: ended, no wait.
        (result, calls) = End(c => new Held(c, Confirmed, OwnedProcess.Outcome.Exited));
        Assert.Equal(["open 42", "start", "kill", "close"], calls);
        Assert.Equal((1, 1), (result.Found, result.Killed));
        (result, _) = End(c => new Held(c, Confirmed, exits: false));
        Assert.Equal((1, 0), (result.Found, result.Killed));
        Assert.Equal(["1cv8c (42): не завершился за отведённое время"], result.Failures);
        (result, _) = End(c => new Held(c, Confirmed, OwnedProcess.Outcome.Refused));
        Assert.Equal(["1cv8c (42): Operation not permitted"], result.Failures);
    }

    [Fact] public void WithoutASafeReferenceNothingIsSignalled()
    {
        // No pidfd (an old kernel) or no handle: a failure is reported, nothing is signalled.
        var (result, calls) = End(_ => null, "нет надёжной ссылки на процесс: Function not implemented");
        Assert.Equal(["open 42"], calls);
        Assert.Equal((1, 0), (result.Found, result.Killed));
        Assert.Equal(["1cv8c (42): нет надёжной ссылки на процесс: Function not implemented"], result.Failures);
        // Gone before the reference was taken: neither found nor a failure.
        (result, calls) = End(_ => null);
        Assert.Equal(["open 42"], calls);
        Assert.Equal((0, 0, 0), (result.Found, result.Killed, result.Failures.Count));
    }
}
