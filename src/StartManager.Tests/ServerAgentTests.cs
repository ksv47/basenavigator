using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Перед запуском ИБ проверять версию Агента сервера» (CheckRAgent of the original): the local setting and the comparison.
public sealed class ServerAgentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-agent-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "Settings", "settings.json");
    private static readonly Guid Base = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static BaseEntry Entry => new() { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
    public ServerAgentTests() => Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
    public void Dispose() => Directory.Delete(root, true);

    private static ServerAgent Agent(string version, bool? running, PlatformArchitecture architecture = PlatformArchitecture.X64) =>
        new("1C:Enterprise 8 Server Agent", @"C:\1cv8\" + version + @"\bin\ragent.exe", Version.Parse(version), architecture, running);
    private static Version? Mismatch(string client, params ServerAgent[] agents) => ServerAgents.Mismatch(Version.Parse(client), PlatformArchitecture.X64, agents);

    [Theory]
    [InlineData("\"C:\\Program Files\\1cv8\\8.3.27.1989\\bin\\ragent.exe\" -debug -srvc -agent -regport 1541", "C:\\Program Files\\1cv8\\8.3.27.1989\\bin\\ragent.exe")]
    [InlineData("C:\\Program Files\\1cv8\\8.3.24.1691\\bin\\RAGENT.EXE -srvc -agent", "C:\\Program Files\\1cv8\\8.3.24.1691\\bin\\RAGENT.EXE")]
    [InlineData("  \"D:\\1C\\bin\\ragent.exe\"", "D:\\1C\\bin\\ragent.exe")]
    [InlineData("\"C:\\Program Files\\1cv8\\8.3.27.1989\\bin\\rmngr.exe\" -port 1541", null)]
    [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs", null)]
    [InlineData("\"C:\\ragent.exe.bak\\other.exe\"", null)]
    [InlineData("\"C:\\unterminated\\ragent.exe", null)]
    // Codex audit P3: a longer file name is not the agent; the whole name ends the path.
    [InlineData("C:\\1C\\bin\\ragent.exe.bak -srvc", null)]
    [InlineData("C:\\1C\\ragent.exe.old\\bin\\ragent.exe -srvc", "C:\\1C\\ragent.exe.old\\bin\\ragent.exe")]
    [InlineData("C:\\1C\\bin\\ragent.exe", "C:\\1C\\bin\\ragent.exe")]
    [InlineData("", null)] [InlineData(null, null)]
    public void ServiceCommandLineGivesTheAgent(string? imagePath, string? expected) => Assert.Equal(expected, ServerAgents.Executable(imagePath));

    [Fact] public void NoAgentMeansNoCheck() => Assert.Null(Mismatch("8.3.27.1989"));

    [Fact] public void TheFullVersionIsCompared()
    {
        Assert.Null(Mismatch("8.3.27.1989", Agent("8.3.27.1989", true)));
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.27.1900", Agent("8.3.27.1989", true)));
    }

    // Two agents of the user's computer: the running one counts; with several running, any of the client's version is enough.
    [Fact] public void RunningAgentsCountFirst()
    {
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.24.1691", Agent("8.3.24.1691", false), Agent("8.3.27.1989", true)));
        Assert.Null(Mismatch("8.3.24.1691", Agent("8.3.27.1989", true), Agent("8.3.24.1691", true)));
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.25.1", Agent("8.3.24.1691", true), Agent("8.3.27.1989", true)));
        Assert.Null(Mismatch("8.3.24.1691", Agent("8.3.27.1989", false), Agent("8.3.24.1691", true)));
    }

    // None running and none of unknown state: the stopped ones, the same way; the order they are found in does not matter.
    [Fact] public void StoppedAgentsCountLast()
    {
        Assert.Null(Mismatch("8.3.24.1691", Agent("8.3.27.1989", false), Agent("8.3.24.1691", false)));
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.25.1", Agent("8.3.27.1989", false), Agent("8.3.24.1691", false)));
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.25.1", Agent("8.3.24.1691", false), Agent("8.3.27.1989", false)));
    }

    // Codex audit P2: an agent whose state could not be read is not taken for a stopped one; with nothing confirmed running,
    // the agents of unknown state count.
    [Fact] public void AnAgentOfUnknownStateCountsAsPossiblyRunning()
    {
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.24.1691", Agent("8.3.27.1989", null), Agent("8.3.24.1691", false)));
        Assert.Null(Mismatch("8.3.24.1691", Agent("8.3.27.1989", null), Agent("8.3.24.1691", null)));
        Assert.Null(Mismatch("8.3.24.1691", Agent("8.3.24.1691", null), Agent("8.3.27.1989", false)));
    }

    // Grok audit P2: a running mismatch is not hidden by an agent of the client's version whose state could not be read.
    [Fact] public void ARunningMismatchIsNotHiddenByAnAgentOfUnknownState() =>
        Assert.Equal(Version.Parse("8.3.27.1989"), Mismatch("8.3.24.1691", Agent("8.3.27.1989", true), Agent("8.3.24.1691", null)));

    // Grok audit P2: as the original (FServerAgentsX32 / FServerAgentsX64, GetServerAgent(Bit64)), a client is compared with the
    // agents of its own bitness.
    [Fact] public void TheClientIsComparedWithTheAgentsOfItsBitness()
    {
        ServerAgent[] agents = [Agent("8.3.27.1989", true, PlatformArchitecture.X64), Agent("8.3.24.1691", true, PlatformArchitecture.X86)];
        var client = Version.Parse("8.3.24.1691");
        Assert.Equal(Version.Parse("8.3.27.1989"), ServerAgents.Mismatch(client, PlatformArchitecture.X64, agents));
        Assert.Null(ServerAgents.Mismatch(client, PlatformArchitecture.X86, agents));
        // No agent of the client's bitness in the group, or a client of unknown bitness: every agent of the group counts.
        Assert.Null(ServerAgents.Mismatch(client, PlatformArchitecture.X64, [Agent("8.3.24.1691", true, PlatformArchitecture.X86)]));
        Assert.Null(ServerAgents.Mismatch(client, PlatformArchitecture.Any, agents));
        // The state goes first: a running agent of the other bitness before a stopped one of the client's.
        Assert.Equal(Version.Parse("8.3.27.1989"), ServerAgents.Mismatch(client, PlatformArchitecture.X86,
            [Agent("8.3.27.1989", true, PlatformArchitecture.X64), Agent("8.3.24.1691", false, PlatformArchitecture.X86)]));
    }

    // Grok audit P3: the settings of the list that choose the client, compared by value.
    [Fact] public void TheClientSettingsOfTheListAreComparedByValue()
    {
        var library = new Library { PlatformRule = new() { Version = "8.3", Architecture = PlatformArchitecture.X64 }, PlatformFolders = [@"D:\1C"],
            Branches = [new() { Branch = "8.3", PlatformRule = new() { Version = "8.3.27" } }] };
        var copy = library with { PlatformRule = library.PlatformRule! with { }, PlatformFolders = [@"D:\1C"],
            Branches = [library.Branches[0] with { PlatformRule = new() { Version = "8.3.27" }, Parameters = "/L ru" }], Bases = [Entry] };
        Assert.True(PlatformCatalog.SameClientSettings(library, copy));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { PlatformRule = new() { Version = "8.3.24" } }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { PlatformRule = null }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { Executable = @"C:\1C\bin\1cv8.exe" }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { PlatformFolders = [] }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { MainVersion = "8.3" }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { Branches = [library.Branches[0] with { Executable = @"C:\1C\1cv8.exe" }] }));
        Assert.False(PlatformCatalog.SameClientSettings(library, library with { Branches = [] }));
    }

    [Fact] public void FindReadsTheServicesOfThisComputer()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Whatever is installed here: every agent found is a ragent.exe with a 1C version, and the search does not fail.
        foreach (var agent in ServerAgents.Find())
        {
            Assert.Equal("ragent.exe", Path.GetFileName(agent.Executable), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(8, agent.Version.Major);
        }
    }

    // A local setting, as «Запуск от имени администратора»: the profile keeps it, the shared list does not change.
    [Fact] public void TheSettingIsLocal()
    {
        var list = Path.Combine(root, "ibases.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Base}\r\nCustom=keep\r\n", new UTF8Encoding(false));
        var before = File.ReadAllText(list);
        var store = new V8iStore(list); var library = store.Load(new());
        Assert.False(library.Bases[0].CheckServerAgent);
        var saved = store.Save(library with { Bases = [library.Bases[0] with { CheckServerAgent = true }] });
        Assert.Equal(before, File.ReadAllText(list));
        new LibraryStore(Settings).Save(saved);
        Assert.True(new V8iStore(list).Load(new LibraryStore(Settings).Load()).Bases[0].CheckServerAgent);
        // A snapshot of the list keeps it, as the other local settings of the same base.
        Assert.True(BaseEdit.KeepLocal([Entry], [Entry with { CheckServerAgent = true }])[0].CheckServerAgent);
    }

    [Fact] public void ConcurrentEditKeepsAnUntouchedSetting()
    {
        var original = Entry;
        Assert.True(BaseEdit.OnCurrent([original with { CheckServerAgent = true }], original, original with { Comment = "x" })!.CheckServerAgent);
        Assert.False(BaseEdit.OnCurrent([original with { CheckServerAgent = true, Comment = "y" }], original with { CheckServerAgent = true },
            original with { CheckServerAgent = false })!.CheckServerAgent);
    }

    [Theory] [InlineData("1", true)] [InlineData("true", true)] [InlineData("0", false)] [InlineData("", false)]
    public void ImportReadsTheSettingOfTheOriginal(string value, bool expected)
    {
        var folder = Path.Combine(root, "SMSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "v8config.smc"), $"[{Base}]\nCheckRAgent={value}\n");
        var input = Path.Combine(folder, "input.v8i");
        File.WriteAllText(input, $"[База]\nID={Base}\nConnect=Srvr=\"server\";Ref=\"base\";\nFolder=/\n");
        var plan = LegacyImport.Preview(new Library(), input, folder, new());
        Assert.Equal(expected, Assert.Single(plan.Added).CheckServerAgent);
        Assert.DoesNotContain(plan.Notices, x => x.Contains("CheckRAgent"));
    }
}
