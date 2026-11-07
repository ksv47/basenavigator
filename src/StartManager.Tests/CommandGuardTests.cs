using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Codex full audit 24.10, P2-7: a folder picker that fails in an asynchronous button handler ended the program. The commands of the
// windows go through CommandGuard: the failure is given to the window, nothing reaches the dispatcher.
public sealed class CommandGuardTests
{
    [Fact] public async Task AFailingCommandIsGivenToTheWindowAndGoesNoFurther()
    {
        Exception? shown = null, lost = null;
        var picker = new IOException("The portal does not answer.");
        await CommandGuard.Run(() => Task.FromException(picker), error => { shown = error; return Task.CompletedTask; }, error => lost = error);
        Assert.Same(picker, shown); Assert.Null(lost);
        // The message itself fails (the window was closed meanwhile): logged, still nothing escapes.
        var closed = new InvalidOperationException("closed");
        await CommandGuard.Run(async () => { await Task.Yield(); throw picker; }, _ => throw closed, error => lost = error);
        Assert.Same(closed, lost);
        await CommandGuard.Run(() => throw picker, _ => throw closed);
        // A command that works is not touched.
        var ran = false; shown = null;
        await CommandGuard.Run(() => { ran = true; return Task.CompletedTask; }, error => { shown = error; return Task.CompletedTask; });
        Assert.True(ran); Assert.Null(shown);
    }

    // Codex re-check 25.10, R4: a cancellation is no failure: neither a cancelled task nor a thrown OperationCanceledException is
    // shown as «Команда не выполнена».
    [Fact] public async Task ACancelledCommandIsNotAFailure()
    {
        var failures = 0; var cancellations = 0;
        Task Failed(Exception _) { failures++; return Task.CompletedTask; }
        await CommandGuard.Run(() => Task.FromCanceled(new CancellationToken(true)), Failed, cancelled: () => cancellations++);
        await CommandGuard.Run(() => throw new OperationCanceledException(), Failed, cancelled: () => cancellations++);
        await CommandGuard.Run(async () => { await Task.Yield(); throw new TaskCanceledException(); }, Failed, cancelled: () => cancellations++);
        Assert.Equal((0, 3), (failures, cancellations));
        // Without a handler for it a cancellation goes nowhere either.
        await CommandGuard.Run(() => throw new OperationCanceledException(), Failed);
        Assert.Equal(0, failures);
    }
}
