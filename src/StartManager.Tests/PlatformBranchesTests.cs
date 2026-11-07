using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Версии платформы» (основная версия, единый каталог) and «Настройки платформы» by branch of the original.
[Collection("1CEStart default version")]
public sealed class PlatformBranchesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-branches-" + Guid.NewGuid().ToString("N"));
    private readonly Func<PlatformRule?> previousDefault = PlatformCatalog.StartDefaultVersion;
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    public PlatformBranchesTests() { Directory.CreateDirectory(root); PlatformCatalog.StartDefaultVersion = () => null; }
    public void Dispose() { PlatformCatalog.StartDefaultVersion = previousDefault; Directory.Delete(root, true); }

    private PlatformInstallation Installation(string version)
    {
        var path = Path.Combine(root, version, "bin", Name("1cv8"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        return new(path, Version.Parse(version), PlatformCatalog.NativeArchitecture);
    }
    private static BaseEntry Base(string version = "") => new() { Name = "База", Kind = ConnectionKind.Server, Location = "srv\\base", PlatformVersion = version };

    [Fact] public void TheBranchIsTheFamilyOfTheRecordElseTheMainVersionElse1CEStart()
    {
        var library = new Library();
        Assert.Equal("8.3", PlatformBranches.Of(library, Base("8.3.27.1989")));
        Assert.Equal("8.2", PlatformBranches.Of(library, Base("8.2")));
        Assert.Null(PlatformBranches.Of(library, Base()));
        Assert.Equal("8.5", PlatformBranches.Of(library with { MainVersion = "8.5" }, Base()));
        PlatformCatalog.StartDefaultVersion = () => new PlatformRule { Version = "8.3" };
        Assert.Equal("8.3", PlatformBranches.Of(library, Base()));
    }

    [Fact] public void TheBaseOwnChoiceWinsTheBranchFileAppliesAlwaysTheBranchVersionOnlyWithoutAFullRelease()
    {
        var rule = new PlatformRule { Version = "8.3.24.1691" };
        var byVersion = new Library { Branches = [new BranchSettings { Branch = "8.3", PlatformRule = rule }] };
        Assert.Equal(rule, PlatformBranches.WithPlatform(byVersion, Base("8.3")).PlatformRule);
        Assert.Equal(rule, PlatformBranches.WithPlatform(byVersion with { MainVersion = "8.3" }, Base()).PlatformRule);
        Assert.Null(PlatformBranches.WithPlatform(byVersion, Base("8.3.27.1989")).PlatformRule); // the full release of the record stays
        Assert.Null(PlatformBranches.WithPlatform(byVersion, Base("8.2")).PlatformRule);         // another branch
        var own = new PlatformRule { Version = "8.3.27" };
        Assert.Equal(own, PlatformBranches.WithPlatform(byVersion, Base("8.3") with { PlatformRule = own }).PlatformRule);
        var file = Path.Combine(root, Name("1cv8"));
        var byFile = new Library { Branches = [new BranchSettings { Branch = "8.3", Executable = file }] };
        Assert.Equal(file, PlatformBranches.WithPlatform(byFile, Base("8.3.27.1989")).Executable);
        Assert.Equal("C:\\own", PlatformBranches.WithPlatform(byFile, Base("8.3") with { Executable = "C:\\own" }).Executable);
    }

    [Fact] public void TheResolvedClientFollowsTheBranchAndTheMainVersion()
    {
        var old = Installation("8.3.24.1691"); var recent = Installation("8.3.27.1989"); var branch82 = Installation("8.2.19.130");
        IReadOnlyList<PlatformInstallation> installed = [old, recent, branch82];
        var library = new Library { Branches = [new BranchSettings { Branch = "8.3", PlatformRule = new PlatformRule { Version = "8.3.24" } }] };
        Assert.Equal(old.Executable, PlatformCatalog.Resolve(library, Base("8.3"), installed));
        Assert.Equal(recent.Executable, PlatformCatalog.Resolve(library, Base("8.3.27.1989"), installed));
        // A record without a version: the main version, then its branch.
        Assert.Equal(branch82.Executable, PlatformCatalog.Resolve(new Library { MainVersion = "8.2" }, Base(), installed));
        Assert.Equal(old.Executable, PlatformCatalog.Resolve(library with { MainVersion = "8.3" }, Base(), installed));
    }

    [Fact] public void BranchParametersGoBeforeTheBaseOwn()
    {
        var client = Installation("8.3.27.1989");
        var library = new Library { Branches = [new BranchSettings { Branch = "8.3", Executable = client.Executable, Parameters = "/DisableStartupMessages" }] };
        var start = Launcher.Build(library, Base("8.3") with { AdditionalParameters = "/C \"текст\"" }, false);
        Assert.Equal(client.Executable, start.FileName);
        Assert.Equal(["ENTERPRISE", "/S", "srv\\base", "/DisableStartupMessages", "/C", "текст"], start.ArgumentList.Take(6));
        // Parameters inherited from the list stay after the branch's.
        var inherited = Launcher.Build(library, Base("8.3") with { ListParameters = "/Debug" }, false);
        Assert.Equal(["/DisableStartupMessages", "/Debug"], inherited.ArgumentList.Skip(3).Take(2));
    }

    // A base on a web server with App=WebClient opens in the browser; branch parameters make it a start of the client, as own ones do.
    [Fact] public void BranchParametersKeepAWebBaseOutOfTheBrowser()
    {
        var web = new BaseEntry { Name = "Веб", Kind = ConnectionKind.Web, Location = "https://srv/base", PlatformVersion = "8.3", ClientType = "WebClient" };
        Assert.True(Launcher.OpensInBrowser(PlatformBranches.ForStart(new Library(), web), false, SessionClient.Auto));
        var library = new Library { Branches = [new BranchSettings { Branch = "8.3", Parameters = "/Debug" }] };
        Assert.False(Launcher.OpensInBrowser(PlatformBranches.ForStart(library, web), false, SessionClient.Auto));
    }

    // The column «Платформа» shows the version the start takes: the branch's, not the general rule.
    [Fact] public void TheInformationColumnShowsTheBranchVersion()
    {
        var branch = Installation("8.3.24.1691"); Installation("8.5.1.100");
        IReadOnlyList<PlatformInstallation> installed = [branch, new(Path.Combine(root, "8.5.1.100", "bin", Name("1cv8")), Version.Parse("8.5.1.100"), PlatformCatalog.NativeArchitecture)];
        var library = new Library { PlatformRule = new PlatformRule { Version = "8.5" },
            Branches = [new BranchSettings { Branch = "8.3", PlatformRule = new PlatformRule { Version = "8.3.24" } }] };
        var info = BaseInfo.Read(Base("8.3"), library, installed);
        Assert.Equal("8.3.24", info.Platform);
        Assert.Contains(branch.Executable, info.Note);
    }

    [Fact] public void SettingsAreValidatedAndKept()
    {
        Assert.Throws<ArgumentException>(() => new BranchSettings { Branch = "8.5", PlatformRule = new PlatformRule { Version = "8.3.27" } }.Validate());
        Assert.Throws<InvalidDataException>(() => new BranchSettings { Branch = "8.4" }.Validate());
        Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { MainVersion = "8.1" }));
        Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { Branches = [new() { Branch = "8.3" }, new() { Branch = "8.3" }] }));
        var store = new LibraryStore(Path.Combine(root, "settings.json"));
        var saved = new Library { MainVersion = "8.5", BasesFolder = Path.Combine(root, "bases"),
            Branches = [new BranchSettings { Branch = "8.5", PlatformRule = new PlatformRule { Version = "8.5.1" }, Parameters = "/Debug" }] };
        store.Save(saved);
        var loaded = store.Load();
        Assert.Equal(("8.5", saved.BasesFolder), (loaded.MainVersion, loaded.BasesFolder));
        var branch = Assert.Single(loaded.Branches);
        Assert.Equal(("8.5", "8.5.1", "/Debug"), (branch.Branch, branch.PlatformRule!.Version, branch.Parameters));
        // An older profile has none of them.
        Assert.Equal(("", "", 0), (new Library().MainVersion, new Library().BasesFolder, new Library().Branches.Count));
    }
}
