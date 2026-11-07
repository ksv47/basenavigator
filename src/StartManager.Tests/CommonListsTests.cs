using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Общие списки баз»: the common lists of 1CEStart, read for the tree and never written.
public sealed class CommonListsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-common-" + Guid.NewGuid().ToString("N"));
    public CommonListsTests() => Directory.CreateDirectory(folder);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, true);
    }

    private string Write(string name, string text, Encoding? encoding = null)
    {
        var path = Path.GetFullPath(Path.Combine(folder, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(true)); return path;
    }

    private static string Record(string name, Guid? id, string group = "", string location = "C:\\b") =>
        $"[{name}]\r\nConnect=File=\"{location}\";\r\n" + (id is { } value ? $"ID={value}\r\n" : "") + $"Folder=/{group}\r\n";

    [Fact] public void ListsComeFromBothStartSettingsAndCommonCfgLocationInOrderWithoutRepeats()
    {
        var first = Write("a.v8i", ""); var second = Write("lists/b.v8i", ""); var third = Write("c.v8i", "");
        var user = Write("user/1cestart.cfg", $"CommonInfoBases={first}\r\nCommonInfoBases=\r\nCommonCfgLocation={Path.Combine(folder, "shared")}\r\n", Encoding.Unicode);
        // The common file names a list relative to its own folder, in quotes, and repeats the first one.
        var common = Write("common/1cestart.cfg", $"CommonInfoBases=\"..{Path.DirectorySeparatorChar}lists{Path.DirectorySeparatorChar}b.v8i\"\r\nCommonInfoBases={first.ToUpperInvariant()}\r\n");
        // CommonCfgLocation names a folder; its file refers back to the user's file: the loop ends on the repeat.
        Write("shared/1cestart.cfg", $"CommonInfoBases={third}\r\nCommonCfgLocation={user}\r\n");
        var found = CommonLists.Discover([user, common, Path.Combine(folder, "missing.cfg")]);
        var expected = new List<string> { first, second };
        if (!OperatingSystem.IsWindows()) expected.Add(first.ToUpperInvariant()); // Linux names differing in case are other files.
        expected.Add(third);
        Assert.Equal(expected, found);
    }

    // The open list is filtered when the tree is built (it may be switched or edited later), so the load keeps its IDs.
    [Fact] public void TheFirstListOfAnIdWinsAndNothingIsWritten()
    {
        var own = Guid.NewGuid(); var shared = Guid.NewGuid(); var second = Guid.NewGuid();
        var first = Write("first.v8i", Record("Как в открытом", own) + Record("Общая", shared, "Отдел/Склад"));
        var other = Write("second.v8i", Record("Повтор", shared) + Record("Вторая", second));
        var bytes = File.ReadAllBytes(first);
        var load = CommonLists.Load([first, other], ignoreReadOnly: false);
        Assert.Equal([("Как в открытом", first), ("Общая", first), ("Вторая", other)], load.Bases.Select(x => (x.Entry.Name, x.ListPath)));
        Assert.Equal("Отдел/Склад", load.Bases[1].Entry.Group);
        Assert.All(load.Bases, x => Assert.True(x.PersistentId));
        Assert.Equal([(first, 2, (string?)null), (other, 1, null)], load.Lists.Select(x => (x.Path, x.Bases, x.Problem)));
        Assert.Equal(bytes, File.ReadAllBytes(first)); // read only: not even a missing ID is written
    }

    [Fact] public void UnreadableOrMissingReferencedSettingsAreReported()
    {
        var offline = Path.Combine(folder, "offline", "1cestart.cfg");
        var user = Write("user/1cestart.cfg", $"CommonInfoBases=a.v8i\r\nCommonCfgLocation={offline}\r\n");
        var unreadable = new List<string>();
        Assert.Equal([Path.Combine(folder, "user", "a.v8i")], CommonLists.Discover([user, Path.Combine(folder, "absent.cfg")], unreadable));
        Assert.Equal([offline], unreadable); // the standard file that does not exist is simply absent
        // A missing standard file that a CommonCfgLocation names as well is not simply absent.
        var missing = Path.Combine(folder, "standard", "1cestart.cfg");
        var referring = Write("referring/1cestart.cfg", $"CommonCfgLocation={missing}\r\n");
        unreadable.Clear();
        CommonLists.Discover([referring, missing], unreadable);
        Assert.Equal([missing], unreadable);
    }

    [Fact] public void CopyNeverReplacesACommonListAlsoWhenTheSettingsCannotBeRead()
    {
        var common = Write("lists/c.v8i", Record("Общая", Guid.NewGuid()));
        var loaded = Write("lists/loaded.v8i", Record("Загружена раньше", Guid.NewGuid()));
        var cfg = Write("user/1cestart.cfg", $"CommonInfoBases={common}\r\n");
        var guarded = CommonLists.GuardedFromCopy([loaded], [cfg]);
        Assert.Equal([loaded, common], guarded);
        var before = File.ReadAllBytes(common);
        Assert.Throws<ArgumentException>(() => ListMaintenance.Export(common, [1, 2, 3], guarded));
        // The settings are held by another program: the copy is refused, the common list stays as it was.
        using (new FileStream(cfg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => ListMaintenance.Export(common, [1, 2, 3], CommonLists.GuardedFromCopy([], [cfg])));
        Assert.Equal(before, File.ReadAllBytes(common));
    }

    [Fact] public void OrphanKnowledgeFollowsTheSameSettingsAsTheTree()
    {
        var id = Guid.NewGuid();
        Write("shared/lists/c.v8i", Record("Общая", id));
        Write("shared/1cestart.cfg", "CommonInfoBases=\"lists/c.v8i\"\r\n");
        var user = Write("user/1cestart.cfg", $"CommonCfgLocation={Path.Combine(folder, "shared")}\r\n");
        var roots = new BaseCacheRoots(Path.Combine(folder, "Local"), Path.Combine(folder, "Roaming"));
        var known = OrphanCache.KnownIds(new Library(), [], [user], roots);
        Assert.True(known.Complete);
        Assert.Contains(id, known.Ids);
        // An offline settings file referenced by CommonCfgLocation: nothing may be removed.
        var offline = Write("user2/1cestart.cfg", $"CommonCfgLocation={Path.Combine(folder, "nowhere", "1cestart.cfg")}\r\n");
        Assert.False(OrphanCache.KnownIds(new Library(), [], [offline], roots).Complete);
    }

    [Fact] public void RecordWithoutIdHasAComputedOneThatIsNotPersistent()
    {
        var list = Write("noid.v8i", Record("Без ID", null));
        var load = CommonLists.Load([list], false);
        var entry = Assert.Single(load.Bases);
        Assert.False(entry.PersistentId);
        Assert.Equal(entry.Entry.Id, CommonLists.Load([list], false).Bases[0].Entry.Id);
        Assert.False(File.Exists(list + ".bak"));
    }

    [Fact] public void MissingDamagedAndReadOnlyListsAreReportedAndSkipped()
    {
        var duplicate = Guid.NewGuid();
        var damaged = Write("damaged.v8i", Record("А", duplicate) + Record("Б", duplicate));
        var locked = Write("locked.v8i", Record("Только чтение", Guid.NewGuid()));
        File.SetAttributes(locked, File.GetAttributes(locked) | FileAttributes.ReadOnly);
        var missing = Path.Combine(folder, "missing.v8i");
        var load = CommonLists.Load([missing, damaged, locked], ignoreReadOnly: true);
        Assert.Empty(load.Bases);
        Assert.Equal(3, load.Lists.Count);
        Assert.All(load.Lists, x => Assert.NotNull(x.Problem));
        Assert.Contains("не найден", load.Lists[0].Problem);
        Assert.Contains("не прочитан", load.Lists[1].Problem);
        Assert.Contains("только чтение", load.Lists[2].Problem);
        // Without the setting a read-only list is read like any other.
        Assert.Equal("Только чтение", Assert.Single(CommonLists.Load([locked], ignoreReadOnly: false).Bases).Entry.Name);
    }

    [Fact] public void SettingsAreKeptInTheProfile()
    {
        var store = new LibraryStore(Path.Combine(folder, "settings.json"));
        store.Save(new Library { UseCommonLists = true, SkipReadOnlyCommonLists = true });
        var loaded = store.Load();
        Assert.True(loaded.UseCommonLists);
        Assert.True(loaded.SkipReadOnlyCommonLists);
        Assert.False(new Library().UseCommonLists); // off by default: the tree stays as it was
    }

    // The line under the properties of a base: the file of its list, as the original's FV8BasesFile.
    [Fact] public void PropertiesShowTheListFileTheBaseBelongsTo()
    {
        var open = Path.Combine(folder, "open.v8i");
        var common = new CommonBase(new BaseEntry { Name = "Nordwind" }, Path.Combine(folder, "common.v8i"), true);
        // A common base names its own list, also when a .v8i is open; a base of the open .v8i and a new one name that file.
        Assert.Equal(common.ListPath, CommonLists.ListFileOf(common, open));
        Assert.Equal(common.ListPath, CommonLists.ListFileOf(common, null));
        Assert.Equal(open, CommonLists.ListFileOf(null, open));
        // The local list of the profile is no .v8i: no line.
        Assert.Null(CommonLists.ListFileOf(null, null));
        Assert.Null(CommonLists.ListFileOf(null, ""));
    }
}
