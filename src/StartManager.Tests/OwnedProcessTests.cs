using System.Diagnostics;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Codex re-check 8, RC8-1: the Linux path of stopping the root gh, through a substitute system — no signal reaches a real process.
public sealed class OwnedProcessTests
{
    private sealed class FakeSystem : OwnedProcess.ISystem
    {
        public int OpenErrno, SendErrno;
        public long? Now = 1234;
        public readonly List<string> Calls = [];
        public int PidfdOpen(int pid, out int errno)
        {
            Calls.Add($"open {pid}"); errno = OpenErrno;
            return OpenErrno == 0 ? 7 : -1;
        }
        public int SendKill(int pidfd, out int errno) { Calls.Add($"kill {pidfd}"); errno = SendErrno; return SendErrno == 0 ? 0 : -1; }
        public void Close(int fd) => Calls.Add($"close {fd}");
        public long? StartTime(int pid) { Calls.Add($"stat {pid}"); return Now; }
    }

    [Fact] public void TheSameProcessIsSignalledThroughItsPidfd()
    {
        var system = new FakeSystem();
        Assert.Equal(OwnedProcess.Outcome.Signalled, OwnedProcess.ForTest(42, 1234, system).Kill());
        Assert.Equal(["open 42", "stat 42", "kill 7", "close 7"], system.Calls);
    }

    [Fact] public void AnotherProcessUnderTheNumberIsNeverSignalled()
    {
        // The root has exited and its number was taken: another start time — nothing is sent, the root counts as gone.
        var system = new FakeSystem { Now = 9999 };
        Assert.Equal(OwnedProcess.Outcome.Exited, OwnedProcess.ForTest(42, 1234, system).Kill());
        Assert.Equal(["open 42", "stat 42", "close 7"], system.Calls);
        system = new FakeSystem { Now = null };
        Assert.Equal(OwnedProcess.Outcome.Exited, OwnedProcess.ForTest(42, 1234, system).Kill());
        Assert.DoesNotContain(system.Calls, x => x.StartsWith("kill"));
    }

    [Theory]
    [InlineData(38, "Refused")]  // ENOSYS: no pidfd in this kernel
    [InlineData(1, "Refused")]   // EPERM
    [InlineData(3, "Exited")]    // ESRCH: the process is gone
    public void WithoutAPidfdNothingIsSent(int errno, string outcome)
    {
        var system = new FakeSystem { OpenErrno = errno };
        Assert.Equal(outcome, OwnedProcess.ForTest(42, 1234, system).Kill().ToString());
        Assert.Equal(["open 42"], system.Calls);
    }

    [Fact] public void AnUnknownStartTimeRefusesWithoutAnySystemCall()
    {
        var system = new FakeSystem();
        Assert.Equal(OwnedProcess.Outcome.Refused, OwnedProcess.ForTest(42, null, system).Kill());
        Assert.Empty(system.Calls);
    }

    [Theory]
    [InlineData(3, "Exited")]
    [InlineData(1, "Refused")]
    public void AFailedSignalIsReported(int errno, string outcome)
    {
        var system = new FakeSystem { SendErrno = errno };
        Assert.Equal(outcome, OwnedProcess.ForTest(42, 1234, system).Kill().ToString());
        Assert.Equal("close 7", system.Calls[^1]);
    }

    [Fact] public void TheStartTimeIsField22OfStat()
    {
        var fields = string.Join(' ', Enumerable.Range(3, 50).Select(x => x == 22 ? "987654" : "0"));
        Assert.Equal(987654, OwnedProcess.ParseStartTime($"42 (gh (x) y) {fields}"));
        Assert.Null(OwnedProcess.ParseStartTime("42 (gh) S 1 2"));
        Assert.Null(OwnedProcess.ParseStartTime("no parenthesis"));
    }

    [Fact] public void AChildThatHasExitedKeepsNoIdentity()
    {
        // Recorded right after the start: a child already gone may have lent its number to another process — no identity is kept.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !File.Exists(dotnet)) return;
        using var child = Process.Start(new ProcessStartInfo(dotnet, "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        child.WaitForExit();
        var system = new FakeSystem();
        Assert.Equal(OwnedProcess.Outcome.Refused, OwnedProcess.Of(child, system).Kill());
        Assert.DoesNotContain(system.Calls, x => x.StartsWith("open") || x.StartsWith("kill"));
    }
}
