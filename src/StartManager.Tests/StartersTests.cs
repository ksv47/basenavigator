using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «1С:Стартер»: common/1cestart next to the version folders, else 1cv8s or 1cv8 of the installation; started without arguments.
public sealed class StartersTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-starter-" + Guid.NewGuid().ToString("N"));
    public StartersTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    private string File(params string[] parts)
    {
        var path = Path.Combine([folder, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "fixture"); return path;
    }
    // Windows: 1cv8\8.3.x\bin\1cv8.exe; Linux: 1cv8/x86_64/8.3.x/1cv8.
    private PlatformInstallation Installation(string root, string version, PlatformArchitecture architecture = PlatformArchitecture.X64, string client = "1cv8") =>
        new(OperatingSystem.IsWindows() ? File(root, version, "bin", Name(client)) : File(root, "x86_64", version, Name(client)), Version.Parse(version), architecture);

    [Fact] public void TheCommonStarterServesAllVersionsOfItsFolder()
    {
        var common = File("1cv8", "common", Name("1cestart"));
        var older = Installation("1cv8", "8.3.24.1691");
        var newer = Installation("1cv8", "8.3.27.1989");
        var starter = Assert.Single(Starters.Find([older, newer]));
        Assert.Equal(common, starter.Executable);
        Assert.True(starter.Common);
        Assert.Equal(new Version(8, 3, 27, 1989), starter.Newest);
        Assert.Equal("1С:Предприятие 8.3 (x64) · 8.3.27.1989", starter.ToString());
        Assert.Equal(common, Starters.For(older.Executable));
    }

    [Fact] public void WithoutTheCommonStarterTheInstallationGivesItsOwnClient()
    {
        var withStarter = Installation("old", "8.2.19.130", PlatformArchitecture.X86);
        var starterClient = File(Path.GetRelativePath(folder, Path.GetDirectoryName(withStarter.Executable)!), Name("1cv8s"));
        var plain = Installation("plain", "8.3.20.100", PlatformArchitecture.X86);
        var thin = Installation("thin", "8.3.25.1", PlatformArchitecture.X86, "1cv8c"); // the thin client alone is no starter
        var found = Starters.Find([withStarter, plain, thin]);
        Assert.Equal([plain.Executable, starterClient], found.Select(x => x.Executable));
        Assert.All(found, x => Assert.False(x.Common));
        Assert.EndsWith(" · " + Name("1cv8"), found[0].ToString());
    }

    // A client outside the layout of an installation never takes the common folder of another tree.
    [Fact] public void ACommonFolderOfAnUnrelatedTreeIsNotTaken()
    {
        File("common", Name("1cestart"));
        var client = File("tools", "bin", Name("1cv8"));
        Assert.Equal(client, Starters.For(client));
        var deep = File("x", "common", Name("1cestart"));
        var outside = File("x", "y", "8.3.27.1", "z", Name("1cv8"));
        Assert.Equal(outside, Starters.For(outside));
        Assert.NotEqual(deep, Starters.For(outside));
    }

    [Fact] public void OfEqualVersionsTheStarterIsShownAsX64InEitherOrder()
    {
        File("1cv8", "common", Name("1cestart"));
        var x86 = Installation("1cv8", "8.3.27.1", PlatformArchitecture.X86) with { };
        var x64 = x86 with { Architecture = PlatformArchitecture.X64 };
        Assert.Equal(PlatformArchitecture.X64, Assert.Single(Starters.Find([x86, x64])).Architecture);
        Assert.Equal(PlatformArchitecture.X64, Assert.Single(Starters.Find([x64, x86])).Architecture);
    }

    [Fact] public void TheMainButtonTakesTheStarterOfTheBasePlatformElseTheNewest()
    {
        File("a", "common", Name("1cestart"));
        File("b", "common", Name("1cestart"));
        var old = Installation("a", "8.3.18.1");
        var recent = Installation("b", "8.3.27.1");
        var found = Starters.Find([old, recent]);
        Assert.Equal(new Version(8, 3, 27, 1), found[0].Newest);
        Assert.Equal(new Version(8, 3, 18, 1), Starters.Choose(found, old.Executable)!.Newest);
        Assert.Equal(new Version(8, 3, 27, 1), Starters.Choose(found, null)!.Newest);
        Assert.Equal(new Version(8, 3, 27, 1), Starters.Choose(found, Path.Combine(folder, "elsewhere", Name("1cv8")))!.Newest);
        Assert.Null(Starters.Choose([], old.Executable));
    }

    [Fact] public void TheStarterGetsNoArguments()
    {
        File("1cv8", "common", Name("1cestart"));
        var starter = Starters.Find([Installation("1cv8", "8.3.27.1")])[0];
        var info = Starters.StartInfo(starter);
        Assert.Empty(info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(starter.Executable), info.WorkingDirectory);
        System.IO.File.Delete(starter.Executable);
        Assert.Throws<FileNotFoundException>(() => Starters.StartInfo(starter));
    }

    [Fact] public void TheButtonIsShownByDefault()
    {
        Assert.False(new Library().HideStarterButton);
        var store = new LibraryStore(Path.Combine(folder, "settings.json"));
        store.Save(new Library { HideStarterButton = true });
        Assert.True(store.Load().HideStarterButton);
    }
}
