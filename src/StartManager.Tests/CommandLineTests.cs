using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cli-tests-" + Guid.NewGuid());
    public CommandLineTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    public void InformationDoesNotResolveAProfile(string key)
    {
        var args = DiagnosticArguments.Parse([key]);
        Assert.Equal(key == "--version", args.Version);
        Assert.Equal(key != "--version", args.Help);
        Assert.Equal("", args.SettingsPath);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData("--help", "-h")]
    [InlineData("--settings", "-h")]
    [InlineData("--help", "--version")]
    [InlineData("--help", "--portable")]
    [InlineData("--version", "--single-instance")]
    [InlineData("--no-gui", "--normal")]
    [InlineData("--list", "relative.v8i")]
    [InlineData("--mode", "designer")]
    [InlineData("--launch", "bad-id")]
    [InlineData("--no-gui", "--no-gui")]
    public void InvalidCombinationsAreRejected(string first, string second) =>
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse([first, second]));

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void InformationAllowsNoGui(string key) => Assert.True(DiagnosticArguments.Parse(["--no-gui", key]).NoGui);

    [Fact] public void ListCanOpenGuiAndEmptyListIsDistinctFromOmitted()
    {
        var list = Path.Combine(root, "Общий список.v8i");
        Assert.Equal(list, DiagnosticArguments.Parse(["--list", list]).ListPath);
        Assert.Equal("", DiagnosticArguments.Parse(["--list", ""]).ListPath);
        Assert.Null(DiagnosticArguments.Parse([]).ListPath);
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--list", list, "--no-gui"]));
    }

    [Fact] public void LaunchDefaultsToSavedListAndEnterpriseWithoutChangingProfile()
    {
        var path = Path.Combine(root, "profile.json");
        var list = Path.Combine(root, "saved.v8i");
        new LibraryStore(path).Save(new Library { ListPath = list });
        var before = File.ReadAllBytes(path);
        var id = Guid.NewGuid();
        var options = DiagnosticArguments.Parse(["--launch", id.ToString(), "--settings", path, "--no-gui"]);
        Assert.Equal(new ShortcutRequest(path, list, id, false), options.LaunchRequest());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact] public void ExplicitEmptyListOverridesSavedList()
    {
        var path = Path.Combine(root, "profile.json");
        new LibraryStore(path).Save(new Library { ListPath = Path.Combine(root, "saved.v8i") });
        var id = Guid.NewGuid();
        var options = DiagnosticArguments.Parse(["--settings", path, "--list", "", "--mode", "designer", "--launch", id.ToString()]);
        Assert.Equal(new ShortcutRequest(path, "", id, true), options.LaunchRequest());
    }

    [Fact] public void LegacyShortcutRoundtripsThroughUnifiedParser()
    {
        var request = new ShortcutRequest(Path.Combine(root, "Профиль с пробелом.json"), "", Guid.NewGuid(), true);
        var options = DiagnosticArguments.Parse([.. request.Arguments(), "--no-gui", "--log-level", "off"]);
        Assert.Equal(request, options.LaunchRequest());
        Assert.True(options.NoGui);
        Assert.Equal(DiagnosticLevel.Off, options.Level);
    }

    [Theory]
    [InlineData(new[] { "--install-desktop" }, DesktopCommand.Install)]
    [InlineData(new[] { "--uninstall-desktop" }, DesktopCommand.Uninstall)]
    [InlineData(new[] { "--no-gui", "--install-desktop" }, DesktopCommand.Install)]
    [InlineData(new[] { "--uninstall-desktop", "--no-gui" }, DesktopCommand.Uninstall)]
    public void DesktopCommandsParseWithoutProfile(string[] args, DesktopCommand expected)
    {
        var options = DiagnosticArguments.Parse(args);
        Assert.Equal(expected, options.Desktop);
        Assert.Equal(args.Contains("--no-gui"), options.NoGui);
        Assert.Equal("", options.SettingsPath);
        Assert.Equal(UpdateCommand.None, options.Update);
        Assert.Equal(DesktopCommand.None, DiagnosticArguments.Parse([]).Desktop);
    }

    [Theory]
    [InlineData("--install-desktop", "--uninstall-desktop")]
    [InlineData("--install-desktop", "--install-desktop")]
    [InlineData("--install-desktop", "--update")]
    [InlineData("--rollback", "--uninstall-desktop")]
    [InlineData("--uninstall-desktop", "--help")]
    [InlineData("--version", "--install-desktop")]
    [InlineData("--install-desktop", "--portable")]
    [InlineData("--uninstall-desktop", "--settings")]
    public void DesktopCommandsStandAlone(string first, string second) =>
        Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse([first, second]));

    [Fact] public void DesktopCommandRejectsLaunch()
    {
        var error = Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--install-desktop", "--launch", Guid.NewGuid().ToString()]));
        Assert.Contains("ярлыка меню приложений используются отдельно", error.Message);
        Assert.Contains("--install-desktop или --uninstall-desktop",
            Assert.Throws<ArgumentException>(() => DiagnosticArguments.Parse(["--uninstall-desktop", "--install-desktop"])).Message);
    }

    [Fact] public void HelpDescribesDesktopCommands()
    {
        Assert.Contains("BaseNavigator --install-desktop | --uninstall-desktop", CommandLineHelp.Text);
        Assert.Contains("  --install-desktop   Только Linux:", CommandLineHelp.Text);
        Assert.Contains("  --uninstall-desktop Только Linux:", CommandLineHelp.Text);
        Assert.Contains("команды обновления и ярлыка меню приложений используются отдельно", CommandLineHelp.Text);
        Assert.Contains("--no-gui без --launch (или справки, версии, команд обновления и ярлыка меню приложений) — ошибка.", CommandLineHelp.Text);
    }
}
