using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>«Каталог» of a group (FormGroupProp.edtFolder): a local setting of the profile, kept by its list, following the group.</summary>
public sealed class GroupFolderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "group-folder-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "library.json");
    private static readonly string Root = OperatingSystem.IsWindows() ? @"D:\Базы" : "/srv/bases";
    public GroupFolderTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private string List(string name, string folder)
    {
        var path = Path.Combine(directory, name + ".v8i");
        File.WriteAllText(path, $"[{name}]\nConnect=Srvr=\"server\";Ref=\"base\";\nID={Guid.NewGuid()}\nFolder=/{folder}\n");
        return path;
    }

    private static Library Sample() => new()
    {
        Bases = [new BaseEntry { Name = "Бух", Kind = ConnectionKind.File, Location = Path.Combine(Root, "Бух"), Group = "Проекты/Клиенты" }],
        Groups = ["Архив"]
    };

    [Fact]
    public void FolderIsSetReplacedAndClearedKeepingUnknownFields()
    {
        var library = Groups.WithFolder(Sample(), "Проекты", Path.Combine(Root, "Проекты"));
        Assert.Equal(Path.Combine(Root, "Проекты"), Groups.Folder(library, "проекты"));
        var withExtra = library with { GroupFolders = [library.GroupFolders[0] with { Extra = new() { ["future"] = System.Text.Json.JsonSerializer.SerializeToElement(1) } }] };
        var replaced = Groups.WithFolder(withExtra, "ПРОЕКТЫ", Path.Combine(Root, "Другой"));
        Assert.Equal(Path.Combine(Root, "Другой"), Assert.Single(replaced.GroupFolders).Folder);
        Assert.NotNull(replaced.GroupFolders[0].Extra);
        Assert.Empty(Groups.WithFolder(replaced, "Проекты", "  ").GroupFolders);
    }

    [Fact]
    public void RelativeOrControlFolderIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Groups.WithFolder(Sample(), "Архив", Path.Combine("relative", "folder")));
        Assert.Throws<ArgumentException>(() => Groups.WithFolder(Sample(), "Архив", Root + "\u0001x"));
    }

    [Fact]
    public void NewBaseGetsTheFolderOfItsGroupOrItsParent()
    {
        var library = Groups.WithFolder(Sample(), "Проекты", Path.Combine(Root, "Проекты"));
        Assert.Equal(Path.Combine(Root, "Проекты"), Groups.InheritedFolder(library, "Проекты/Клиенты"));
        // As the original's GetParentFolder: one level up only (Grok audit 13.10).
        Assert.Equal("", Groups.InheritedFolder(library, "Проекты/Клиенты/Москва"));
        Assert.Equal("", Groups.Folder(library, "Проекты/Клиенты"));
        Assert.Equal("", Groups.InheritedFolder(library, "Архив"));
        Assert.Equal("", Groups.InheritedFolder(library, ""));
        library = Groups.WithFolder(library, "Проекты/Клиенты", Path.Combine(Root, "Клиенты"));
        Assert.Equal(Path.Combine(Root, "Клиенты"), Groups.InheritedFolder(library, "Проекты/Клиенты"));
    }

    [Fact]
    public void RenameMovesTheFoldersOfTheGroupAndItsSubgroups()
    {
        var library = Groups.WithFolder(Groups.WithFolder(Sample(), "Проекты", Path.Combine(Root, "П")), "Проекты/Клиенты", Path.Combine(Root, "К"));
        library = Groups.WithFolder(library, "Архив", Path.Combine(Root, "А"));
        var renamed = Groups.Rename(library, "Проекты", "Работа/Проекты");
        Assert.Equal(Path.Combine(Root, "П"), Groups.Folder(renamed, "Работа/Проекты"));
        Assert.Equal(Path.Combine(Root, "К"), Groups.Folder(renamed, "Работа/Проекты/Клиенты"));
        Assert.Equal(Path.Combine(Root, "А"), Groups.Folder(renamed, "Архив"));
        Assert.Equal("", Groups.Folder(renamed, "Проекты"));
    }

    [Fact]
    public void RemovingAGroupDropsItsFolderOnly()
    {
        var library = Groups.WithFolder(Groups.WithFolder(Sample(), "Архив", Path.Combine(Root, "А")), "Проекты", Path.Combine(Root, "П"));
        var removed = Groups.RemoveEmpty(library, "Архив");
        Assert.Equal("", Groups.Folder(removed, "Архив"));
        Assert.Equal(Path.Combine(Root, "П"), Groups.Folder(removed, "Проекты"));
        // A group made again with the old name does not get the old folder back.
        Assert.Equal("", Groups.Folder(Groups.Rename(removed, "", "Архив"), "Архив"));
    }

    [Fact]
    public void FormattingTheListKeepsTheFoldersWithTheirGroups()
    {
        var library = Groups.WithFolder(Sample() with { Groups = ["архив"] }, "архив", Path.Combine(Root, "А"));
        var plan = ListFormatting.Plan(library, new NameFormat(NameCase.Upper));
        Assert.Equal(Path.Combine(Root, "А"), Groups.Folder(plan.Library, "АРХИВ"));
        Assert.Equal("АРХИВ", Assert.Single(plan.Library.GroupFolders).Group);
    }

    [Fact]
    public void FoldersBelongToTheirListAndSurviveSwitchReloadAndSave()
    {
        var first = List("Первый", "Проекты");
        var second = List("Второй", "Проекты");
        var store = new LibraryStore(Settings);
        var listStore = new V8iStore(first);
        var library = listStore.Load(LibrarySources.Select(new Library(), Settings, first));
        var before = File.ReadAllBytes(first);
        library = listStore.Save(Groups.WithFolder(library, "Проекты", Path.Combine(Root, "Первый")));
        store.Save(library);
        // The group was known only from its base: it gets its own record, whose ID keeps the folder (Grok audit 13.10).
        Assert.NotEqual(before, File.ReadAllBytes(first));
        var id = Assert.Single(library.GroupFolders).Id;
        Assert.NotNull(id);
        Assert.Contains("[Проекты]", File.ReadAllText(first));
        Assert.Contains("ID=" + id, File.ReadAllText(first));
        // Saved again, nothing changes in the file.
        var once = File.ReadAllBytes(first);
        library = listStore.Save(library);
        Assert.Equal(once, File.ReadAllBytes(first));
        library = store.Load();
        Assert.Equal(Path.Combine(Root, "Первый"), Groups.Folder(library, "Проекты"));
        // Another list has its own groups and folders.
        var other = new V8iStore(second).Load(LibrarySources.Select(library, Settings, second));
        Assert.Equal("", Groups.Folder(other, "Проекты"));
        store.Save(other);
        var back = new V8iStore(first).Load(LibrarySources.Select(store.Load(), Settings, first));
        Assert.Equal(Path.Combine(Root, "Первый"), Groups.Folder(back, "Проекты"));
    }

    private const string GroupId = "33333333-3333-3333-3333-333333333333", BaseId = "11111111-1111-1111-1111-111111111111";
    private string ListWithGroupRecord()
    {
        var path = Path.Combine(directory, "ibases.v8i");
        File.WriteAllText(path, "[Бух]\r\nConnect=Srvr=\"srv\";Ref=\"acc\";\r\nID=" + BaseId + "\r\nFolder=/\r\n[Работа]\r\nID=" + GroupId + "\r\nFolder=/\r\n");
        return path;
    }
    private static void External(string path, string from, string to) => File.WriteAllText(path, File.ReadAllText(path).Replace(from, to));

    // Codex audit 13.10, P2-3: the folder is kept by the ID of the group record, as the original's Directory.
    [Fact]
    public void FolderFollowsAGroupRenamedByAnotherProgram()
    {
        var path = ListWithGroupRecord();
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(store.Load(new()), "Работа", Path.Combine(Root, "Р")));
        Assert.Equal(Guid.Parse(GroupId), Assert.Single(library.GroupFolders).Id);
        External(path, "[Работа]", "[Офис]");
        var reloaded = new V8iStore(path).Load(library);
        Assert.Equal(Path.Combine(Root, "Р"), Groups.Folder(reloaded, "Офис"));
        Assert.Equal("", Groups.Folder(reloaded, "Работа"));
    }

    [Fact]
    public void AGroupMadeAgainWithTheSameNameDoesNotGetTheFolder()
    {
        var path = ListWithGroupRecord();
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(store.Load(new()), "Работа", Path.Combine(Root, "Р")));
        External(path, GroupId, Guid.NewGuid().ToString());
        var reloaded = new V8iStore(path).Load(library);
        Assert.Equal("", Groups.Folder(reloaded, "Работа"));
        Assert.Empty(reloaded.GroupFolders);
        // Removed by another program: the folder goes with it.
        var removed = new V8iStore(ListWithGroupRecord()).Load(library);
        External(path, "[Работа]\r\nID=" + GroupId + "\r\nFolder=/\r\n", "");
        Assert.Empty(new V8iStore(path).Load(removed).GroupFolders);
    }

    [Fact]
    public void ANewGroupRecordGivesItsIdToTheFolder()
    {
        var path = ListWithGroupRecord();
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(Groups.Rename(store.Load(new()), "", "Новая"), "Новая", Path.Combine(Root, "Н")));
        var id = Assert.Single(library.GroupFolders).Id;
        Assert.NotNull(id);
        Assert.Contains("ID=" + id, File.ReadAllText(path));
    }

    [Fact]
    public void FolderFollowsTheirRenameWhenThisSaveGoesOverIt()
    {
        var path = ListWithGroupRecord();
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(store.Load(new()), "Работа", Path.Combine(Root, "Р")));
        External(path, "[Работа]", "[Офис]");
        var next = library with { Bases = [library.Bases[0] with { Name = "Бухгалтерия" }] };
        Assert.Throws<ListChangedException>(() => store.Save(next));
        var (_, saved) = store.SaveOverExternalChange(next);
        Assert.Equal(Path.Combine(Root, "Р"), Groups.Folder(saved, "Офис"));
        Assert.Single(saved.GroupFolders);
    }

    // Codex re-check 13.10: two group records with one ID; the folder stays with its path through saves and reloads.
    [Fact]
    public void ASharedRecordIdDoesNotLoseTheFolder()
    {
        var path = Path.Combine(directory, "ibases.v8i");
        File.WriteAllText(path, "[A]\r\nID=" + GroupId + "\r\nFolder=/\r\n[B]\r\nID=" + GroupId + "\r\nFolder=/\r\n");
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(store.Load(new()), "A", Path.Combine(Root, "A")));
        Assert.Null(Assert.Single(library.GroupFolders).Id);
        library = store.Save(library);
        var reloaded = new V8iStore(path).Load(library);
        Assert.Equal(Path.Combine(Root, "A"), Groups.Folder(reloaded, "A"));
        Assert.Equal("", Groups.Folder(reloaded, "B"));
        // A folder that had the ID before another record took it too keeps to its path as well.
        var bound = new Library { Groups = ["A"], GroupFolders = [new GroupFolder { Group = "A", Folder = Path.Combine(Root, "A"), Id = Guid.Parse(GroupId) }] };
        var kept = new V8iStore(path).Load(bound);
        Assert.Equal(Path.Combine(Root, "A"), Groups.Folder(kept, "A"));
        Assert.Null(Assert.Single(kept.GroupFolders).Id);
    }

    // Grok audit 13.10, P2-1: a group known only from Folder= of its bases keeps its folder through a rename by another program.
    [Fact]
    public void AGroupOfBasesOnlyKeepsItsFolderThroughTheirRename()
    {
        var path = List("Бух", "Проекты");
        var store = new V8iStore(path);
        var library = store.Save(Groups.WithFolder(store.Load(new()), "Проекты", Path.Combine(Root, "П")));
        File.WriteAllText(path, File.ReadAllText(path).Replace("[Проекты]", "[Офис]").Replace("Folder=/Проекты", "Folder=/Офис"));
        var reloaded = new V8iStore(path).Load(library);
        Assert.Equal(Path.Combine(Root, "П"), Groups.Folder(reloaded, "Офис"));
    }

    // Codex re-check 13.10: a folder kept by its path from before (e.g. set while the list was read-only) does not make an ordinary
    // save or a save over another program's change add the group record; changing the folder does.
    [Fact]
    public void AnUnchangedPathFolderDoesNotRewriteTheList()
    {
        var path = List("Бух", "Проекты");
        var old = new Library { GroupFolders = [new GroupFolder { Group = "Проекты", Folder = Path.Combine(Root, "П") }] };
        var store = new V8iStore(path);
        var library = store.Load(old);
        var before = File.ReadAllBytes(path);
        library = store.Save(library with { Bases = [library.Bases[0] with { Comment = "локально" }] });
        Assert.Equal(before, File.ReadAllBytes(path));
        File.AppendAllText(path, "[Другая]\nConnect=Srvr=\"s\";Ref=\"r\";\nID=" + Guid.NewGuid() + "\nFolder=/\n");
        var next = library with { Bases = [library.Bases[0] with { Comment = "ещё" }] };
        Assert.Throws<ListChangedException>(() => store.Save(next));
        var (fresh, saved) = store.SaveOverExternalChange(next);
        Assert.DoesNotContain("[Проекты]", File.ReadAllText(path));
        Assert.Null(Assert.Single(saved.GroupFolders).Id);
        // The folder changed now: the record is made.
        fresh.Save(Groups.WithFolder(saved, "Проекты", Path.Combine(Root, "Новый")));
        Assert.Contains("[Проекты]", File.ReadAllText(path));
    }

    // P2-2: on a read-only list the folder is still a local setting: saved, the file untouched, no record made.
    [Fact]
    public void ReadOnlyListKeepsTheFolderWithoutWriting()
    {
        var path = List("Бух", "Проекты");
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var store = new V8iStore(path);
            var library = store.Load(new());
            if (store.ReadOnly is null) return; // a file system without the attribute (root on Linux)
            var saved = store.Save(Groups.WithFolder(library, "Проекты", Path.Combine(Root, "П")));
            Assert.Equal(Path.Combine(Root, "П"), Groups.Folder(saved, "Проекты"));
            Assert.Null(Assert.Single(saved.GroupFolders).Id);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    // P3-1: a folder found by the record's ID keeps the group against one kept only by the path.
    [Fact]
    public void TheFolderOfTheRecordWinsOverAPathOnlyOne()
    {
        var id = Guid.NewGuid();
        var result = Groups.Reconcile([new GroupFolder { Group = "A", Folder = Path.Combine(Root, "ID"), Id = id }, new GroupFolder { Group = "B", Folder = Path.Combine(Root, "Путь") }],
            new Dictionary<string, Guid?> { ["B"] = id }, ["B"]);
        Assert.Equal(Path.Combine(Root, "ID"), Assert.Single(result).Folder);
    }

    // P2-4: damaged folders in the profile are refused as a damaged profile, not left to fail later.
    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Group\":null,\"Folder\":\"x\"}]")]
    [InlineData("[{\"Group\":\"A\",\"Folder\":null}]")]
    public void DamagedFoldersAreRefused(string json)
    {
        File.WriteAllText(Settings, "{\"Version\":1,\"GroupFolders\":" + json + "}");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(Settings).Load());
        File.WriteAllText(Settings, "{\"Version\":1,\"Sources\":{\"local:x\":{\"GroupFolders\":" + json + "}}}");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(Settings).Load());
    }

    // P2-2: a snapshot of a local list keeps the folders; an older one has none.
    [Fact]
    public void LocalSnapshotStateKeepsTheFolders()
    {
        var library = Groups.WithFolder(Sample(), "Архив", Path.Combine(Root, "А"));
        var state = LocalListState.Read(LocalListState.Write(library));
        Assert.Equal(Path.Combine(Root, "А"), Assert.Single(state.GroupFolders!).Folder);
        Assert.Null(LocalListState.Read("{\"Bases\":[],\"Groups\":[]}"u8.ToArray()).GroupFolders);
        // Restored over a list without the group: its folder is not kept.
        Assert.Empty(Groups.Existing(library.GroupFolders, library with { Groups = [] }));
    }
}
