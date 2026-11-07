using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Скорость соединения» and «Использование Proxy» of the page «Подключение» for a base on a web server: the keys of the list
// (ClientConnectionSpeed=, UseProxy=, ProxyServer=, ProxyPort=, ProxyUser=, ProxyPassword=) and the keys of the thin client.
public sealed class WebProxyTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-proxy-" + Guid.NewGuid().ToString("N"));
    private static string Name(string client) => OperatingSystem.IsWindows() ? client + ".exe" : client;
    private string Thick => Path.Combine(folder, Name("1cv8"));
    private const string Address = "https://srv/erp";
    private const string Record = "[Демо]\r\nConnect=ws=\"https://srv/erp\";\r\nID=33333333-3333-3333-3333-333333333333\r\n";
    private static BaseEntry Web => new() { Name = "Демо", Kind = ConnectionKind.Web, Location = Address };
    private static BaseEntry Manual => Web with { UseProxy = ProxyMode.Manual, ProxyServer = "proxy.local", ProxyPort = "3128" };
    private string ListPath => Path.Combine(folder, "ibases.v8i");

    public WebProxyTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Thick, "fixture"); File.WriteAllText(Path.Combine(folder, Name("1cv8c")), "fixture");
    }
    public void Dispose() => Directory.Delete(folder, true);
    private V8iStore Write(string content) { File.WriteAllText(ListPath, content, new UTF8Encoding(false)); return new(ListPath); }
    private IList<string> Start(BaseEntry entry, PasswordVault? vault = null, LaunchOptions? options = null) =>
        Launcher.Build(new Library { Executable = Thick }, entry, false, [], options: options, passwords: vault?.For(new Library())).ArgumentList;

    [Theory]
    [InlineData("ClientConnectionSpeed=Normal\r\n", "Normal")] [InlineData("ClientConnectionSpeed=low\r\n", "Low")]
    [InlineData("ClientConnectionSpeed=Auto\r\n", "")] [InlineData("ClientConnectionSpeed=proxy.local\r\n", null)] [InlineData("", "")]
    public void ListReadsTheConnectionSpeed(string line, string? expected) =>
        Assert.Equal(expected, Write(Record + line).Load(new()).Bases.Single().ConnectionSpeed);

    [Theory]
    [InlineData("UseProxy=0\r\n", ProxyMode.None)] [InlineData("UseProxy=1\r\n", ProxyMode.Auto)] [InlineData("UseProxy=2\r\n", ProxyMode.Manual)]
    [InlineData("UseProxy=3\r\n", null)] [InlineData("UseProxy=\r\n", null)] [InlineData("", null)]
    public void ListReadsTheProxyMode(string line, ProxyMode? expected) =>
        Assert.Equal(expected, Write(Record + line).Load(new()).Bases.Single().UseProxy);

    [Fact] public void ListReadsTheProxySettings()
    {
        var entry = Write(Record + "UseProxy=2\r\nProxyServer=proxy.local\r\nProxyPort=3128\r\nProxyUser=ivanov\r\nProxyPassword=secret\r\n").Load(new()).Bases.Single();
        Assert.Equal(("proxy.local", "3128", "ivanov", "secret"), (entry.ProxyServer, entry.ProxyPort, entry.ProxyUser, entry.ListProxyPassword));
    }

    // A value 1C could not take does not hide the base: it is not used, the line stays.
    [Fact] public void TabInAProxyValueKeepsTheBase()
    {
        var store = Write(Record + "ProxyServer=proxy\tlocal\r\n");
        var library = store.Load(new());
        Assert.Equal("", Assert.Single(library.Bases).ProxyServer); Assert.Empty(store.Warnings);
        store.Save(library with { Bases = [library.Bases[0] with { Comment = "x" }] });
        Assert.Contains("ProxyServer=proxy\tlocal\r\n", File.ReadAllText(ListPath));
    }

    [Fact] public void UntouchedRecordGetsNoNewLines()
    {
        var store = Write(Record + "Custom=keep\r\n");
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Новое" }] });
        var text = File.ReadAllText(ListPath);
        foreach (var key in new[] { "ClientConnectionSpeed", "UseProxy", "ProxyServer", "ProxyPort", "ProxyUser", "ProxyPassword" }) Assert.DoesNotContain(key, text);
        Assert.Contains("Custom=keep", text);
    }

    [Fact] public void ConnectionSpeedIsWrittenAndRemovedForSelectOnStart()
    {
        var store = Write(Record);
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { ConnectionSpeed = "Low" }] });
        Assert.Contains("ClientConnectionSpeed=Low\r\n", File.ReadAllText(ListPath));
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { ConnectionSpeed = "Normal" }] });
        Assert.Single(File.ReadAllLines(ListPath), x => x.StartsWith("ClientConnectionSpeed=", StringComparison.Ordinal));
        Assert.Equal("Normal", new V8iStore(ListPath).Load(new()).Bases[0].ConnectionSpeed);
        // «Выбирать при запуске», as the original: no line.
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { ConnectionSpeed = "" }] });
        Assert.DoesNotContain("ClientConnectionSpeed", File.ReadAllText(ListPath));
    }

    // The original wrote the proxy address over ClientConnectionSpeed= (_Unit205.pas:26583); here it goes to ProxyServer=.
    [Fact] public void ProxySettingsAreWrittenToTheirOwnLines()
    {
        var store = Write(Record + "ClientConnectionSpeed=Normal\r\nProxyPassword=old\r\n");
        var library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { UseProxy = ProxyMode.Manual, ProxyServer = "proxy.local", ProxyPort = "3128", ProxyUser = "ivanov" }] });
        var text = File.ReadAllText(ListPath);
        foreach (var line in new[] { "ClientConnectionSpeed=Normal\r\n", "UseProxy=2\r\n", "ProxyServer=proxy.local\r\n", "ProxyPort=3128\r\n", "ProxyUser=ivanov\r\n", "ProxyPassword=old\r\n" })
            Assert.Contains(line, text);
        var read = new V8iStore(ListPath).Load(new()).Bases.Single();
        Assert.Equal((ProxyMode.Manual, "proxy.local", "3128", "ivanov"), (read.UseProxy!.Value, read.ProxyServer, read.ProxyPort, read.ProxyUser));
        // Another way keeps the address; an emptied field removes its line.
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { UseProxy = ProxyMode.None, ProxyUser = "" }] });
        text = File.ReadAllText(ListPath);
        Assert.Contains("UseProxy=0\r\n", text); Assert.Contains("ProxyServer=proxy.local\r\n", text); Assert.DoesNotContain("ProxyUser", text);
        Assert.Single(File.ReadAllLines(ListPath), x => x.StartsWith("UseProxy=", StringComparison.Ordinal));
    }

    [Fact] public void NewRecordGetsOnlyTheChosenLines()
    {
        var store = Write("");
        var library = store.Load(new());
        store.Save(library with { Bases = [Web, Manual with { Name = "Второй", Location = "https://srv/other", ConnectionSpeed = "Low" }] });
        var saved = new V8iStore(ListPath).Load(new()).Bases;
        Assert.Equal(new ProxyMode?[] { null, ProxyMode.Manual }, saved.Select(x => x.UseProxy));
        Assert.Equal(new[] { "", "Low" }, saved.Select(x => x.ConnectionSpeed));
        Assert.Equal(new[] { "", "3128" }, saved.Select(x => x.ProxyPort));
    }

    [Fact] public void ExportedListKeepsTheSettingsButNotThePassword()
    {
        var text = ListMaintenance.ToV8i(new Library { Bases = [Manual with { ConnectionSpeed = "Low", ProxyUser = "ivanov", ListProxyPassword = "secret" }] });
        foreach (var line in new[] { "ClientConnectionSpeed=Low", "UseProxy=2", "ProxyServer=proxy.local", "ProxyPort=3128", "ProxyUser=ivanov" }) Assert.Contains(line, text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact] public void ThinClientGetsTheSpeedAndTheProxy()
    {
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Start(Web));
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/O", "Low" }, Start(Web with { ConnectionSpeed = "Low" }));
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/NoProxy" }, Start(Web with { UseProxy = ProxyMode.None }));
        // «Автоматическое определение» passes nothing: 1C finds the proxy itself.
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Start(Web with { UseProxy = ProxyMode.Auto }));
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/O", "Normal", "/Proxy", "-PSrv", "proxy.local", "-PPort", "3128" },
            Start(Manual with { ConnectionSpeed = "Normal" }));
        // The settings of another way are kept but not passed.
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Start(Manual with { UseProxy = ProxyMode.Auto, ProxyUser = "ivanov" }));
    }

    [Fact] public void ProxyPasswordComesFromTheVaultBeforeTheList()
    {
        var vault = new PasswordVault(Path.Combine(folder, "settings.json"));
        var entry = Manual with { ProxyUser = "ivanov", ListProxyPassword = "from-list" };
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/Proxy", "-PSrv", "proxy.local", "-PPort", "3128", "-PUser", "ivanov", "-PPwd", "from-list" }, Start(entry, vault));
        vault.Write(ActivityStore.Source(vault.SettingsPath, ""), entry.Id, new Dictionary<PasswordRole, string?> { [PasswordRole.Proxy] = ProxyPassword.Pack(entry, "saved") });
        Assert.Equal("saved", Start(entry, vault).Last());
        // A password without a user is not passed; other roles are not the proxy's.
        Assert.DoesNotContain("-PPwd", Start(entry with { ProxyUser = "" }, vault));
        vault.Write(ActivityStore.Source(vault.SettingsPath, ""), entry.Id, new Dictionary<PasswordRole, string?> { [PasswordRole.Proxy] = null, [PasswordRole.Enterprise] = "enterprise" });
        Assert.Equal("from-list", Start(entry, vault).Last());
        // Removing the base forgets the proxy password too.
        vault.Write(ActivityStore.Source(vault.SettingsPath, ""), entry.Id, new Dictionary<PasswordRole, string?> { [PasswordRole.Proxy] = "saved" });
        vault.Remove(ActivityStore.Source(vault.SettingsPath, ""), entry.Id);
        Assert.Empty(vault.Read(new Library(), ActivityStore.Source(vault.SettingsPath, ""), entry.Id).Passwords);
    }

    [Theory]
    [InlineData("", "3128")] [InlineData("proxy.local", "")] [InlineData("proxy.local", "0")] [InlineData("proxy.local", "65536")]
    [InlineData("proxy.local", "31 28")] [InlineData("proxy.local", "-1")] [InlineData("proxy local", "3128")] [InlineData("proxy\"local", "3128")]
    public void ExplicitProxyNeedsAnAddressAndAPort(string server, string port)
    {
        var message = Assert.Throws<ArgumentException>(() => Start(Manual with { ProxyServer = server, ProxyPort = port })).Message;
        Assert.Contains("Прокси", message, StringComparison.OrdinalIgnoreCase);
    }

    // Not a web base: the keys of the list are kept but not passed; the configurator is refused anyway.
    [Fact] public void OtherKindsGetNoWebKeys()
    {
        var server = Manual with { Kind = ConnectionKind.Server, Location = "srv\\erp", ConnectionSpeed = "Low" };
        var args = Start(server);
        Assert.DoesNotContain("/O", args); Assert.DoesNotContain("/Proxy", args);
    }

    [Theory]
    [InlineData("/O Low", "/O")] [InlineData("/NoProxy", "/Proxy")] [InlineData("/Proxy -PSrv p -PPort 1", "/Proxy")]
    public void SameKeyInTheParametersAndOnThePageIsRefused(string parameters, string key)
    {
        var entry = Manual with { ConnectionSpeed = "Normal", AdditionalParameters = parameters };
        var message = Assert.Throws<ArgumentException>(() => Start(entry)).Message;
        Assert.Contains(key, message); Assert.Contains("Подключение", message);
        // Without the page's setting the parameters are passed as written.
        Assert.Equal(LaunchParameters.Validate(parameters), Start(Web with { AdditionalParameters = parameters }).Skip(3));
    }

    // «Запустить с аутентификацией»: the proxy stays, its password is not taken for a key, /WA- comes last.
    [Fact] public void StartWithLoginKeepsTheProxy()
    {
        var vault = new PasswordVault(Path.Combine(folder, "settings.json"));
        var entry = Manual with { ProxyUser = "ivanov" };
        vault.Write(ActivityStore.Source(vault.SettingsPath, ""), entry.Id, new Dictionary<PasswordRole, string?> { [PasswordRole.Proxy] = ProxyPassword.Pack(entry, "/WA+") });
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address, "/Proxy", "-PSrv", "proxy.local", "-PPort", "3128", "-PUser", "ivanov", "-PPwd", "/WA+", "/WA-" },
            Start(entry, vault, LaunchOptions.Login));
    }

    [Fact] public void ConcurrentEditMergesTheSpeedAloneAndTheProxyAsOneChoice()
    {
        var original = Manual with { ProxyUser = "ivanov" };
        var now = original with { ConnectionSpeed = "Low", ProxyServer = "other.local", ProxyPort = "8080" };
        // Untouched: what another program wrote stays.
        var kept = BaseEdit.OnCurrent([now], original, original with { Comment = "x" })!;
        Assert.Equal(("Low", "other.local", "8080", "ivanov"), (kept.ConnectionSpeed, kept.ProxyServer, kept.ProxyPort, kept.ProxyUser));
        // The user changed the port: the proxy of the dialog is taken whole, the speed stays as it is now.
        var mine = BaseEdit.OnCurrent([now], original, original with { ProxyPort = "3129" })!;
        Assert.Equal(("Low", "proxy.local", "3129", "ivanov"), (mine.ConnectionSpeed, mine.ProxyServer, mine.ProxyPort, mine.ProxyUser));
        Assert.Equal("Normal", BaseEdit.OnCurrent([now], original, original with { ConnectionSpeed = "Normal" })!.ConnectionSpeed);
    }

    // Codex audit P2: a proxy setting in the parameters next to the page's proxy is refused, not passed twice.
    [Theory] [InlineData("-PSrv proxy-b")] [InlineData("-PPort 8080")] [InlineData("-PUser petrov")]
    public void ProxySettingInTheParametersNextToThePageIsRefused(string parameters)
    {
        var message = Assert.Throws<ArgumentException>(() => Start(Manual with { AdditionalParameters = parameters })).Message;
        Assert.Contains("/Proxy", message); Assert.Contains("Подключение", message);
        // «Не используется» on the page and a proxy of the parameters contradict each other as well.
        Assert.Throws<ArgumentException>(() => Start(Web with { UseProxy = ProxyMode.None, AdditionalParameters = parameters }));
        // Without the page's proxy the parameters are passed as written.
        Assert.Equal(LaunchParameters.Validate(parameters), Start(Web with { AdditionalParameters = parameters }).Skip(3));
    }

    // Codex audit P1: saving over another program's change keeps the proxy it wrote; the proxy password of the dialog belongs to
    // the dialog's proxy, and the check after the save sees the difference.
    [Fact] public void SaveOverExternalChangeKeepsTheOtherProxyAndTheCheckSeesIt()
    {
        var store = Write(Record + "UseProxy=2\r\nProxyServer=proxy-a\r\nProxyPort=3128\r\n");
        var library = store.Load(new());
        var dialog = library.Bases[0] with { Comment = "x" };
        File.WriteAllText(ListPath, File.ReadAllText(ListPath).Replace("ProxyServer=proxy-a", "ProxyServer=proxy-b"), new UTF8Encoding(false));
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [dialog] });
        var stored = saved.Bases.Single(x => x.Id == dialog.Id);
        Assert.Equal("proxy-b", stored.ProxyServer);
        Assert.False(BaseEdit.SameProxy(stored, dialog));
        Assert.True(BaseEdit.SameProxy(stored, stored with { ProxyServer = " proxy-b ", Comment = "y" }));
        Assert.False(BaseEdit.SameProxy(stored, stored with { ProxyUser = "ivanov" }));
    }

    // Grok audit P2: a saved proxy password goes only to the proxy it was saved for.
    [Fact] public void SavedProxyPasswordGoesOnlyToItsProxy()
    {
        var vault = new PasswordVault(Path.Combine(folder, "settings.json"));
        var entry = Manual with { ProxyUser = "ivanov" };
        vault.Write(ActivityStore.Source(vault.SettingsPath, ""), entry.Id, new Dictionary<PasswordRole, string?> { [PasswordRole.Proxy] = ProxyPassword.Pack(entry with { ProxyServer = " proxy.local " }, "old-secret") });
        Assert.Equal("old-secret", Start(entry, vault).Last());
        foreach (var other in new[] { entry with { ProxyServer = "proxy-b" }, entry with { ProxyPort = "8080" }, entry with { ProxyUser = "petrov" } })
            Assert.DoesNotContain("old-secret", Start(other, vault));
        // The list's own password is the fallback, as without a saved one.
        Assert.Equal("from-list", Start(entry with { ProxyServer = "proxy-b", ListProxyPassword = "from-list" }, vault).Last());
        Assert.Null(ProxyPassword.Unpack(entry, "old-format"));
        Assert.Null(ProxyPassword.Unpack(entry, null));
    }

    // Grok audit P2: a value of the list this program does not know stays until a speed is chosen; then it is replaced or removed.
    [Fact] public void UnknownSpeedOfTheListIsReplacedByAChoice()
    {
        var store = Write(Record + "ClientConnectionSpeed=proxy.local\r\nUseProxy=9\r\n");
        var library = store.Load(new());
        var read = library.Bases.Single();
        Assert.Equal((null, null, "9"), (read.ConnectionSpeed, read.UseProxy, read.ListUseProxy));
        Assert.Equal(new[] { "ENTERPRISE", "/WS", Address }, Start(read));
        // Untouched: the lines stay as written.
        store.Save(library with { Bases = [read with { Comment = "x" }] });
        Assert.Contains("ClientConnectionSpeed=proxy.local\r\n", File.ReadAllText(ListPath));
        // «Выбирать при запуске» chosen: the line goes; a chosen proxy way replaces UseProxy=9.
        library = store.Load(new());
        store.Save(library with { Bases = [library.Bases[0] with { ConnectionSpeed = "", UseProxy = ProxyMode.Auto }] });
        var text = File.ReadAllText(ListPath);
        Assert.DoesNotContain("ClientConnectionSpeed", text); Assert.Contains("UseProxy=1\r\n", text); Assert.DoesNotContain("UseProxy=9", text);
        // A new record with no known speed gets no line; the export skips it too.
        Assert.DoesNotContain("ClientConnectionSpeed", ListMaintenance.ToV8i(new Library { Bases = [Web with { ConnectionSpeed = null }] }));
    }

    [Theory] [InlineData("Auto")] [InlineData("normal")]
    public void UnknownSpeedIsRefused(string speed) =>
        Assert.Throws<ArgumentException>(() => (Web with { ConnectionSpeed = speed }).Validate());
}
