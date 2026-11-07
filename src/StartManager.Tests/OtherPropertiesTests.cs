using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>The page «Прочие свойства» of the base properties: the forbidden removal and the base's own double click.</summary>
public sealed class OtherPropertiesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-other-properties-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "Settings", "settings.json");
    private static readonly Guid Base = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public OtherPropertiesTests() => Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
    public void Dispose() => Directory.Delete(root, true);

    private static BaseEntry Entry(ConnectionKind kind = ConnectionKind.Server, BaseDoubleClick? action = null) => new()
    {
        Id = Base, Name = "База", Kind = kind, DoubleClick = action,
        Location = kind == ConnectionKind.Web ? "https://server/base" : "server\\base"
    };

    [Fact] public void DoubleClickUsesTheBaseChoiceElseTheGeneralOne()
    {
        Assert.Equal(BaseDoubleClick.Enterprise, Entry().DoubleClickFor(DoubleClickAction.Enterprise));
        Assert.Equal(BaseDoubleClick.Designer, Entry().DoubleClickFor(DoubleClickAction.Designer));
        foreach (var action in Enum.GetValues<BaseDoubleClick>())
        {
            Assert.Equal(action, Entry(action: action).DoubleClickFor(DoubleClickAction.Designer));
            Assert.Equal(action, Entry(action: action).DoubleClickFor(DoubleClickAction.Enterprise));
        }
        // The configurator does not connect to a base on a web server: the general setting and the base's own choice open 1С:Предприятие.
        Assert.Equal(BaseDoubleClick.Enterprise, Entry(ConnectionKind.Web).DoubleClickFor(DoubleClickAction.Designer));
        Assert.Equal(BaseDoubleClick.Enterprise, Entry(ConnectionKind.Web, BaseDoubleClick.Designer).DoubleClickFor(DoubleClickAction.Enterprise));
        Assert.Equal(BaseDoubleClick.Properties, Entry(ConnectionKind.Web, BaseDoubleClick.Properties).DoubleClickFor(DoubleClickAction.Designer));
    }

    [Fact] public void LocalListKeepsThePropertiesAndRejectsAnUnknownAction()
    {
        var store = new LibraryStore(Settings);
        store.Save(new Library { Bases = [Entry(action: BaseDoubleClick.EnterpriseParameters) with { DisallowDelete = true }] });
        var loaded = Assert.Single(store.Load().Bases);
        Assert.True(loaded.DisallowDelete);
        Assert.Equal(BaseDoubleClick.EnterpriseParameters, loaded.DoubleClick);
        // Not set: the general setting, removal allowed.
        store.Save(new Library { Bases = [Entry()] });
        var plain = Assert.Single(new LibraryStore(Settings).Load().Bases);
        Assert.False(plain.DisallowDelete); Assert.Null(plain.DoubleClick);
        var text = File.ReadAllText(Settings).Replace("\"DoubleClick\": null", "\"DoubleClick\": 99");
        Assert.Contains("\"DoubleClick\": 99", text);
        File.WriteAllText(Settings, text);
        Assert.Contains("двойном щелчке", Assert.Throws<ArgumentException>(() => new LibraryStore(Settings).Load()).Message);
    }

    [Fact] public void SharedListLeavesThePropertiesLocal()
    {
        var list = Path.Combine(root, "ibases.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Base}\r\nCustom=keep\r\n[Другая]\r\nConnect=Srvr=\"server\";Ref=\"other\";\r\nID={Other}\r\n", new UTF8Encoding(false));
        var before = File.ReadAllText(list);
        var store = new V8iStore(list); var library = store.Load(new());
        Assert.All(library.Bases, x => { Assert.False(x.DisallowDelete); Assert.Null(x.DoubleClick); });
        var changed = library.Bases[0] with { DisallowDelete = true, DoubleClick = BaseDoubleClick.Properties, RunAsAdmin = true };
        var saved = store.Save(library with { Bases = [changed, library.Bases[1]] });
        // Nothing of the page goes to the shared list: the file stays as it was.
        Assert.Equal(before, File.ReadAllText(list));
        new LibraryStore(Settings).Save(saved);
        var reloaded = new V8iStore(list).Load(new LibraryStore(Settings).Load());
        Assert.True(reloaded.Bases[0].DisallowDelete); Assert.Equal(BaseDoubleClick.Properties, reloaded.Bases[0].DoubleClick); Assert.True(reloaded.Bases[0].RunAsAdmin);
        Assert.False(reloaded.Bases[1].DisallowDelete); Assert.Null(reloaded.Bases[1].DoubleClick); Assert.False(reloaded.Bases[1].RunAsAdmin);
    }

    [Fact] public void RunAsAdminElevatesEveryStartOnWindowsOnly()
    {
        // Launcher.Start applies it, so desktop shortcuts do too (Codex audit 12.10).
        var admin = Entry() with { RunAsAdmin = true };
        Assert.Equal(OperatingSystem.IsWindows(), Launcher.Elevated(admin, false));
        Assert.False(Launcher.Elevated(Entry(), false));
        // An explicit request stays a request on every system: Linux answers it with its own error.
        Assert.True(Launcher.Elevated(Entry(), true));
        // The flag of a base opens a web base in the client, not the browser, as an explicit request does.
        var web = Entry(ConnectionKind.Web) with { RunAsAdmin = true, ClientType = "WebClient" };
        Assert.Equal(!OperatingSystem.IsWindows(), Launcher.OpensInBrowser(web, false, SessionClient.Auto, Launcher.Elevated(web, false)));
    }

    [Fact] public void RestoringAListWithoutAProtectedBaseIsRefused()
    {
        var kept = Entry() with { DisallowDelete = true };
        var free = Entry() with { Id = Other, Name = "Другая" };
        var forbidden = BaseEdit.ForbiddenRemovals([kept, free], [free]);
        Assert.Equal(Base, Assert.Single(forbidden).Id);
        // The protected base is in the restored list (even renamed or changed there): nothing to refuse; an unprotected one may go.
        Assert.Empty(BaseEdit.ForbiddenRemovals([kept, free], [kept with { Name = "Новое имя" }]));
        Assert.Empty(BaseEdit.ForbiddenRemovals([free], []));
    }

    [Fact] public void SnapshotWithoutLocalStateKeepsThePageOfTheSameBase()
    {
        // A snapshot of a shared list (or of the original) is the .v8i text only.
        var list = Path.Combine(root, "snapshot.v8i");
        File.WriteAllText(list, $"[База из снимка]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Base}\r\n[Новая]\r\nConnect=Srvr=\"server\";Ref=\"new\";\r\nID={Other}\r\n", new UTF8Encoding(false));
        var read = new V8iStore(list).Load(new()).Bases;
        var current = Entry(action: BaseDoubleClick.Properties) with { DisallowDelete = true, Executable = "client", RunAsAdmin = true };
        var restored = BaseEdit.KeepLocal(read, [current]);
        var store = new LibraryStore(Settings);
        store.Save(new Library { Bases = restored });
        var reloaded = new LibraryStore(Settings).Load().Bases;
        var same = Assert.Single(reloaded, x => x.Id == Base);
        Assert.Equal("База из снимка", same.Name);
        Assert.True(same.DisallowDelete); Assert.Equal(BaseDoubleClick.Properties, same.DoubleClick); Assert.Equal("client", same.Executable); Assert.True(same.RunAsAdmin);
        var added = Assert.Single(reloaded, x => x.Id == Other);
        Assert.False(added.DisallowDelete); Assert.Null(added.DoubleClick);
    }

    [Theory]
    [InlineData("1", BaseDoubleClick.Enterprise)]
    [InlineData("2", BaseDoubleClick.EnterpriseLogin)]
    [InlineData("3", BaseDoubleClick.EnterpriseParameters)]
    [InlineData("4", BaseDoubleClick.Designer)]
    [InlineData("6", BaseDoubleClick.Properties)]
    [InlineData("0", null)]
    public void ImportReadsThePageOfTheOriginal(string value, BaseDoubleClick? expected)
    {
        var entry = Assert.Single(Import($"DisallowDelete=1\nActionDblClick={value}\nRunAsAdmin=1\nApp=3\n", new()).Added);
        Assert.True(entry.DisallowDelete); Assert.True(entry.RunAsAdmin); Assert.True(entry.ManagedApplication);
        // Codex audit: the list has no App=, the original's own mode 3 gives the thick client.
        Assert.Equal("ThickClient", entry.ClientType);
        Assert.Equal(expected, entry.DoubleClick);
    }

    [Fact] public void ImportReportsSnegopatAndHonoursTheChoice()
    {
        var plan = Import("DisallowDelete=0\nActionDblClick=5\n", new());
        var entry = Assert.Single(plan.Added);
        Assert.False(entry.DisallowDelete); Assert.Null(entry.DoubleClick); Assert.False(entry.RunAsAdmin);
        Assert.Contains(plan.Notices, x => x.Contains("Snegopat"));
        Assert.DoesNotContain(plan.Notices, x => x.Contains("ActionDblClick"));
        // Not chosen: the base comes without the page and the keys are named as not transferred.
        var skipped = Import("DisallowDelete=1\nActionDblClick=4\n", new(Properties: false));
        Assert.False(skipped.Added[0].DisallowDelete); Assert.Null(skipped.Added[0].DoubleClick);
        Assert.Contains(skipped.Notices, x => x.Contains("ActionDblClick") && x.Contains("DisallowDelete"));
    }

    private LegacyImportPreview Import(string properties, LegacyImportOptions options)
    {
        var folder = Path.Combine(root, "SMSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "v8config.smc"), $"[{Base}]\n{properties}");
        var input = Path.Combine(folder, "input.v8i");
        File.WriteAllText(input, $"[База]\nID={Base}\nConnect=Srvr=\"server\";Ref=\"base\";\nFolder=/\n");
        return LegacyImport.Preview(new Library(), input, folder, options);
    }
}
