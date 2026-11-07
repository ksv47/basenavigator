using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Режим» of the base properties, as the original (decision of the user 12.10.2026: the whole original): App= of the list,
/// «Толстый клиент (ОП)» and «(УП)» told apart locally and started with their run mode.</summary>
public sealed class ClientModeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-client-mode-" + Guid.NewGuid().ToString("N"));
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    private string Thick => Path.Combine(folder, Name("1cv8"));
    private static readonly Guid Id = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static BaseEntry Entry => new() { Id = Id, Name = "Test", Kind = ConnectionKind.Server, Location = "server\\base" };
    public ClientModeTests() { Directory.CreateDirectory(folder); File.WriteAllText(Thick, "fixture"); }
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData("ThickClient", false, SessionClient.Auto, "/RunModeOrdinaryApplication")]
    [InlineData("thickclient", true, SessionClient.Auto, "/RunModeManagedApplication")]
    [InlineData("ThinClient", false, SessionClient.Auto, null)]
    [InlineData("", true, SessionClient.Auto, null)]
    // The current session goes over the base's mode.
    [InlineData("ThickClient", true, SessionClient.Ordinary, "/RunModeOrdinaryApplication")]
    public void ThickClientOfTheBaseGivesItsRunMode(string clientType, bool managed, SessionClient session, string? mode)
    {
        var start = Launcher.Build(new Library { Executable = Thick }, Entry with { ClientType = clientType, ManagedApplication = managed }, false, [], session);
        var expected = new List<string> { "ENTERPRISE", "/S", Entry.Location };
        if (mode is not null) expected.Add(mode);
        Assert.Equal(expected, start.ArgumentList);
        // The configurator has no run mode.
        Assert.Equal(new[] { "DESIGNER", "/S", Entry.Location },
            Launcher.Build(new Library { Executable = Thick }, Entry with { ClientType = clientType, ManagedApplication = managed }, true, [], session).ArgumentList);
    }

    [Fact]
    public void RunModeOfTheParametersWins()
    {
        // Codex audit: no second, contradictory key when the parameters already choose the run mode.
        var entry = Entry with { ClientType = "ThickClient", AdditionalParameters = "/RunModeManagedApplication" };
        var start = Launcher.Build(new Library { Executable = Thick }, entry, false, []);
        Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location, "/RunModeManagedApplication" }, start.ArgumentList);
    }

    [Fact]
    public void ParameterValueIsNoRunMode()
    {
        // Codex re-check: a value that only begins like the key does not suppress the base's mode.
        var entry = Entry with { ClientType = "ThickClient", AdditionalParameters = "/Out /RunModeLogs/output.txt" };
        Assert.Contains("/RunModeOrdinaryApplication", Launcher.Build(new Library { Executable = Thick }, entry, false, []).ArgumentList);
        // Second re-check: the key itself as the value of /C is a value.
        var value = Entry with { ClientType = "ThickClient", AdditionalParameters = "/C \"/RunModeManagedApplication\"" };
        Assert.Equal("/RunModeOrdinaryApplication", Launcher.Build(new Library { Executable = Thick }, value, false, []).ArgumentList[^1]);
    }

    [Fact]
    public void ManagedOnlyChangeKeepsTheirOtherChanges()
    {
        // Codex re-check: «(УП)» chosen here and another key of the record changed there — no conflict, theirs stays.
        var list = Path.Combine(folder, "other.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Id}\r\nApp=ThickClient\r\nCustom=a\r\n", new UTF8Encoding(false));
        var store = new V8iStore(list); var library = store.Load(new());
        File.WriteAllText(list, File.ReadAllText(list).Replace("Custom=a", "Custom=b"), new UTF8Encoding(false));
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [library.Bases[0] with { ManagedApplication = true }] });
        Assert.Contains("Custom=b", File.ReadAllText(list));
        Assert.True(Assert.Single(saved.Bases).ManagedApplication);
        // Second re-check: App= changed there only by case is the same mode — no conflict, their spelling stays.
        var cased = new V8iStore(list); var casedLibrary = cased.Load(new());
        File.WriteAllText(list, File.ReadAllText(list).Replace("App=ThickClient", "App=thickclient"), new UTF8Encoding(false));
        // Loaded without the profile: «(ОП)»; «(УП)» chosen here is a change of the mode (Codex third re-check: the test enters the rule).
        Assert.False(casedLibrary.Bases[0].ManagedApplication);
        var (_, casedSaved) = cased.SaveOverExternalChange(casedLibrary with { Bases = [casedLibrary.Bases[0] with { ManagedApplication = true }] });
        Assert.True(Assert.Single(casedSaved.Bases).ManagedApplication);
        Assert.Contains("App=thickclient", File.ReadAllText(list));
    }

    [Fact]
    public void ModeIsOneChoiceInTheMerge()
    {
        // Codex audit: «(УП)» chosen while another program set App=ThinClient — the edit is thick managed, not a mix.
        var shown = Entry with { ClientType = "ThickClient" };
        var now = shown with { ClientType = "ThinClient" };
        var saved = BaseEdit.OnCurrent([now], shown, shown with { ManagedApplication = true })!;
        Assert.Equal(("ThickClient", true), (saved.ClientType, saved.ManagedApplication));
        // Untouched: both as they are now.
        var kept = BaseEdit.OnCurrent([now with { ManagedApplication = true }], shown, shown with { Comment = "x" })!;
        Assert.Equal(("ThinClient", true), (kept.ClientType, kept.ManagedApplication));
    }

    [Fact]
    public void ManagedOnlyChangeIsAChangeOfTheRecord()
    {
        // The list changed App= meanwhile; «(УП)» chosen over it is refused as a conflict instead of a silent mix.
        var list = Path.Combine(folder, "conflict.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Id}\r\nApp=ThickClient\r\n", new UTF8Encoding(false));
        var store = new V8iStore(list); var library = store.Load(new());
        File.WriteAllText(list, File.ReadAllText(list).Replace("App=ThickClient", "App=ThinClient"), new UTF8Encoding(false));
        var mine = library with { Bases = [library.Bases[0] with { ManagedApplication = true }] };
        Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(mine));
        Assert.Contains("App=ThinClient", File.ReadAllText(list));
    }

    [Fact]
    public void ModeIsWrittenToTheListOnlyWhenChanged()
    {
        var list = Path.Combine(folder, "ibases.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Id}\r\nApp=thickclient\r\n", new UTF8Encoding(false));
        var store = new V8iStore(list); var library = store.Load(new());
        var entry = Assert.Single(library.Bases);
        Assert.Equal("thickclient", entry.ClientType);
        // «(УП)»: the list keeps App= as written, the choice stays in the profile.
        var saved = store.Save(library with { Bases = [entry with { ManagedApplication = true, Comment = "x" }] });
        Assert.Contains("App=thickclient", File.ReadAllText(list));
        var profile = Path.Combine(folder, "settings.json");
        new LibraryStore(profile).Save(saved);
        var reloaded = Assert.Single(new V8iStore(list).Load(new LibraryStore(profile).Load()).Bases);
        Assert.True(reloaded.ManagedApplication);
        // Another mode goes to the list; «Автоматический» is written as Auto.
        var thin = new V8iStore(list); var thinLibrary = thin.Load(new LibraryStore(profile).Load());
        thin.Save(thinLibrary with { Bases = [thinLibrary.Bases[0] with { ClientType = "ThinClient" }] });
        Assert.Contains("App=ThinClient", File.ReadAllText(list));
        var auto = new V8iStore(list); var autoLibrary = auto.Load(new());
        auto.Save(autoLibrary with { Bases = [autoLibrary.Bases[0] with { ClientType = "" }] });
        Assert.Contains("App=Auto", File.ReadAllText(list));
    }

    [Fact]
    public void ProfileWithoutListKeepsTheMode()
    {
        var profile = Path.Combine(folder, "local.json");
        new LibraryStore(profile).Save(new Library { Bases = [Entry with { ClientType = "ThickClient", ManagedApplication = true }] });
        var loaded = Assert.Single(new LibraryStore(profile).Load().Bases);
        Assert.Equal(("ThickClient", true), (loaded.ClientType, loaded.ManagedApplication));
    }

    [Fact]
    public void EditOverAChangeOfTheListKeepsTheirMode()
    {
        var shown = Entry with { ClientType = "ThinClient" };
        var now = shown with { ClientType = "WebClient" };
        Assert.Equal("WebClient", BaseEdit.OnCurrent([now], shown, shown with { Comment = "мой" })!.ClientType);
        Assert.Equal("ThickClient", BaseEdit.OnCurrent([now], shown, shown with { ClientType = "ThickClient" })!.ClientType);
    }
}
