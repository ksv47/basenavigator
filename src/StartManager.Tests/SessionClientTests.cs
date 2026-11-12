using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Текущая сессия»: the client of the same installation and the run mode switches; the configurator is unaffected.
public sealed class SessionClientTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-session-" + Guid.NewGuid().ToString("N"));
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    private string Thick => Path.Combine(folder, Name("1cv8"));
    private string Thin => Path.Combine(folder, Name("1cv8c"));
    private static BaseEntry Entry => new() { Name = "Test", Kind = ConnectionKind.Server, Location = "server\\base" };
    public SessionClientTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Thick, "fixture"); File.WriteAllText(Thin, "fixture");
    }
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData(SessionClient.Auto, "1cv8", null)]
    [InlineData(SessionClient.Ordinary, "1cv8", "/RunModeOrdinaryApplication")]
    [InlineData(SessionClient.Thick, "1cv8", "/RunModeManagedApplication")]
    [InlineData(SessionClient.Thin, "1cv8c", null)]
    public void EnterpriseFollowsTheSessionClient(SessionClient session, string client, string? mode)
    {
        var start = Launcher.Build(new Library { Executable = Thick }, Entry, false, [], session);
        Assert.Equal(Path.Combine(folder, Name(client)), start.FileName);
        var expected = new List<string> { "ENTERPRISE", "/S", Entry.Location };
        if (mode is not null) expected.Add(mode);
        Assert.Equal(expected, start.ArgumentList);
    }

    [Fact] public void ThickModesReplaceTheThinClientOfTheSameInstallation()
    {
        var start = Launcher.Build(new Library { Executable = Thin }, Entry, false, [], SessionClient.Ordinary);
        Assert.Equal(Thick, start.FileName);
    }

    [Theory]
    [InlineData(SessionClient.Ordinary)] [InlineData(SessionClient.Thick)] [InlineData(SessionClient.Thin)]
    public void DesignerIgnoresTheSessionClient(SessionClient session)
    {
        var start = Launcher.Build(new Library { Executable = Thick }, Entry, true, [], session);
        Assert.Equal(Thick, start.FileName);
        Assert.Equal(new[] { "DESIGNER", "/S", Entry.Location }, start.ArgumentList);
    }

    [Fact] public void MissingClientIsReportedWithItsFolder()
    {
        File.Delete(Thin);
        var error = Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Thick }, Entry, false, [], SessionClient.Thin));
        Assert.Contains("1cv8c", error.Message);
        Assert.Contains(folder, error.Message);
    }

    [Fact] public void AutoStartsTheStarterOfAClientChosenByVersion()
    {
        var starter = Path.Combine(folder, Name("1cv8s")); File.WriteAllText(starter, "fixture");
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installation = new PlatformInstallation(Thick, new Version(8, 3, 27, 2214), native);
        var byRule = Launcher.Build(new Library { PlatformRule = new() { Version = "8.3" } }, Entry, false, [installation]);
        Assert.Equal(starter, byRule.FileName);
        // A rule of a family leaves 1C the choice of the server's release; a rule of one build keeps it.
        Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location }, byRule.ArgumentList);
        var byBuild = Launcher.Build(new Library { PlatformRule = new() { Version = "8.3.27.2214" } }, Entry, false, [installation]);
        Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location, "/AppAutoCheckVersion-" }, byBuild.ArgumentList);
        // A file chosen by hand, the configurator and an explicit session client keep their executable.
        Assert.Equal(Thick, Launcher.Build(new Library { Executable = Thick }, Entry, false, []).FileName);
        Assert.Equal(Thick, Launcher.Build(new Library { PlatformRule = new() { Version = "8.3" } }, Entry, true, [installation]).FileName);
        Assert.Equal(Thin, Launcher.Build(new Library { PlatformRule = new() { Version = "8.3" } }, Entry, false, [installation], SessionClient.Thin).FileName);
    }

    [Fact] public void AutoWithoutStarterKeepsTheChosenClient()
    {
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installation = new PlatformInstallation(Thick, new Version(8, 3, 27, 2214), native);
        Assert.Equal(Thick, Launcher.Build(new Library { PlatformRule = new() { Version = "8.3" } }, Entry, false, [installation]).FileName);
    }

    [Theory]
    [InlineData("ThinClient", "1cv8c")]
    [InlineData("ThickClient", "1cv8")]
    [InlineData("Auto", "1cv8s")]
    [InlineData("", "1cv8s")]
    public void AutoFollowsTheAppSettingOfTheList(string app, string client)
    {
        File.WriteAllText(Path.Combine(folder, Name("1cv8s")), "fixture");
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installation = new PlatformInstallation(Thick, new Version(8, 3, 27, 2214), native);
        var start = Launcher.Build(new Library { PlatformRule = new() { Version = "8.3" } }, Entry with { ClientType = app }, false, [installation]);
        Assert.Equal(Path.Combine(folder, Name(client)), start.FileName);
    }

    [Fact] public void AppOfAnExternalListReachesTheEntryAndStaysInTheFile()
    {
        var list = Path.Combine(folder, "external.v8i");
        var text = "[Тонкий]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID=5f9265c4-b4a6-49a8-be3d-619db89f580f\r\nApp=ThinClient\r\n";
        File.WriteAllText(list, text, new System.Text.UTF8Encoding(true));
        var store = new V8iStore(list);
        var library = store.Load(new Library());
        Assert.Equal("ThinClient", Assert.Single(library.Bases).ClientType);
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Тонкий 2" }] });
        Assert.Contains("App=ThinClient", File.ReadAllText(list));
    }
}
