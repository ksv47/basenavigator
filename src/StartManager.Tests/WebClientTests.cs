using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// Bases on a web server (ws= in .v8i): reading, writing, the thin client with /WS and the browser.
public sealed class WebClientTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-web-" + Guid.NewGuid().ToString("N"));
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    private string Thick => Path.Combine(folder, Name("1cv8"));
    private string Thin => Path.Combine(folder, Name("1cv8c"));
    private const string Address = "https://accounting.demo.1c.ru/accounting/ru/";
    private static BaseEntry Web => new() { Name = "Демо", Kind = ConnectionKind.Web, Location = Address };
    public WebClientTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Thick, "fixture"); File.WriteAllText(Thin, "fixture");
    }
    public void Dispose() => Directory.Delete(folder, true);
    private V8iStore Write(string content)
    {
        var path = Path.Combine(folder, "ibases.v8i");
        File.WriteAllText(path, content, new UTF8Encoding(false)); return new(path);
    }

    [Fact] public void ListReadsWebBaseAndKeepsItsOptions()
    {
        var store = Write("[Демо]\r\nConnect=ws=\"https://accounting.demo.1c.ru/accounting/ru/\";wsn=\"user\";\r\nID=33333333-3333-3333-3333-333333333333\r\nApp=ThinClient\r\n");
        var library = store.Load(new());
        var entry = Assert.Single(library.Bases);
        Assert.Equal(ConnectionKind.Web, entry.Kind); Assert.Equal(Address, entry.Location); Assert.Equal("ThinClient", entry.ClientType);
        Assert.Empty(store.Warnings);
        store.Save(library with { Bases = [entry with { Location = "https://srv/erp" }] });
        var text = File.ReadAllText(Path.Combine(folder, "ibases.v8i"));
        Assert.Contains("Connect=ws=\"https://srv/erp\";wsn=\"user\";\r\n", text);
        Assert.Contains("App=ThinClient", text);
    }

    [Fact] public void AnotherKindDropsTheWebOptions()
    {
        var store = Write("[Демо]\r\nConnect=ws=\"https://srv/erp\";wsn=\"user\";Usr=\"admin\";\r\nID=33333333-3333-3333-3333-333333333333\r\n");
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { Kind = ConnectionKind.Server, Location = "srv\\erp" }] });
        Assert.Contains("Connect=Srvr=\"srv\";Ref=\"erp\";Usr=\"admin\";\r\n", File.ReadAllText(Path.Combine(folder, "ibases.v8i")));
    }

    [Fact] public void NewWebBaseIsWrittenAsWs()
    {
        var store = Write("");
        var library = store.Load(new());
        store.Save(library with { Bases = [Web] });
        Assert.Contains("Connect=ws=\"" + Address + "\";", File.ReadAllText(Path.Combine(folder, "ibases.v8i")));
        Assert.Equal(Web with { ClientType = "" }, store.Load(new()).Bases.Single() with { Id = Web.Id, ClientType = "" }, new EntryComparer());
    }

    // A new record keeps the version chosen for it (the search on the computer offers 8.1–8.3); 8.3 when none is known.
    [Theory] [InlineData("8.1")] [InlineData("8.2")] [InlineData("8.3.27")] [InlineData("")]
    public void NewRecordKeepsItsVersion(string version)
    {
        var store = Write("");
        store.Save(store.Load(new()) with { Bases = [new BaseEntry { Name = "B", Kind = ConnectionKind.Server, Location = @"srv\b", PlatformVersion = version }] });
        Assert.Equal(version.Length > 0 ? version : "8.3", new V8iStore(Path.Combine(folder, "ibases.v8i")).Load(new()).Bases.Single().PlatformVersion);
    }

    [Theory]
    [InlineData("ws=\"https://srv/erp\";File=\"C:\\base\";")]
    [InlineData("ws=\"https://srv/erp\";Srvr=\"srv\";Ref=\"erp\";")]
    public void MixedConnectionIsRejected(string connect)
    {
        var result = V8iImporter.Import(new Library(), "[A]\nConnect=" + connect + "\n");
        Assert.Empty(result.Library.Bases); Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("ftp://srv/erp")] [InlineData("srv/erp")] [InlineData("https://")] [InlineData("C:\\base")]
    public void OnlyHttpAddressesAreAccepted(string address) =>
        Assert.Throws<ArgumentException>(() => (Web with { Location = address }).Validate());

    [Fact] public void ConnectionStringAndClipboardUseWs()
    {
        Assert.Equal("ws=\"" + Address + "\";", BaseClipboard.ConnectionString(Web));
        Assert.Null(BaseClipboard.Text(Web, BaseClipboardKind.Path));
        Assert.True(BaseSearch.Matches(Web, "demo.1c.ru", SearchMode.Connection));
    }

    [Theory]
    [InlineData(SessionClient.Auto)] [InlineData(SessionClient.Thin)]
    public void EnterpriseStartsTheThinClientWithWs(SessionClient session)
    {
        var start = Launcher.Build(new Library { Executable = Thick }, Web, false, [], session);
        Assert.Equal(Thin, start.FileName);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, start.ArgumentList);
    }

    [Fact] public void ManualThinClientIsKept()
    {
        var start = Launcher.Build(new Library(), Web with { Executable = Thin }, false, []);
        Assert.Equal(Thin, start.FileName);
    }

    [Fact] public void MissingThinClientIsNamed()
    {
        File.Delete(Thin);
        var error = Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Thick }, Web, false, []));
        Assert.Contains("1cv8c", error.Message);
    }

    [Theory]
    [InlineData(SessionClient.Ordinary)] [InlineData(SessionClient.Thick)]
    public void ThickModesAreRefused(SessionClient session) =>
        Assert.Contains("тонким клиентом", Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Thick }, Web, false, [], session)).Message);

    [Fact] public void DesignerIsRefused()
    {
        Assert.Contains("Конфигуратор", Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Thick }, Web, true, [])).Message);
        Assert.Throws<ArgumentException>(() => Launcher.Build(Thick, Web, true));
    }

    [Fact] public void WebClientOfTheListOpensTheBrowser()
    {
        var web = Web with { ClientType = "WebClient" };
        Assert.True(Launcher.OpensInBrowser(web, false, SessionClient.Auto));
        Assert.False(Launcher.OpensInBrowser(web, false, SessionClient.Thin));
        Assert.False(Launcher.OpensInBrowser(Web, false, SessionClient.Auto));
        Assert.False(Launcher.OpensInBrowser(web, true, SessionClient.Auto));
        var browser = Launcher.Browser(web);
        Assert.True(browser.UseShellExecute); Assert.Equal(Address, browser.FileName);
        Assert.Throws<ArgumentException>(() => Launcher.Browser(new BaseEntry { Name = "F", Kind = ConnectionKind.File, Location = folder }));
    }

    [Fact] public void InformationHasNoLocalFile()
    {
        var info = BaseInfo.Read(Web, new Library { Executable = Thick }, []);
        Assert.Null(info.Size); Assert.Contains("веб-сервере", info.Note);
    }

    [Fact] public void CacheFolderFoundByWsInTheMap()
    {
        var roots = new BaseCacheRoots(Path.Combine(folder, "roaming"), Path.Combine(folder, "local"));
        Directory.CreateDirectory(roots.User);
        File.WriteAllText(Path.Combine(roots.User, "1cv8u.pfl"),
            "{{\"S\",\"ws=\"\"https://accounting.demo.1c.ru/accounting/ru\"\";\"},{\"S\",\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"}}");
        Assert.Contains("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", BaseCache.Names(Web, false, roots));
    }

    [Theory]
    [InlineData("https://SRV/erp", "https://srv/erp/", true)]
    [InlineData("HTTPS://srv:443/erp", "https://srv/erp", true)]
    [InlineData("https://srv/ERP", "https://srv/erp", false)]
    [InlineData("https://srv/erp?a=1", "https://srv/erp?a=2", false)]
    [InlineData("http://srv/erp", "https://srv/erp", false)]
    [InlineData("https://srv:8080/erp", "https://srv/erp", false)]
    public void WebAddressesDifferByPathCase(string left, string right, bool same) =>
        Assert.Equal(same, BaseEntry.SameWebAddress(left, right));

    [Fact] public void PublicationsDifferingByCaseAreBothImported()
    {
        var result = V8iImporter.Import(new Library(), "[A]\nConnect=ws=\"https://srv/ERP\";\n[B]\nConnect=ws=\"https://srv/erp\";\n[C]\nConnect=ws=\"https://SRV/erp/\";\n");
        Assert.Equal(2, result.Added); Assert.Equal(1, result.Duplicates);
    }

    [Fact] public void CacheOfAnotherPublicationIsNotTaken()
    {
        var roots = new BaseCacheRoots(Path.Combine(folder, "roaming"), Path.Combine(folder, "local"));
        Directory.CreateDirectory(roots.User);
        File.WriteAllText(Path.Combine(roots.User, "1cv8u.pfl"),
            "{{\"S\",\"ws=\"\"https://srv/erp\"\";\"},{\"S\",\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"}}");
        Assert.DoesNotContain("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", BaseCache.Names(Web with { Location = "https://srv/ERP" }, false, roots));
        Assert.Contains("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", BaseCache.Names(Web with { Location = "https://SRV/erp/" }, false, roots));
    }

    [Theory]
    [InlineData("File=\"C:\\old\";wsCustom=\"keep\";", ConnectionKind.File, "C:\\new", "File=\"C:\\new\";wsCustom=\"keep\";")]
    [InlineData("Srvr=\"a\";Ref=\"b\";wsCustom=\"keep\";", ConnectionKind.Server, "a\\c", "Srvr=\"a\";Ref=\"c\";wsCustom=\"keep\";")]
    public void UnknownWsKeysOfOtherKindsStay(string before, ConnectionKind kind, string location, string expected)
    {
        var store = Write("[A]\r\nConnect=" + before + "\r\nID=33333333-3333-3333-3333-333333333333\r\n");
        var library = store.Load(new());
        Assert.Equal(kind, library.Bases[0].Kind);
        store.Save(library with { Bases = [library.Bases[0] with { Location = location }] });
        Assert.Contains("Connect=" + expected + "\r\n", File.ReadAllText(Path.Combine(folder, "ibases.v8i")));
    }

    [Fact] public void VersionRuleWinsOverAnOldManualClient()
    {
        var old = Path.Combine(folder, "old"); Directory.CreateDirectory(old);
        var oldThin = Path.Combine(old, Name("1cv8c")); File.WriteAllText(oldThin, "fixture");
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installation = new PlatformInstallation(Thick, new Version(8, 3, 27, 2214), native);
        var entry = Web with { Executable = oldThin, PlatformRule = new() { Version = "8.3.27" } };
        var start = Launcher.Build(new Library(), entry, false, [installation]);
        Assert.Equal(Thin, start.FileName);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/AppAutoCheckVersion-" }, start.ArgumentList);
    }

    [Fact] public void ElevationStartsTheThinClientInsteadOfTheBrowser() =>
        Assert.False(Launcher.OpensInBrowser(Web with { ClientType = "WebClient" }, false, SessionClient.Auto, elevated: true));

    // WSA= of the list: «Способ аутентификации» of the page «Подключение» (1 asks, 0 chooses automatically).
    [Theory] [InlineData("WSA=1\r\n", true)] [InlineData("WSA=0\r\n", false)] [InlineData("", null)] [InlineData("WSA=2\r\n", null)] [InlineData("WSA=\r\n", null)]
    public void ListReadsTheWebServerAuthentication(string line, bool? expected)
    {
        var store = Write("[Демо]\r\nConnect=ws=\"https://srv/erp\";\r\nID=33333333-3333-3333-3333-333333333333\r\n" + line);
        Assert.Equal(expected, store.Load(new()).Bases.Single().WebServerAskCredentials);
    }

    [Fact] public void WebServerAuthenticationIsWrittenOnlyWhenChanged()
    {
        var path = Path.Combine(folder, "ibases.v8i");
        var store = Write("[Демо]\r\nConnect=ws=\"https://srv/erp\";\r\nID=33333333-3333-3333-3333-333333333333\r\nCustom=keep\r\n");
        var library = store.Load(new());
        // Saved unchanged (null): the record has no new line.
        store.Save(library with { Bases = [library.Bases[0] with { Comment = "x" }] });
        Assert.DoesNotContain("WSA", File.ReadAllText(path));
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { WebServerAskCredentials = false }] });
        Assert.Contains("WSA=0\r\n", File.ReadAllText(path));
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { WebServerAskCredentials = true }] });
        var text = File.ReadAllText(path);
        Assert.Contains("WSA=1\r\n", text); Assert.DoesNotContain("WSA=0", text); Assert.Contains("Custom=keep", text);
        // A line the program does not change stays as it is.
        Assert.True(new V8iStore(path).Load(new()).Bases[0].WebServerAskCredentials);
        // A new record gets the line only when it was chosen.
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0], Web with { WebServerAskCredentials = false }, Web with { Name = "Второй", Location = "https://srv/other" }] });
        var saved = new V8iStore(path).Load(new()).Bases;
        Assert.Equal([true, false, null], saved.Select(x => x.WebServerAskCredentials));
    }

    [Fact] public void ExportedListKeepsTheWebServerAuthentication() =>
        Assert.Contains("WSA=1", ListMaintenance.ToV8i(new Library { Bases = [Web with { WebServerAskCredentials = true }] }));

    [Fact] public void ThinClientAsksTheWebServerForTheNameAndPassword()
    {
        var library = new Library { Executable = Thick };
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/WSA-" }, Launcher.Build(library, Web with { WebServerAskCredentials = true }, false, []).ArgumentList);
        // Automatic choice and a record without the line pass nothing.
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Launcher.Build(library, Web with { WebServerAskCredentials = false }, false, []).ArgumentList);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Launcher.Build(library, Web, false, []).ArgumentList);
        // Other kinds of base never get /WSA-.
        var local = new BaseEntry { Name = "Л", Kind = ConnectionKind.Server, Location = "srv\\b", WebServerAskCredentials = true };
        Assert.DoesNotContain("/WSA-", Launcher.Build(library, local, false, []).ArgumentList);
        // Together with the list mode of the other page and a parameter.
        var both = Launcher.Build(library, Web with { WebServerAskCredentials = true, AskCredentials = true, AdditionalParameters = "/Debug" }, false, []).ArgumentList;
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/WA-", "/WSA-", "/Debug" }, both);
    }

    // Codex audit P2.1/P2.2: «Запустить с аутентификацией» keeps the web server setting, with or without the page «Аутентификация»,
    // and /WSA- is not taken for a key with a value, so a /WA of the parameters is still replaced.
    [Theory] [InlineData(null, null)] [InlineData("/WA+", null)] [InlineData("/WA+", "user")] [InlineData(null, "user")]
    public void StartWithLoginKeepsTheWebServerSetting(string? parameters, string? user)
    {
        var entry = Web with { WebServerAskCredentials = true, AdditionalParameters = parameters,
            Authentication = user is null ? null : new AuthenticationSettings { EnterpriseLogin = true, EnterpriseUser = user } };
        var start = Launcher.Build(new Library { Executable = Thick }, entry, false, [], options: LaunchOptions.Login);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/WSA-", "/WA-" }, start.ArgumentList);
    }

    // Coverage gap of the Codex re-check: /WSA of the parameters with «Запустить с аутентификацией».
    [Theory] [InlineData("/WSA+")] [InlineData("/WSA-")] [InlineData("/WSA")]
    public void StartWithLoginTreatsWsaOfTheParametersLikeTheNormalStart(string parameter)
    {
        var library = new Library { Executable = Thick };
        // The page already passes /WSA-: the same key in the parameters is refused, as at the normal start.
        var both = Web with { WebServerAskCredentials = true, AdditionalParameters = parameter };
        Assert.Contains("/WSA", Assert.Throws<ArgumentException>(() => Launcher.Build(library, both, false, [], options: LaunchOptions.Login)).Message);
        // Without the setting the parameter stays; /WA- is added after it and is not taken for its value.
        var own = Web with { AdditionalParameters = parameter + " /WA+" };
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, parameter, "/WA-" }, Launcher.Build(library, own, false, [], options: LaunchOptions.Login).ArgumentList);
    }

    // Coverage gap of the Codex re-check: WSA= edited in a dialog opened before the list was read again.
    [Fact] public void ConcurrentWebServerAuthenticationEditIsMergedPerField()
    {
        var original = Web;                                              // WSA= absent when the dialog was opened
        BaseEntry[] now(bool? value) => [original with { WebServerAskCredentials = value }];
        // The user did not touch it: what another program wrote stays.
        Assert.Equal(false, BaseEdit.OnCurrent(now(false), original, original with { Comment = "x" })!.WebServerAskCredentials);
        // The user chose: the choice wins, also over a newer value.
        Assert.Equal(true, BaseEdit.OnCurrent(now(false), original, original with { WebServerAskCredentials = true })!.WebServerAskCredentials);
        Assert.Equal(false, BaseEdit.OnCurrent(now(null), original, original with { WebServerAskCredentials = false })!.WebServerAskCredentials);
        // The other fields are merged independently of it.
        var merged = BaseEdit.OnCurrent([original with { WebServerAskCredentials = true, Name = "Новое" }], original, original with { Group = "G" })!;
        Assert.Equal((true, "Новое", "G"), (merged.WebServerAskCredentials, merged.Name, merged.Group));
    }

    [Theory] [InlineData("/WSA+")] [InlineData("/WSA-")] [InlineData("/WSA")]
    public void WsaInTheParametersAndOnThePageIsRefused(string parameter)
    {
        var entry = Web with { WebServerAskCredentials = true, AdditionalParameters = parameter };
        var message = Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Thick }, entry, false, [])).Message;
        Assert.Contains("/WSA", message); Assert.Contains("Подключение", message);
        // Without the setting the parameter is passed as the user wrote it.
        Assert.Contains(parameter, Launcher.Build(new Library { Executable = Thick }, Web with { AdditionalParameters = parameter }, false, []).ArgumentList);
    }

    private sealed class EntryComparer : IEqualityComparer<BaseEntry>
    {
        public bool Equals(BaseEntry? x, BaseEntry? y) => x!.Kind == y!.Kind && x.Location == y.Location && x.Name == y.Name;
        public int GetHashCode(BaseEntry obj) => 0;
    }
}
