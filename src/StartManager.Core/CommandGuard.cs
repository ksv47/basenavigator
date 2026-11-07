namespace StartManager.Core;

/// <summary>An asynchronous command of a button: its handler is <c>async void</c>, so an exception it does not handle would reach the
/// dispatcher and end the program (Codex full audit 24.10, P2-7: a failing folder picker of «Дублирование»). Such an exception is
/// given to <paramref name="failed"/> instead; a failure of that one is given to <paramref name="lost"/> and goes no further.
/// A cancellation (an <see cref="OperationCanceledException"/> or a cancelled task) is not a failure: it goes to
/// <paramref name="cancelled"/> only, nothing is shown (Codex re-check 25.10, R4).</summary>
public static class CommandGuard
{
    public static async Task Run(Func<Task> action, Func<Exception, Task> failed, Action<Exception>? lost = null, Action? cancelled = null)
    {
        try { await action(); }
        catch (OperationCanceledException) { cancelled?.Invoke(); }
        catch (Exception error)
        {
            try { await failed(error); }
            catch (Exception shown) { lost?.Invoke(shown); }
        }
    }
}
