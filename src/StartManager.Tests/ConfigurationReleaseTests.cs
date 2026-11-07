using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Конфигурация:» and «Релиз:» of the page «Сведения» (local, like the comment), « [релиз]» in the list, and the import of
/// Note, ConfigVersion, ConfigId (with Descr of v8updates.lst) and of the groups' Directory from the original.</summary>
public sealed class ConfigurationReleaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bn-config-release-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "Settings", "settings.json");
    private static readonly Guid Base = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Group = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Child = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static BaseEntry Entry => new() { Id = Base, Name = "База", Kind = ConnectionKind.Server, Location = "server\\base" };
    private static string Folder(string name) => OperatingSystem.IsWindows() ? @"D:\Bases\" + name : "/data/bases/" + name;
    public ConfigurationReleaseTests() => Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
    public void Dispose() => Directory.Delete(root, true);

    [Fact] public void TheFieldsAreLocalAndTheSharedListStaysByteForByte()
    {
        var list = Path.Combine(root, "ibases.v8i");
        File.WriteAllText(list, $"[База]\r\nConnect=Srvr=\"server\";Ref=\"base\";\r\nID={Base}\r\nCustom=keep\r\n[Другая]\r\nConnect=Srvr=\"server\";Ref=\"other\";\r\nID={Other}\r\n", new UTF8Encoding(true));
        var before = File.ReadAllBytes(list);
        var store = new V8iStore(list); var library = store.Load(new());
        Assert.All(library.Bases, x => { Assert.Equal("", x.Configuration); Assert.Equal("", x.ConfigurationRelease); Assert.Equal("", x.ConfigurationId); });
        var changed = library.Bases[0] with { Configuration = "Бухгалтерия предприятия", ConfigurationRelease = "3.0.150.25", ConfigurationId = "Accounting" };
        var saved = store.Save(library with { Bases = [changed, library.Bases[1]] });
        Assert.Equal(before, File.ReadAllBytes(list));
        new LibraryStore(Settings).Save(saved);
        Assert.Contains("\"ConfigurationRelease\": \"3.0.150.25\"", File.ReadAllText(Settings));
        var reloaded = new V8iStore(list).Load(new LibraryStore(Settings).Load());
        Assert.Equal("Бухгалтерия предприятия", reloaded.Bases[0].Configuration);
        Assert.Equal("3.0.150.25", reloaded.Bases[0].ConfigurationRelease);
        Assert.Equal("Accounting", reloaded.Bases[0].ConfigurationId);
        Assert.Equal("", reloaded.Bases[1].ConfigurationRelease);
        Assert.Equal(before, File.ReadAllBytes(list));
    }

    [Fact] public void LocalListKeepsThemAndRefusesControlCharacters()
    {
        var store = new LibraryStore(Settings);
        store.Save(new Library { Bases = [Entry with { Configuration = "ERP", ConfigurationRelease = "2.5.17.80", ConfigurationId = "ERP25" }] });
        var loaded = Assert.Single(new LibraryStore(Settings).Load().Bases);
        Assert.Equal(("ERP", "2.5.17.80", "ERP25"), (loaded.Configuration, loaded.ConfigurationRelease, loaded.ConfigurationId));
        // A profile written before the fields: they are empty.
        File.WriteAllText(Settings, File.ReadAllText(Settings).Replace("\"Configuration\": \"ERP\",", "").Replace("\"ConfigurationRelease\": \"2.5.17.80\",", ""));
        var old = Assert.Single(new LibraryStore(Settings).Load().Bases);
        Assert.Equal(("", ""), (old.Configuration, old.ConfigurationRelease));
        Assert.Throws<ArgumentException>(() => (Entry with { ConfigurationRelease = "1.0\n2.0" }).Validate());
        Assert.Throws<ArgumentException>(() => (Entry with { Configuration = "ERP\t" }).Validate());
        Assert.Throws<ArgumentException>(() => (Entry with { ConfigurationId = null! }).Validate());
    }

    [Fact] public void ASnapshotWithoutLocalStateKeepsThemForTheSameBase()
    {
        var read = new[] { Entry with { Name = "Из снимка" }, Entry with { Id = Other, Name = "Новая" } };
        var restored = BaseEdit.KeepLocal(read, [Entry with { Configuration = "ERP", ConfigurationRelease = "2.5", ConfigurationId = "ERP25" }]);
        Assert.Equal(("Из снимка", "ERP", "2.5", "ERP25"), (restored[0].Name, restored[0].Configuration, restored[0].ConfigurationRelease, restored[0].ConfigurationId));
        Assert.Equal("", restored[1].ConfigurationRelease);
    }

    [Fact] public void AConcurrentEditKeepsWhatTheUserDidNotTouch()
    {
        var original = Entry with { Configuration = "ERP", ConfigurationRelease = "2.5.17.80", ConfigurationId = "ERP25" };
        var now = original with { ConfigurationRelease = "2.5.18.10", Comment = "чужой" };
        // The comment was edited here; the release changed in another window stays.
        var kept = BaseEdit.OnCurrent([now], original, original with { Comment = "мой" })!;
        Assert.Equal(("2.5.18.10", "мой", "ERP25"), (kept.ConfigurationRelease, kept.Comment, kept.ConfigurationId));
        // The release edited here wins; the configuration not edited is taken as it is now.
        var edited = BaseEdit.OnCurrent([now with { Configuration = "ERP 2" }], original, original with { ConfigurationRelease = "2.5.19.1" })!;
        Assert.Equal(("2.5.19.1", "ERP 2"), (edited.ConfigurationRelease, edited.Configuration));
        // The dialog trims the fields: an untouched value with spaces is no change of the user.
        var spaced = original with { ConfigurationRelease = " 2.5.17.80 " };
        var returned = BaseEdit.AsDialogReturns(spaced);
        Assert.Equal("2.5.17.80", returned.ConfigurationRelease);
        Assert.Equal("2.5.18.10", BaseEdit.OnCurrent([now], returned, returned with { Comment = "мой" })!.ConfigurationRelease);
    }

    [Fact] public void TheListShowsTheReleaseAfterTheName()
    {
        Assert.Equal("База [8.3.27.1]", (Entry with { ConfigurationRelease = "8.3.27.1" }).ListName);
        Assert.Equal("База [xx]", (Entry with { ConfigurationRelease = " xx " }).ListName);
        Assert.Equal("База", Entry.ListName);
        Assert.Equal("База", (Entry with { ConfigurationRelease = "   " }).ListName);
        // The name itself (search, sort, copy) does not change.
        Assert.Equal("База", (Entry with { ConfigurationRelease = "1.0" }).Name);
    }

    [Fact] public void ImportReadsNoteReleaseAndTheConfigurationOfV8updates()
    {
        var plan = Import($"[{Base}]\nNote=Основная база\nConfigVersion=3.0.150.25\nConfigId=Accounting30\n",
            updates: "[Accounting30]\nDescr=Бухгалтерия предприятия, редакция 3.0\nConfigName=Accounting\n[Other]\nDescr=Другая\n");
        var entry = Assert.Single(plan.Added);
        Assert.Equal("Основная база", entry.Comment);
        Assert.Equal("3.0.150.25", entry.ConfigurationRelease);
        Assert.Equal("Accounting30", entry.ConfigurationId);
        Assert.Equal("Бухгалтерия предприятия, редакция 3.0", entry.Configuration);
        Assert.DoesNotContain(plan.Notices, x => x.Contains("ConfigVersion") || x.Contains("ConfigId") || x.Contains("Note") || x.Contains("v8updates"));
        LibraryStore.Validate(plan.Result);
    }

    [Fact] public void ImportWithoutV8updatesKeepsTheIdAndLeavesTheConfigurationEmpty()
    {
        var plan = Import($"[{Base}]\nConfigVersion=3.0.150.25\nConfigId=Accounting30\n");
        var entry = Assert.Single(plan.Added);
        Assert.Equal(("", "3.0.150.25", "Accounting30"), (entry.Configuration, entry.ConfigurationRelease, entry.ConfigurationId));
        Assert.Contains(plan.Notices, x => x.Contains("v8updates.lst"));
        // A file without the section of the base: the configuration stays empty too.
        var other = Assert.Single(Import($"[{Base}]\nConfigId=Accounting30\n", updates: "[ERP]\nDescr=ERP\n").Added);
        Assert.Equal(("", "Accounting30"), (other.Configuration, other.ConfigurationId));
    }

    [Fact] public void ImportOfEmptyOrBrokenValues()
    {
        var empty = Assert.Single(Import($"[{Base}]\nNote=\nConfigVersion=\nConfigId=\n", updates: "[Accounting30]\nDescr=Не должна попасть\n").Added);
        Assert.Equal(("", "", "", ""), (empty.Comment, empty.Configuration, empty.ConfigurationRelease, empty.ConfigurationId));
        // Without ConfigId the list of updates is not read at all.
        Assert.DoesNotContain(Import($"[{Base}]\nConfigVersion=1\n").Notices, x => x.Contains("v8updates"));
        // A control character of the original's file does not stop the import.
        var tab = Assert.Single(Import($"[{Base}]\nConfigVersion=3.0\t150\nConfigId=A\n", updates: "[A]\nDescr=Конф\u0001игурация\n").Added);
        Assert.Equal(("3.0 150", "Конф игурация"), (tab.ConfigurationRelease, tab.Configuration));
    }

    [Fact] public void ImportWithoutThePropertiesNamesTheKeys()
    {
        var plan = Import($"[{Base}]\nNote=x\nConfigVersion=1\nConfigId=A\n[{Group}]\nDirectory={Folder("archive")}\n", new(Properties: false), "[A]\nDescr=Конфигурация\n");
        var entry = Assert.Single(plan.Added);
        Assert.Equal(("", "", "", ""), (entry.Comment, entry.Configuration, entry.ConfigurationRelease, entry.ConfigurationId));
        Assert.Empty(plan.Result.GroupFolders);
        Assert.Contains(plan.Notices, x => x.Contains("ConfigId") && x.Contains("ConfigVersion") && x.Contains("Note") && x.Contains("Directory"));
    }

    [Fact] public void ImportTakesTheFolderOfAGroupByTheIdOfItsRecord()
    {
        var plan = Import($"[{Group}]\nDirectory={Folder("archive")}\nCategoryIcon=3\n[{Child}]\nDirectory={Folder("child")}\n");
        var archive = Assert.Single(plan.Result.GroupFolders, x => x.Group == "Архив");
        Assert.Equal((Folder("archive"), Group), (archive.Folder, archive.Id));
        // A nested group: the path of its record's Folder= and its name.
        var child = Assert.Single(plan.Result.GroupFolders, x => x.Group == "Архив/Старые");
        Assert.Equal((Folder("child"), Child), (child.Folder, child.Id));
        Assert.Equal(2, plan.Result.GroupFolders.Count);
        Assert.Contains(plan.Notices, x => x.Contains("CategoryIcon"));
        Assert.DoesNotContain(plan.Notices, x => x.Contains("Directory"));
        Assert.Equal(Folder("archive"), Groups.InheritedFolder(plan.Result, "Архив"));
        // A section of no group record (the ID of a base, of no record at all) gives no folder, and its Directory is named.
        var none = Import($"[{Other}]\nDirectory={Folder("nowhere")}\n[{Base}]\nDirectory={Folder("base")}\n");
        Assert.Empty(none.Result.GroupFolders);
        Assert.Contains(none.Notices, x => x.Contains("Directory"));
    }

    [Fact] public void ImportOfAGroupFolderEmptyForeignOrAlreadySet()
    {
        // Empty: nothing.
        Assert.Empty(Import($"[{Group}]\nDirectory=\n").Result.GroupFolders);
        // Relative (or of another system): named, not transferred.
        var relative = Import($"[{Group}]\nDirectory=bases\\archive\n");
        Assert.Empty(relative.Result.GroupFolders);
        Assert.Contains(relative.Notices, x => x.Contains("«Архив»") && x.Contains("не перенесён"));
        // The destination's own folder of the group stays.
        var local = new Library { Groups = ["архив"], GroupFolders = [new GroupFolder { Group = "архив", Folder = Folder("mine") }] };
        var kept = Import($"[{Group}]\nDirectory={Folder("archive")}\n", local: local);
        var folder = Assert.Single(kept.Result.GroupFolders);
        Assert.Equal((Folder("mine"), (Guid?)null), (folder.Folder, folder.Id));
        Assert.Contains(kept.Notices, x => x.Contains("уже задан"));
    }

    // Codex audit 14.10 (P2): a group and a base with one ID share one section of v8config.smc: its Directory belongs to neither.
    [Fact] public void ImportSkipsTheFolderOfAnIdAGroupSharesWithABase()
    {
        var folder = Path.Combine(root, "SMSettings-shared");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "v8config.smc"), $"[{Group}]\nDirectory={Folder("archive")}\n");
        var input = Path.Combine(folder, "input.v8i");
        File.WriteAllText(input, $"[Архив]\nID={Group}\nFolder=/\n[База]\nID={Group}\nConnect=Srvr=\"server\";Ref=\"base\";\nFolder=/Архив\n");
        var plan = LegacyImport.Preview(new Library(), input, folder, new());
        Assert.Single(plan.Added);
        Assert.Empty(plan.Result.GroupFolders);
        Assert.Contains(plan.Notices, x => x.Contains("не переносятся") && x.Contains("Directory"));
    }

    [Fact] public void ImportOfAGroupFolderSurvivesTheSaveOfTheLocalList()
    {
        var plan = Import($"[{Base}]\nConfigVersion=3.0.1\n[{Group}]\nDirectory={Folder("archive")}\n");
        var store = new LibraryStore(Settings);
        var applied = LegacyImport.Apply(plan, store, new ActivityStore(Path.Combine(root, "Settings", "activity.json")));
        Assert.Null(applied.FavoriteError);
        var loaded = new LibraryStore(Settings).Load();
        Assert.Equal(Folder("archive"), Groups.Folder(loaded, "Архив"));
        Assert.Equal("3.0.1", Assert.Single(loaded.Bases).ConfigurationRelease);
    }

    private LegacyImportPreview Import(string smc, LegacyImportOptions? options = null, string? updates = null, Library? local = null)
    {
        var folder = Path.Combine(root, "SMSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "v8config.smc"), smc);
        if (updates is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "Updates"));
            File.WriteAllText(Path.Combine(folder, "Updates", "v8updates.lst"), updates, new UTF8Encoding(true));
        }
        var input = Path.Combine(folder, "input.v8i");
        File.WriteAllText(input, $"[Архив]\nID={Group}\nFolder=/\n[Старые]\nID={Child}\nFolder=/Архив\n" +
            $"[База]\nID={Base}\nConnect=Srvr=\"server\";Ref=\"base\";\nFolder=/Архив\n");
        return LegacyImport.Preview(local ?? new Library(), input, folder, options ?? new());
    }
}
