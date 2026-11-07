using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class LaunchParametersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-parameters-" + Guid.NewGuid().ToString("N"));
    private string Client => Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8");
    private BaseEntry Entry => new() { Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
    public LaunchParametersTests() { Directory.CreateDirectory(root); File.WriteAllText(Client, "fixture"); }
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void ArgumentsPreserveQuotedValuesWithoutAShell()
    {
        var entry = Entry with { AdditionalParameters = "/UC \"код доступа\" /C \"& literal; $(value)\" /Debug" };
        var start = Launcher.Build(Client, entry, false);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "ENTERPRISE", "/S", "server\\base", "/UC", "код доступа", "/C", "& literal; $(value)", "/Debug" }, start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("value with spaces")]
    [InlineData("C:\\каталог с пробелами\\")]
    [InlineData("\\\\server\\share\\file.epf")]
    [InlineData("embedded \"quotes\" and \\\"quoted")]
    [InlineData("/home/user/file.epf")]
    public void QuotingRoundTripsOnBothOperatingSystems(string value)
    {
        Assert.Equal(value, Assert.Single(LaunchParameters.Parse(LaunchParameters.Quote(value))));
    }

    [Fact]
    public void ParserSupportsAttachedValuesEmptyArgumentsAndDoubledQuotes()
    {
        Assert.Equal(new[] { "/UCsecret value", "/C", "", "a\"b", @"C:\tools\file" },
            LaunchParameters.Parse("/UC\"secret value\" /C \"\" \"a\"\"b\" C:\\tools\\file"));
    }

    [Theory]
    [InlineData("/C \"unfinished")]
    [InlineData("/Debug\n/Out file")]
    [InlineData("/Debug\0")]
    [InlineData("/UC")]
    [InlineData("/P password")]
    [InlineData("/psecret")]
    [InlineData("/WSPsecret")]
    [InlineData("/ConfigurationRepositoryPsecret")]
    [InlineData("/F other")]
    [InlineData("/Fother")]
    [InlineData("/Sserver\\other")]
    [InlineData("/WShttps://other/base")]
    [InlineData("/IBConnectionString value")]
    [InlineData("/IBNameOther")]
    [InlineData("/RunShortcut file.v8l")]
    [InlineData("/Execute file.epf")]
    [InlineData("/L /Psecret")]
    [InlineData("/N /Psecret")]
    [InlineData("/UC /Fother")]
    [InlineData("/C /Execute x.epf")]
    [InlineData("/C \"/Psecret\"")]
    [InlineData("/Out /Psecret")]
    [InlineData("/Out /Fother")]
    [InlineData("/Proxy -PPwd secret")]
    [InlineData("-PPasswd secret")]
    [InlineData("/ModifyPassword secret")]
    [InlineData("/AccessToken secret")]
    [InlineData("/@ parameters.txt")]
    [InlineData("/O")]
    [InlineData("/Out /Out /Psecret/x")]
    [InlineData("/UC /Out /Psecret/x")]
    [InlineData("/L /Out /Psecret/x")]
    [InlineData("/C /Out /Fsrv/base")]
    public void InvalidOrConflictingArgumentsAreRejectedWithoutEchoingValues(string text)
    {
        var error = Assert.Throws<ArgumentException>(() => LaunchParameters.Validate(text));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("/srv/1c/out.log")]
    [InlineData("/proc/self/log")]
    [InlineData("/files/log.txt")]
    [InlineData("/storage/log.txt")]
    [InlineData("/ibname/log.txt")]
    public void UnixOutputPathsAreValuesAndDoNotHideFollowingSwitches(string path)
    {
        Assert.Equal(new[] { "/Out", path, "/Debug" }, LaunchParameters.Validate("/Out " + LaunchParameters.Quote(path) + " /Debug"));
        Assert.Throws<ArgumentException>(() => LaunchParameters.Validate("/Out " + LaunchParameters.Quote(path) + " /Psecret"));
    }

    [Theory]
    [InlineData("/Out /Debug", "/Out", "/Debug")]
    [InlineData("/UC /L ru", "/UC", "/L")]
    [InlineData("/Debug /c /DisableStartupDialogs", "/c", "/DisableStartupDialogs")]
    [InlineData("/N /WSN webuser", "/N", "/WSN")]
    public void ASwitchInPlaceOfAValueIsWarnedAboutButStillAccepted(string text, string key, string next)
    {
        var warning = LaunchParameters.MissingValueWarning(text);
        Assert.NotNull(warning);
        Assert.Contains(key, warning); Assert.Contains("«" + next + "»", warning);
        Assert.Equal(LaunchParameters.Parse(text), LaunchParameters.Validate(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/UC secret /L ru /Debug")]
    [InlineData("/Out /srv/1c/out.log /Debug")]
    [InlineData("/C \"/any application text\" /Debug")]
    [InlineData("/UC /Debug-not-a-key")]
    [InlineData("/Debug /DisableStartupDialogs")]
    [InlineData("/UC \"unclosed")]
    public void OrdinaryValuesAndInvalidTextGiveNoWarning(string text)
    {
        Assert.Null(LaunchParameters.MissingValueWarning(text));
    }

    [Fact]
    public void TheWarningNamesSwitchesButNeverTheFollowingValues()
    {
        var warning = LaunchParameters.MissingValueWarning("/UC /L secret-value");
        Assert.NotNull(warning);
        Assert.DoesNotContain("secret", warning);
    }

    [Fact]
    public void ProxyWithoutPasswordAndWebUserAreAccepted()
    {
        var text = "/Proxy -PSrv 10.0.0.1 -PPort 8080 -PUser user /WSN webuser /O Low";
        Assert.Equal(LaunchParameters.Parse(text), LaunchParameters.Validate(text));
    }

    [Fact]
    public void InvalidInheritedParametersExplainRecoveryWithoutExposingTheValue()
    {
        var entry = Entry with { ListParameters = "/N user /Psecret" };
        var error = Assert.Throws<ArgumentException>(() => Launcher.Build(Client, entry, false));
        Assert.Contains("из списка баз (.v8i)", error.Message);
        Assert.Contains("Исправьте или очистите строку «Дополнительные параметры»", error.Message);
        Assert.DoesNotContain("флажок", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("/N", Launcher.Build(Client, entry with { AdditionalParameters = "" }, false).ArgumentList);
    }

    [Fact]
    public void ExplicitOptionsReplaceSavedParametersAndProcessingWithoutMutatingTheBase()
    {
        var saved = Entry with { AdditionalParameters = "/ClearCache", ExternalProcessing = Path.Combine(root, "missing.epf") };
        var options = new LaunchOptions("/Debug", "");
        var start = Launcher.Build(new Library { Executable = Client }, saved, false, [], options: options);
        Assert.Contains("/Debug", start.ArgumentList);
        Assert.DoesNotContain("/ClearCache", start.ArgumentList);
        Assert.DoesNotContain("/Execute", start.ArgumentList);
        Assert.Equal("/ClearCache", saved.AdditionalParameters);
        Assert.EndsWith("missing.epf", saved.ExternalProcessing);
    }

    [Fact]
    public void LaunchWithLoginAsksOneCForCredentialsAndKeepsTheSavedOptions()
    {
        var addon = Path.Combine(root, "addon.epf"); File.WriteAllText(addon, "fixture");
        var saved = Entry with { AdditionalParameters = "/wa+ /N Иванов /Debug /WA", ExternalProcessing = addon };
        var start = Launcher.Build(new Library { Executable = Client }, saved, false, [], options: LaunchOptions.Login);
        Assert.Equal(new[] { "ENTERPRISE", "/S", saved.Location, "/N", "Иванов", "/Debug", "/Execute", addon, "/WA-" }, start.ArgumentList);
        var designer = Launcher.Build(new Library { Executable = Client }, Entry, true, [], options: LaunchOptions.Login);
        Assert.Equal(new[] { "DESIGNER", "/S", Entry.Location, "/WA-" }, designer.ArgumentList);
        Assert.Equal("/wa+ /N Иванов /Debug /WA", saved.AdditionalParameters);
        Assert.DoesNotContain("/WA-", Launcher.Build(new Library { Executable = Client }, saved, false, []).ArgumentList);
        var values = Entry with { AdditionalParameters = "/C /WA /UC /wa+ /WA+" };
        Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location, "/C", "/WA", "/UC", "/wa+", "/WA-" },
            Launcher.Build(new Library { Executable = Client }, values, false, [], options: LaunchOptions.Login).ArgumentList);
    }

    // Codex audit 30.09 P2: /WA after a key outside the catalog may be that key's value; it is never removed silently.
    [Theory]
    [InlineData("/DumpConfigFiles /WA+ /Debug", "/DumpConfigFiles")]
    [InlineData("/DumpResult /WA /Debug", "/DumpResult")]
    [InlineData("/Debug /UnknownKey /wa-", "/UnknownKey")]
    [InlineData("/Debug -PUnknown /WA", "-PUnknown")]
    [InlineData("/UnknownWithValue \"/user marker-value\" /WA+", "marker-value")]
    public void LaunchWithLoginRefusesAWaThatMayBeTheValueOfAnUnknownKey(string text, string token)
    {
        foreach (var entry in new[] { Entry with { AdditionalParameters = text }, Entry with { ListParameters = text } })
        {
            var error = Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Client }, entry, true, [], options: LaunchOptions.Login));
            Assert.Contains("/WA", error.Message);
            Assert.DoesNotContain(token, error.Message);
            Assert.Equal(LaunchParameters.Parse(text), Launcher.Build(new Library { Executable = Client }, entry, true, []).ArgumentList.Skip(3));
        }
        Assert.Equal(new[] { "DESIGNER", "/S", Entry.Location, "/ConfigurationRepositoryN", "user", "/Debug", "/WA-" },
            Launcher.Build(new Library { Executable = Client }, Entry with { AdditionalParameters = "/ConfigurationRepositoryN user /WA+ /Debug" },
                true, [], options: LaunchOptions.Login).ArgumentList);
    }

    // Codex re-check P2: a value of a proxy key is kept, even when it looks like /WA.
    [Fact]
    public void LaunchWithLoginKeepsTheValuesOfProxyKeys()
    {
        var thin = Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "fixture");
        var text = "/Proxy -PSrv proxy.example.test -PPort 8080 -PUser /WA+ /Debug";
        var web = Entry with { Kind = ConnectionKind.Web, Location = "https://example.test/base" };
        foreach (var entry in new[] { web with { AdditionalParameters = text }, web with { ListParameters = text } })
            Assert.Equal(new[] { "ENTERPRISE", "/WS", web.Location, "/Proxy", "-PSrv", "proxy.example.test", "-PPort", "8080", "-PUser", "/WA+", "/Debug", "/WA-" },
                Launcher.Build(new Library { Executable = Client }, entry, false, [], options: LaunchOptions.Login).ArgumentList);
        Assert.NotNull(LaunchParameters.MissingValueWarning("/Proxy -PSrv -PPort 8080"));
        // Kimi 0.3.0.3 P3: /WA after a flag without a value is replaced, not refused.
        foreach (var flag in new[] { "/Proxy", "/UseHwLicenses+", "/AppAutoCheckVersion+" })
            Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location, flag, "/WA-" },
                Launcher.Build(new Library { Executable = Client }, Entry with { AdditionalParameters = flag + " /WA+" }, false, [], options: LaunchOptions.Login).ArgumentList);
        // Codex re-check 2: a trailing proxy key without a value is refused before /WA- could become its value.
        var incomplete = "/Proxy -PSrv proxy.example.test -PPort 8080 -PUser";
        Assert.Throws<ArgumentException>(() => LaunchParameters.Validate(incomplete));
        foreach (var entry in new[] { web with { AdditionalParameters = incomplete }, web with { ListParameters = incomplete } })
            Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Client }, entry, false, [], options: LaunchOptions.Login));
    }

    [Fact]
    public void LaunchWithLoginKeepsInheritedListParametersAndTheirRecoveryHint()
    {
        var inherited = Entry with { ListParameters = "/L ru" };
        var start = Launcher.Build(new Library { Executable = Client }, inherited, false, [], options: LaunchOptions.Login);
        Assert.Equal(new[] { "ENTERPRISE", "/S", Entry.Location, "/L", "ru", "/WA-" }, start.ArgumentList);
        var invalid = Entry with { ListParameters = "/N user /Psecret" };
        var error = Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Client }, invalid, false, [], options: LaunchOptions.Login));
        Assert.Contains("из списка баз (.v8i)", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void LaunchWithLoginOpensAWebBaseInTheThinClient()
    {
        var thin = Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "fixture");
        var entry = Entry with { Kind = ConnectionKind.Web, Location = "https://example.test/base", ClientType = "WebClient" };
        var start = Launcher.Build(new Library { Executable = Client }, entry, false, [], options: LaunchOptions.Login);
        Assert.Equal(thin, start.FileName);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", entry.Location, "/WA-" }, start.ArgumentList);
        Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Client }, entry, true, [], options: LaunchOptions.Login));
    }

    [Fact]
    public void ProcessingRunsOnlyInEnterpriseAndMissingFileIsReported()
    {
        var path = Path.Combine(root, "Обработка с пробелами.epf"); File.WriteAllText(path, "fixture");
        var entry = Entry with { ExternalProcessing = path, AdditionalParameters = "/Debug" };
        Assert.Equal(new[] { "ENTERPRISE", "/S", entry.Location, "/Debug", "/Execute", path }, Launcher.Build(Client, entry, false).ArgumentList);
        Assert.DoesNotContain("/Execute", Launcher.Build(Client, entry, true).ArgumentList);
        File.Delete(path);
        Assert.Throws<ArgumentException>(() => Launcher.Build(Client, entry, false));
        Assert.Contains("DESIGNER", Launcher.Build(Client, entry, true).ArgumentList);
    }

    [Fact]
    public void OneTimeVersionOverridesTheManualClientAndKeepsTheSavedRule()
    {
        var native = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? PlatformArchitecture.Arm64 : PlatformArchitecture.X64;
        var installed = new PlatformInstallation(Client, new Version(8, 3, 27, 2214), native);
        var saved = Entry with { Executable = Path.Combine(root, "absent"), PlatformRule = new() { Version = "8.2" } };
        var start = Launcher.Build(new Library(), saved, false, [installed], options: new("", "") { Platform = new() { Version = "8.3" } });
        Assert.Equal(Client, start.FileName);
        Assert.Equal("8.2", saved.PlatformRule.Version);
        Assert.Throws<InvalidOperationException>(() => Launcher.Build(new Library(), saved, false, [installed], options: new("", "") { Platform = new() { Version = "8.5" } }));
    }

    [Fact]
    public void WebClientParametersSelectTheThinClientInsteadOfBeingSilentlyLost()
    {
        var thin = Path.Combine(root, OperatingSystem.IsWindows() ? "1cv8c.exe" : "1cv8c"); File.WriteAllText(thin, "fixture");
        var entry = Entry with { Kind = ConnectionKind.Web, Location = "https://example.test/base", ClientType = "WebClient" };
        Assert.True(Launcher.OpensInBrowser(entry, false, SessionClient.Auto));
        entry = entry with { AdditionalParameters = "/C \"start test\"" };
        Assert.False(Launcher.OpensInBrowser(entry, false, SessionClient.Auto));
        var start = Launcher.Build(new Library { Executable = Client }, entry, false, []);
        Assert.Equal(thin, start.FileName);
        Assert.Equal(new[] { "ENTERPRISE", "/WS", entry.Location, "/C", "start test" }, start.ArgumentList);
        Assert.Throws<ArgumentException>(() => Launcher.Build(new Library { Executable = Client }, entry, true, []));
    }

    [Fact]
    public void SharedListPreferencesSurviveRestartSwitchesAndUnknownFieldsStayUntouched()
    {
        var id = Guid.NewGuid();
        var a = Path.Combine(root, "a.v8i"); var b = Path.Combine(root, "b.v8i");
        var list = $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={id}\r\nUnknown=keep\r\nAdditionalParameters=/KeepOriginal\r\n";
        File.WriteAllText(a, list); File.WriteAllText(b, list);
        var profile = new LibraryStore(Path.Combine(root, "library.json"));
        Library Open(Library settings, string path) => new V8iStore(path).Load(LibrarySources.Select(settings, profile.PathName, path));
        var library = Open(new Library(), a);
        var extra = new Dictionary<string, JsonElement> { ["Future"] = JsonSerializer.SerializeToElement("keep") };
        library = library with { Bases = [library.Bases[0] with { AdditionalParameters = "/Debug", Comment = "Комментарий", ExternalProcessing = Path.Combine(root, "addon.epf"), Extra = extra }] };
        profile.Save(library);
        var second = Open(profile.Load(), b);
        Assert.Null(second.Bases[0].AdditionalParameters);
        Assert.Equal("/KeepOriginal", second.Bases[0].EffectiveParameters);
        profile.Save(second);
        var restored = Open(profile.Load(), a).Bases[0];
        Assert.Equal("/Debug", restored.AdditionalParameters); Assert.Equal("Комментарий", restored.Comment);
        Assert.Equal("keep", restored.Extra!["Future"].GetString());
        Assert.EndsWith("addon.epf", restored.ExternalProcessing);
        Assert.Equal(list, File.ReadAllText(a)); Assert.Equal(list, File.ReadAllText(b));
        var fromShortcut = new ShortcutRequest(profile.PathName, a, id, false).Resolve().Entry;
        Assert.Equal(restored.AdditionalParameters, fromShortcut.AdditionalParameters);
        var cleared = Open(profile.Load(), a) with { Bases = [restored with { AdditionalParameters = "", ExternalProcessing = "", Comment = "" }] };
        profile.Save(cleared);
        Assert.Equal("", Open(profile.Load(), a).Bases[0].AdditionalParameters);
    }

    [Fact]
    public void PasswordParametersAreNeverWrittenToTheProfile()
    {
        var store = new LibraryStore(Path.Combine(root, "library.json"));
        var library = new Library { Bases = [Entry] }; store.Save(library);
        var before = File.ReadAllBytes(store.PathName);
        Assert.Throws<ArgumentException>(() => store.Save(library with { Bases = [Entry with { AdditionalParameters = "/Psecret" }] }));
        Assert.Equal(before, File.ReadAllBytes(store.PathName));
        Assert.DoesNotContain("secret", File.ReadAllText(store.PathName));
    }

    [Fact]
    public void InvalidUnchangedParametersInAnotherSourceDoNotBlockRepairOrUnrelatedSettings()
    {
        var path = Path.Combine(root, "library.json");
        var old = Entry with { AdditionalParameters = "/C \"bad" };
        var active = Entry with { AdditionalParameters = "/Debug" };
        var source = ActivityStore.Source(path, Path.Combine(root, "other.v8i"));
        File.WriteAllText(path, JsonSerializer.Serialize(new Library { Bases = [active], Sources = new() { [source] = new() { Bases = [old] } } }));
        var store = new LibraryStore(path); var loaded = store.Load();
        store.Save(loaded with { AfterLaunch = AfterLaunchAction.Minimize });
        Assert.Equal(old.AdditionalParameters, store.Load().Sources[source].Bases[0].AdditionalParameters);
        Assert.Throws<ArgumentException>(() => store.Save(store.Load() with { Bases = [active with { AdditionalParameters = "/L /Psecret" }] }));
        var repaired = store.Load();
        repaired.Sources[source] = new SourceSettings { Bases = [old with { AdditionalParameters = "" }] };
        store.Save(repaired);
        Assert.Empty(store.Load().Sources[source].Bases[0].EffectiveParameters);
    }

    [Fact]
    public void SharedParametersAreInheritedUntilExplicitlyOverriddenAndCanBeCleared()
    {
        var path = Path.Combine(root, "shared.v8i"); var id = Guid.NewGuid();
        var text = $"[База]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={id}\nAdditionalParameters=/L en\nUnknown=keep\n";
        File.WriteAllText(path, text);
        var shared = new V8iStore(path); var profile = new LibraryStore(Path.Combine(root, "library.json"));
        var library = shared.Load(new Library());
        Assert.Null(library.Bases[0].AdditionalParameters);
        Assert.Contains("en", Launcher.Build(Client, library.Bases[0], false).ArgumentList);
        profile.Save(library);
        Assert.DoesNotContain("/L en", File.ReadAllText(profile.PathName));
        File.WriteAllText(path, text.Replace("/L en", "/L ru"));
        library = shared.Load(profile.Load()); Assert.Equal("/L ru", library.Bases[0].EffectiveParameters);
        library = library with { Bases = [library.Bases[0] with { AdditionalParameters = "" }] };
        profile.Save(library); Assert.Empty(shared.Load(profile.Load()).Bases[0].EffectiveParameters);
        library = library with { Bases = [library.Bases[0] with { AdditionalParameters = null }] };
        profile.Save(library); Assert.Equal("/L ru", shared.Load(profile.Load()).Bases[0].EffectiveParameters);
        Assert.Equal(text.Replace("/L en", "/L ru"), File.ReadAllText(path));
    }

    [Fact]
    public void LongerSOptionsAreNotMistakenForAConnection()
    {
        Assert.Equal(new[] { "/SimulateServerCallDelay", "100", "/SAOnRestart", "/SLev2", "/SafeMode" },
            LaunchParameters.Validate("/SimulateServerCallDelay 100 /SAOnRestart /SLev2 /SafeMode"));
    }
}
