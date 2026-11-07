using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Отсутствующие конфигурации»: cache folders named by an ID no known base has.
public sealed class OrphanCacheTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-orphan-" + Guid.NewGuid().ToString("N"));
    private BaseCacheRoots Roots => new(Path.Combine(folder, "Local", "1cv8"), Path.Combine(folder, "Roaming", "1cv8"));
    public OrphanCacheTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private string Cache(string root, string name, int bytes = 10)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[bytes]); return path;
    }

    [Fact] public void OnlyUnknownGuidFoldersAreReported()
    {
        var inList = Guid.NewGuid(); var inStandard = Guid.NewGuid(); var inCommon = Guid.NewGuid(); var mapped = Guid.NewGuid();
        var inSource = Guid.NewGuid(); var orphan = Guid.NewGuid(); var old = Guid.NewGuid();
        foreach (var id in new[] { inList, inStandard, inCommon, mapped, inSource, orphan })
            Cache(Roots.Program, id.ToString("D"));
        Cache(Roots.User, orphan.ToString("D"), 5);
        Cache(Path.Combine(Roots.Program, "8.2.19"), old.ToString("D"));
        Cache(Roots.Program, "Srvr__host__Ref__base__");
        Cache(Roots.Program, "not-a-guid");
        var standard = Path.Combine(folder, "ibases.v8i");
        File.WriteAllText(standard, $"[A]\r\nConnect=File=\"C:\\a\";\r\nID={inStandard}\r\n", new UTF8Encoding(true));
        var common = Path.Combine(folder, "common.v8i");
        File.WriteAllText(common, $"[B]\r\nID={inCommon}\r\n");
        var config = Path.Combine(folder, "1cestart.cfg");
        File.WriteAllText(config, $"DefaultVersion=8.3\r\nCommonInfoBases={common}\r\n", Encoding.Unicode);
        File.WriteAllText(Path.Combine(Roots.User, "1cv8u.pfl"), $"{{\"S\",\"File=\"\"C:\\\\x\"\";\"}},{{\"S\",\"{mapped}\"}}");
        var library = new Library
        {
            Bases = [new BaseEntry { Id = inList, Name = "L", Kind = ConnectionKind.File, Location = @"C:\l" }],
            Sources = new() { ["other"] = new SourceSettings { Bases = [new BaseEntry { Id = inSource, Name = "S", Kind = ConnectionKind.File, Location = @"C:\s" }] } }
        };
        var known = OrphanCache.KnownIds(library, [standard], [config], Roots);
        var found = OrphanCache.Find(known, Roots);
        Assert.Equal(new[] { orphan, old }.Order(), found.Select(x => x.Id).Order());
        var entry = found.Single(x => x.Id == orphan);
        Assert.Single(entry.ProgramFolders); Assert.Single(entry.UserFolders); Assert.Equal(15, entry.Size);
        Assert.NotNull(entry.Modified);
        Assert.Contains(Path.Combine(Roots.Program, "8.2.19", old.ToString("D")), found.Single(x => x.Id == old).ProgramFolders);
    }

    [Fact] public void RememberedListsAreReadAgain()
    {
        var other = Path.Combine(folder, "other.v8i"); var added = Guid.NewGuid();
        File.WriteAllText(other, $"[New]\r\nConnect=File=\"C:\\n\";\r\nID={added}\r\n");
        Cache(Roots.Program, added.ToString("D"));
        // The profile remembers the list from before the base was added there.
        var library = new Library { Sources = new() { [ActivityStore.Source(Path.Combine(folder, "library.json"), other)] = new SourceSettings() } };
        var known = OrphanCache.KnownIds(library, [], [], Roots);
        Assert.True(known.Complete); Assert.Contains(added, known.Ids);
        Assert.Empty(OrphanCache.Find(known, Roots));
    }

    [Fact] public void AnUnreadableSourceMakesTheResultIncomplete()
    {
        var list = Path.Combine(folder, "locked.v8i"); File.WriteAllText(list, $"[A]\r\nID={Guid.NewGuid()}\r\n");
        using (new FileStream(list, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var known = OrphanCache.KnownIds(new Library(), [list], [], Roots);
            if (OperatingSystem.IsWindows()) { Assert.False(known.Complete); Assert.Contains(list, known.Unavailable); }
        }
        // A common list on a share that cannot be reached: its folder is not there.
        var config = Path.Combine(folder, "1cestart.cfg");
        var unreachable = Path.Combine(folder, "offline-share", "ibases.v8i");
        File.WriteAllText(config, $"CommonInfoBases={unreachable}\r\n", Encoding.Unicode);
        var withShare = OrphanCache.KnownIds(new Library(), [], [config], Roots);
        Assert.False(withShare.Complete); Assert.Contains(unreachable, withShare.Unavailable);
        // A list that is simply absent in an existing folder proves nothing and does not block.
        var absent = OrphanCache.KnownIds(new Library(), [Path.Combine(folder, "absent.v8i")], [], Roots);
        Assert.True(absent.Complete);
    }

    [Fact] public void AListInAFolderClosedForThisUserIsUnavailable()
    {
        var closed = Path.Combine(folder, "closed"); Directory.CreateDirectory(closed);
        var list = Path.Combine(closed, "ibases.v8i"); var id = Guid.NewGuid();
        File.WriteAllText(list, $"[A]\r\nID={id}\r\n");
        void Close(bool deny)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(closed); var acl = info.GetAccessControl();
                var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
                if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
                info.SetAccessControl(acl);
            }
            else File.SetUnixFileMode(closed, deny ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Close(true);
        try
        {
            bool readable;
            try { File.ReadAllText(list); readable = true; } catch (UnauthorizedAccessException) { readable = false; }
            // root and administrators may still read it; then the ID is simply known.
            var known = OrphanCache.KnownIds(new Library(), [list], [], Roots);
            if (readable) Assert.Contains(id, known.Ids);
            else { Assert.False(known.Complete); Assert.Contains(list, known.Unavailable); }
        }
        finally { Close(false); }
    }

    // Linux: cache and settings in one folder. The orphan's folder goes whole, so its size counts the settings too.
    [Fact] public void SharedRootsCountTheSettingsInTheSize()
    {
        var shared = new BaseCacheRoots(Path.Combine(folder, "1cv8"), Path.Combine(folder, "1cv8"));
        var orphan = Cache(shared.Program, Guid.NewGuid().ToString("D"), 10);
        File.WriteAllBytes(Path.Combine(orphan, "def.pfl"), new byte[100]);
        var found = Assert.Single(OrphanCache.Find([], shared));
        Assert.Empty(found.UserFolders);
        Assert.Equal(110, found.Size);
    }

    // Д1 of the Linux run: the question promises the settings go too; the folder was emptied down to *.pfl and stayed, and the row
    // came back after a restart. Now the folder goes whole; the cache of a known base in the same root keeps its settings.
    [Fact] public void SharedRootsRemoveTheOrphanFolderWhole()
    {
        var shared = new BaseCacheRoots(Path.Combine(folder, "home", ".1cv8", "1C", "1cv8"), Path.Combine(folder, "home", ".1cv8", "1C", "1cv8"));
        var orphan = Cache(shared.Program, Guid.NewGuid().ToString("D"));
        File.WriteAllText(Path.Combine(orphan, "def.usr.pfl"), "{settings}");
        File.WriteAllText(Path.Combine(orphan, "lognhist.reg"), "x");
        Directory.CreateDirectory(Path.Combine(orphan, "071523a4"));
        File.WriteAllText(Path.Combine(orphan, "071523a4", "1cv8c.pfl"), "x");
        var known = Cache(shared.Program, Guid.NewGuid().ToString("D"));
        File.WriteAllText(Path.Combine(known, "def.usr.pfl"), "{mine}");
        var found = Assert.Single(OrphanCache.Find([Guid.Parse(Path.GetFileName(known))], shared));

        var result = OrphanCache.Clear([found], shared);

        Assert.Equal(1, result.Removed); Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(orphan));
        Assert.Empty(OrphanCache.Find([Guid.Parse(Path.GetFileName(known))], shared));
        Assert.True(File.Exists(Path.Combine(known, "data.bin")));
        // The cache clearing of an existing base is unchanged: there the user's settings stay.
        Assert.Equal(1, BaseCache.Clear([known], BaseCacheKind.Program, shared).Removed);
        Assert.True(File.Exists(Path.Combine(known, "def.usr.pfl")));
        Assert.False(File.Exists(Path.Combine(known, "data.bin")));
    }

    // Windows: Local and Roaming folders of the orphan both go; the count is the folders really removed.
    [Fact] public void SeparateRootsRemoveCacheAndSettingsFolders()
    {
        var id = Guid.NewGuid().ToString("D");
        var local = Cache(Roots.Program, id); var roaming = Cache(Roots.User, id);
        File.WriteAllText(Path.Combine(roaming, "def.usr.pfl"), "x");
        var gone = Cache(Roots.Program, Guid.NewGuid().ToString("D"));
        var found = OrphanCache.Find([], Roots);
        Directory.Delete(gone, true); // removed by someone else meanwhile: not counted, not failed

        var result = OrphanCache.Clear(found, Roots);

        Assert.Equal(2, result.Removed); Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(local)); Assert.False(Directory.Exists(roaming));
        Assert.Equal(0, OrphanCache.Clear([], Roots).Removed);
    }

    [Fact] public void LinksAreNeverReported()
    {
        var target = Path.Combine(folder, "elsewhere"); Directory.CreateDirectory(target);
        var link = Path.Combine(Roots.Program, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(Roots.Program);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // Windows without the right to create links.
        try { Assert.Empty(OrphanCache.Find([], Roots)); }
        finally { Directory.Delete(link); }
    }

    [Fact] public void CommonListsAreReadFromTheStartSettings()
    {
        var config = Path.Combine(folder, "1cestart.cfg");
        File.WriteAllText(config, "CommonInfoBases=\\\\srv\\lists\\ibases.v8i\r\nCommonInfoBases=D:\\l.v8i\r\nOther=x\r\n", Encoding.Unicode);
        Assert.Equal([@"\\srv\lists\ibases.v8i", @"D:\l.v8i"], OrphanCache.CommonLists([config, Path.Combine(folder, "missing.cfg")]));
    }

    // Codex re-check 5, RC5-2: window A holds an old snapshot of the profile; window B saves a new external source, or a new base of
    // the local list, with G. Deleting through A reads the profile again: G's whole folder and settings stay.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProfileSavedByAnotherWindowKeepsItsConfigurations(bool externalSource)
    {
        var shared = new BaseCacheRoots(Path.Combine(folder, "home", "1cv8"), Path.Combine(folder, "home", "1cv8"));
        var profile = Path.Combine(folder, "profile", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        var a = new LibraryStore(profile);
        a.Save(new Library());
        var snapshotA = a.Load();
        var g = Guid.NewGuid();
        var orphan = Cache(shared.Program, g.ToString("D"));
        File.WriteAllText(Path.Combine(orphan, "def.usr.pfl"), "{settings}");
        var chosen = OrphanCache.Find(OrphanCache.KnownIds(snapshotA, [], [], shared, profile), shared);
        Assert.Contains(chosen, x => x.Id == g);

        var b = new LibraryStore(profile);
        var snapshotB = b.Load();
        if (externalSource)
        {
            var list = Path.Combine(folder, "other.v8i");
            File.WriteAllText(list, $"[Новая]\r\nConnect=File=\"C:\n\";\r\nID={g}\r\n");
            b.Save(snapshotB with { Sources = new(snapshotB.Sources) { [ActivityStore.Source(profile, list)] = new SourceSettings() } });
        }
        else b.Save(snapshotB with { Bases = [.. snapshotB.Bases, new BaseEntry { Id = g, Name = "Новая", Kind = ConnectionKind.File, Location = @"C:\n" }] });

        // As ClearOrphans after the confirmation: the window's snapshot and the profile read again.
        var known = OrphanCache.KnownIds(snapshotA, [], [], shared, profile);
        Assert.True(known.Complete);
        Assert.Contains(g, known.Ids);
        var result = OrphanCache.Clear(chosen.Where(x => !known.Ids.Contains(x.Id)), shared);
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(Path.Combine(orphan, "def.usr.pfl")));
        Assert.True(File.Exists(Path.Combine(orphan, "data.bin")));
    }

    [Fact] public void AnUnreadableProfileRefusesTheDeletion()
    {
        var profile = Path.Combine(folder, "library.json");
        File.WriteAllText(profile, "{ not json");
        var known = OrphanCache.KnownIds(new Library(), [], [], Roots, profile);
        Assert.False(known.Complete);
        Assert.Equal([profile], known.Unavailable);
        // No profile yet (a new one) is not a refusal.
        Assert.True(OrphanCache.KnownIds(new Library(), [], [], Roots, Path.Combine(folder, "none", "library.json")).Complete);
    }

    // Re-check 6, RC6-2: a profile whose folder this user may not enter is not absent: the knowledge is incomplete, nothing goes;
    // with the access back the base saved there by another window is known and the folder stays.
    [Fact] public void AProfileInAClosedFolderRefusesUntilItIsOpenAgain()
    {
        var closed = Path.Combine(folder, "profile-closed"); Directory.CreateDirectory(closed);
        var profile = Path.Combine(closed, "library.json");
        var shared = new BaseCacheRoots(Path.Combine(folder, "home", "1cv8"), Path.Combine(folder, "home", "1cv8"));
        var g = Guid.NewGuid();
        var orphan = Cache(shared.Program, g.ToString("D"));
        new LibraryStore(profile).Save(new Library { Bases = [new BaseEntry { Id = g, Name = "B", Kind = ConnectionKind.File, Location = @"C:\b" }] });
        var chosen = OrphanCache.Find([], shared);
        void Close(bool deny)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(closed); var acl = info.GetAccessControl();
                var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.ListDirectory |
                    System.Security.AccessControl.FileSystemRights.Traverse,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
                if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
                info.SetAccessControl(acl);
            }
            else File.SetUnixFileMode(closed, deny ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Close(true);
        try
        {
            bool readable;
            try { File.ReadAllBytes(profile); readable = true; } catch (UnauthorizedAccessException) { readable = false; }
            if (!readable)
            {
                // What File.Exists says here is no proof of absence.
                var known = OrphanCache.KnownIds(new Library(), [], [], shared, profile);
                Assert.False(known.Complete); Assert.Equal([profile], known.Unavailable);
                Assert.True(Directory.Exists(orphan));
            }
        }
        finally { Close(false); }
        var open = OrphanCache.KnownIds(new Library(), [], [], shared, profile);
        Assert.True(open.Complete); Assert.Contains(g, open.Ids);
        Assert.Equal(0, OrphanCache.Clear(chosen.Where(x => !open.Ids.Contains(x.Id)), shared).Removed);
        Assert.True(Directory.Exists(orphan));
        // A profile that is not there is no refusal.
        Assert.Null(LibraryStore.ReadExisting(Path.Combine(folder, "none", "library.json")));
        var none = OrphanCache.KnownIds(new Library(), [], [], shared, Path.Combine(folder, "none", "library.json"));
        Assert.True(none.Complete);
        Assert.Equal(1, OrphanCache.Clear(chosen.Where(x => !none.Ids.Contains(x.Id)), shared).Removed);
        Assert.False(Directory.Exists(orphan));
    }

    // Re-check 6, RC6-3: the decision and the removal are one step under the profile's lock. B saving while A has decided waits and
    // is refused (nothing of B is lost unseen: its save fails, A removes only what no saved profile names); B saving just before A
    // takes the lock is seen by A, whose decision then keeps G. Both for a new local base and a newly remembered list.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDecisionAndTheRemovalHoldTheProfileLock(bool externalSource)
    {
        var shared = new BaseCacheRoots(Path.Combine(folder, "home", "1cv8"), Path.Combine(folder, "home", "1cv8"));
        var profile = Path.Combine(folder, "profile", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        new LibraryStore(profile).Save(new Library());
        var snapshotA = new LibraryStore(profile).Load();
        var g = Guid.NewGuid();
        var orphan = Cache(shared.Program, g.ToString("D"));
        File.WriteAllText(Path.Combine(orphan, "def.usr.pfl"), "{settings}");
        var list = Path.Combine(folder, "other.v8i");
        File.WriteAllText(list, $"[Новая]\r\nConnect=File=\"C:\n\";\r\nID={g}\r\n");
        Library WithG(Library b) => externalSource
            ? b with { Sources = new(b.Sources) { [ActivityStore.Source(profile, list)] = new SourceSettings() } }
            : b with { Bases = [.. b.Bases, new BaseEntry { Id = g, Name = "Новая", Kind = ConnectionKind.File, Location = @"C:\n" }] };
        var chosen = OrphanCache.Find([], shared);

        // 1. B saves while A has decided: B waits for the lock and is refused; the profile stays without G, so A's removal is right.
        var storeB = new LibraryStore(profile); var snapshotB = storeB.Load();
        Exception? refusedB = null;
        var result = OrphanCache.ClearUnknown(chosen, snapshotA, profile, [], [], shared, () =>
        {
            var save = new Thread(() => { try { storeB.Save(WithG(snapshotB)); } catch (Exception ex) { refusedB = ex; } });
            save.Start(); Assert.True(save.Join(TimeSpan.FromSeconds(10)));
        });
        Assert.IsType<IOException>(refusedB);
        Assert.DoesNotContain(g, OrphanCache.KnownIds(new Library(), [], [], shared, profile).Ids);
        Assert.Equal(1, result.Removed); Assert.False(Directory.Exists(orphan));

        // 2. B holds the lock and saves G just as A begins: A waits for it and then sees G — the folder and its settings stay.
        orphan = Cache(shared.Program, g.ToString("D"));
        File.WriteAllText(Path.Combine(orphan, "def.usr.pfl"), "{settings}");
        chosen = OrphanCache.Find([], shared);
        var held = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            using (FilePersistence.Lock(profile))
            {
                held.Set(); Thread.Sleep(400);
                // As Save writes under its lock.
                File.WriteAllText(profile, System.Text.Json.JsonSerializer.Serialize(WithG(new LibraryStore(profile).Load())));
            }
        });
        writer.Start(); Assert.True(held.Wait(5000));
        var kept = OrphanCache.ClearUnknown(chosen, snapshotA, profile, [], [], shared);
        Assert.True(writer.Join(5000));
        Assert.Equal(0, kept.Removed);
        Assert.True(File.Exists(Path.Combine(orphan, "def.usr.pfl")));
    }

    // Re-check 7, RC7-2: no profile folder yet. A makes it and takes the lock before deciding, so B's first save waits for it and
    // is refused while A removes; afterwards B saves normally. Both for a new local base and a newly remembered list.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFirstSaveOfAnotherWindowWaitsForTheDecision(bool externalSource)
    {
        var shared = new BaseCacheRoots(Path.Combine(folder, "home", "1cv8"), Path.Combine(folder, "home", "1cv8"));
        var profile = Path.Combine(folder, "new-profile", "library.json");
        Assert.False(Directory.Exists(Path.GetDirectoryName(profile)));
        var g = Guid.NewGuid();
        var orphan = Cache(shared.Program, g.ToString("D"));
        File.WriteAllText(Path.Combine(orphan, "def.usr.pfl"), "{settings}");
        var list = Path.Combine(folder, "other.v8i");
        File.WriteAllText(list, $"[Новая]\r\nConnect=File=\"C:\\n\";\r\nID={g}\r\n");
        Library WithG(Library b) => externalSource
            ? b with { Sources = new(b.Sources) { [ActivityStore.Source(profile, list)] = new SourceSettings() } }
            : b with { Bases = [.. b.Bases, new BaseEntry { Id = g, Name = "Новая", Kind = ConnectionKind.File, Location = @"C:\n" }] };
        var chosen = OrphanCache.Find([], shared);
        Exception? refusedB = null;
        var result = OrphanCache.ClearUnknown(chosen, new Library(), profile, [], [], shared, () =>
        {
            Assert.True(Directory.Exists(Path.GetDirectoryName(profile)));
            var save = new Thread(() =>
            {
                try { var b = new LibraryStore(profile); b.Save(WithG(b.Load())); }
                catch (Exception ex) { refusedB = ex; }
            });
            save.Start(); Assert.True(save.Join(TimeSpan.FromSeconds(10)));
        });
        // B's first save came while A had decided: refused, nothing of it written, so A's removal is right.
        Assert.IsType<IOException>(refusedB);
        Assert.False(File.Exists(profile));
        Assert.Equal(1, result.Removed); Assert.False(Directory.Exists(orphan));
        // Once A is done, B's save goes through and the next decision knows G.
        var store = new LibraryStore(profile); store.Save(WithG(store.Load()));
        orphan = Cache(shared.Program, g.ToString("D"));
        Assert.Equal(0, OrphanCache.ClearUnknown(OrphanCache.Find([], shared), new Library(), profile, [], [], shared).Removed);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact] public void AProfileFolderThatCannotBeMadeRefuses()
    {
        var blocker = Path.Combine(folder, "file-not-folder"); File.WriteAllText(blocker, "");
        var orphan = Cache(Roots.Program, Guid.NewGuid().ToString("D"));
        var refused = Assert.Throws<IOException>(() => OrphanCache.ClearUnknown(OrphanCache.Find([], Roots), new Library(), Path.Combine(blocker, "library.json"), [], [], Roots));
        Assert.Contains("кэш не удаляется", refused.Message);
        Assert.True(Directory.Exists(orphan));
    }
}
