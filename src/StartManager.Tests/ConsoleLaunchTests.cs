using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Codex re-check 4, N2: with a pending notice about removed passwords, a shortcut start with --no-gui printed the notice instead
// of its error. Both are printed now, each to its own stream; the exit code is the start's, set apart from this.
public sealed class ConsoleLaunchTests
{
    private const string Notice = "Сохранённые пароли прежней версии удалены у баз: 1 — введите их заново в свойствах.";

    private static (string Output, string Errors) Run(string? error, string? notice)
    {
        var (output, errors) = (new StringWriter(), new StringWriter());
        ConsoleLaunch.Report(error, notice, output, errors);
        return (output.ToString().Trim(), errors.ToString().Trim());
    }

    [Theory]
    [InlineData("Не удалось прочитать профиль/список или найти базу: …")]
    [InlineData("Не удалось запустить клиент 1С: …")]
    [InlineData("База уже запущена, но история не сохранена. …")]
    public void AnErrorIsPrintedWithThePendingNotice(string error)
    {
        Assert.Equal((Notice, error), Run(error, Notice));
        Assert.Equal(("", error), Run(error, null));
    }

    [Fact] public void ASuccessIsPrintedWithThePendingNotice()
    {
        Assert.Equal((ConsoleLaunch.Started + Environment.NewLine + Notice, ""), Run(null, Notice));
        Assert.Equal((ConsoleLaunch.Started, ""), Run(null, null));
    }
}
