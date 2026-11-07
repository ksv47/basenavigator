using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// ProfileRelocation.PortableRoot is process-wide; share the non-parallel collection with other static settings.
[Collection("First run list")]
public sealed class RelocationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-move-" + Guid.NewGuid().ToString("N"));
    private readonly string oldRoot, newRoot;
    private static readonly Guid LocalId = Guid.NewGuid(), SharedId = Guid.NewGuid();

    public RelocationTests()
    {
        oldRoot = Path.Combine(folder, "old", "BaseNavigator");
        newRoot = Path.Combine(folder, "moved", "Other place", "BaseNavigator");
        Directory.CreateDirectory(Path.Combine(oldRoot, "Settings"));
    }
    public void Dispose() { ProfileRelocation.PortableRoot = null; PlatformCatalog.UseFolders(new Library()); Directory.Delete(folder, true); }

    private static string Settings(string root) => Path.Combine(root, "Settings", "library.json");
    private static string List(string root) => Path.Combine(root, "Lists", "team.v8i");
    private string ExternalPlatforms => Path.Combine(folder, "external", "1cv8");
    private const string Network = @"\\server\share\lists\common.v8i";
    private static string Client => OperatingSystem.IsWindows() ? @"C:\Program Files\1cv8\8.3.27.2214\bin\1cv8.exe" : "/opt/1cv8/x86_64/8.3.27.2214/1cv8";

    // A profile written by 0.2.0.19 or older: absolute keys and no ProfilePath.
    private void WriteOldProfile(bool withLocalHistory = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(List(oldRoot))!);
        File.WriteAllText(List(oldRoot), $"[Shared base]\nID={SharedId}\nConnect=Srvr=\"server\";Ref=\"shared\";\n");
        var local = ActivityStore.Source(Settings(oldRoot), "");
        var fileBase = new BaseEntry { Id = LocalId, Name = "Local", Kind = ConnectionKind.File, Location = Path.Combine(oldRoot, "Bases", "demo"),
            Executable = Path.Combine(oldRoot, "1cv8", "1cv8.exe") };
        var server = new BaseEntry { Name = "Server", Kind = ConnectionKind.Server, Location = "srv\\db", Executable = Client };
        var profile = new Library
        {
            ListPath = List(oldRoot), Executable = Client, Groups = ["Team"],
            // One search folder inside the program folder and one outside it.
            PlatformFolders = [Path.Combine(oldRoot, "Platforms"), ExternalPlatforms],
            Sources = new()
            {
                [local] = new() { Bases = [fileBase, server] },
                [ActivityStore.Source("", List(oldRoot))] = new() { Groups = ["Team"] },
                [ActivityStore.Source("", Network)] = new() { Groups = ["Network"] }
            },
            Extra = new() { ["future"] = JsonSerializer.SerializeToElement("keep") }
        };
        File.WriteAllText(Settings(oldRoot), JsonSerializer.Serialize(profile));
        var favorites = new List<Favorite> { new(ActivityStore.Source("", List(oldRoot)), SharedId), new(ActivityStore.Source("", Network), SharedId) };
        if (withLocalHistory) favorites.Add(new(local, LocalId));
        File.WriteAllText(Settings(oldRoot) + ".activity.json", JsonSerializer.Serialize(new Activity { Favorites = favorites }));
    }

    private void Move()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(newRoot)!);
        Directory.Move(oldRoot, newRoot);
        ProfileRelocation.PortableRoot = newRoot;
    }

    [Fact] public void MovedPortableFolderKeepsListsFavoritesAndInnerPaths()
    {
        WriteOldProfile(); Move();
        var settings = Settings(newRoot);
        var library = new LibraryStore(settings).Load();
        Assert.Equal(List(newRoot), library.ListPath);
        Assert.Equal(Client, library.Executable);
        // Codex audit P2.1: the search folder inside the moved folder follows it, the outer one stays.
        Assert.Equal([Path.Combine(newRoot, "Platforms"), ExternalPlatforms], library.PlatformFolders);
        Assert.Equal(library.PlatformFolders, PlatformCatalog.UserRoots);
        var local = LibrarySources.Select(library, settings, "");
        Assert.Equal(Path.Combine(newRoot, "Bases", "demo"), local.Bases[0].Location);
        Assert.Equal(Path.Combine(newRoot, "1cv8", "1cv8.exe"), local.Bases[0].Executable);
        Assert.Equal("srv\\db", local.Bases[1].Location); Assert.Equal(Client, local.Bases[1].Executable);
        Assert.Equal(["Team"], LibrarySources.Select(library, settings, List(newRoot)).Groups);
        Assert.Equal(["Network"], LibrarySources.Select(library, settings, Network).Groups);
        Assert.Equal("Shared base", Assert.Single(new V8iStore(library.ListPath).Load(LibrarySources.Select(library, settings, library.ListPath)).Bases).Name);

        var favorites = new ActivityStore(settings).Load().Favorites.Select(x => x.Source).ToHashSet();
        Assert.Contains(ActivityStore.Source(settings, ""), favorites);
        Assert.Contains(ActivityStore.Source("", List(newRoot)), favorites);
        Assert.Contains(ActivityStore.Source("", Network), favorites);
    }

    [Fact] public void ProfileInAHomeFolderDoesNotMoveThePathsAroundIt()
    {
        // A portable Settings folder right in a home folder: the lists and bases next to it did not move with it.
        var home = Path.Combine(folder, "Users", "me"); var moved = Path.Combine(folder, "stick");
        var list = Path.Combine(home, "AppData", "ibases.v8i"); var bases = Path.Combine(home, "Bases", "acc");
        Directory.CreateDirectory(Path.Combine(home, "Settings")); Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        File.WriteAllText(list, "[Base]\nConnect=File=\"x\";\n");
        var profile = new Library { ListPath = list, Sources = new() { [ActivityStore.Source(Settings(home), "")] = new()
            { Bases = [new BaseEntry { Name = "Local", Kind = ConnectionKind.File, Location = bases }] } } };
        File.WriteAllText(Settings(home), JsonSerializer.Serialize(profile));
        Directory.CreateDirectory(Path.Combine(moved, "Settings")); File.Copy(Settings(home), Settings(moved));
        ProfileRelocation.PortableRoot = moved;
        var library = new LibraryStore(Settings(moved)).Load();
        Assert.Equal(list, library.ListPath);
        Assert.Equal(bases, LibrarySources.Select(library, Settings(moved), "").Bases[0].Location);
        Assert.True(ProfileRelocation.Broad(home, moved));
        Assert.True(ProfileRelocation.Broad(Path.Combine(folder, "Home", "me"), moved));
        Assert.True(ProfileRelocation.Broad(Path.GetPathRoot(folder)!));
        Assert.False(ProfileRelocation.Broad(oldRoot, newRoot));
        // A program folder that happens to lie right in Users keeps moving with itself.
        Assert.False(ProfileRelocation.Broad(Path.Combine(folder, "Users", "BaseNavigator"), Path.Combine(folder, "stick", "BaseNavigator")));
    }

    [Fact] public void ProcessingInsidePortableFolderMovesButExternalPathAndParametersStayLiteral()
    {
        var internalPath = Path.Combine(oldRoot, "Tools", "addon.epf");
        var outside = Path.Combine(folder, "external.epf");
        var entry = new BaseEntry { Name = "База", Kind = ConnectionKind.Server, Location = "srv\\db",
            ExternalProcessing = internalPath, AdditionalParameters = "/Out " + LaunchParameters.Quote(internalPath) };
        var localKey = ActivityStore.Source(Settings(oldRoot), "");
        var profile = new Library { ProfilePath = Settings(oldRoot), Bases = [entry],
            Sources = new() { [localKey] = new() { Bases = [entry with { ExternalProcessing = outside }] } } };
        File.WriteAllText(Settings(oldRoot), JsonSerializer.Serialize(profile));
        Move();
        var moved = new LibraryStore(Settings(newRoot)).Load();
        Assert.Equal(Path.Combine(newRoot, "Tools", "addon.epf"), moved.Bases[0].ExternalProcessing);
        Assert.Equal(entry.AdditionalParameters, moved.Bases[0].AdditionalParameters);
        Assert.Equal(outside, moved.Sources[ActivityStore.Source(Settings(newRoot), "")].Bases[0].ExternalProcessing);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void UnchangedInvalidParametersDoNotBlockCompletingAMove(bool activeSharedList)
    {
        WriteOldProfile();
        var old = LibraryStore.ReadStored(Settings(oldRoot))!;
        var entry = new BaseEntry { Id = SharedId, Name = "Shared", Kind = ConnectionKind.Server,
            Location = "srv\\db", AdditionalParameters = "/C \"unfinished" };
        old.Sources[ActivityStore.Source("", List(oldRoot))] = new() { Bases = [entry] };
        old = old with { Bases = activeSharedList ? [entry] : [], ListPath = activeSharedList ? List(oldRoot) : "" };
        File.WriteAllText(Settings(oldRoot), JsonSerializer.Serialize(old));
        Move();
        var store = new LibraryStore(Settings(newRoot));
        var loaded = store.Load();
        Assert.NotNull(loaded.Moved);
        var completed = ProfileRelocation.Complete(loaded, store);
        Assert.Null(completed.Moved);
        store.Save(completed with { AfterLaunch = AfterLaunchAction.Minimize });
        var saved = store.Load();
        Assert.Null(saved.Moved);
        Assert.Equal(entry.AdditionalParameters, saved.Sources[ActivityStore.Source("", List(newRoot))].Bases[0].AdditionalParameters);
        var bytes = File.ReadAllBytes(Settings(newRoot));
        // The same ID and bad text in an unrelated new source must still be rejected.
        saved.Sources[ActivityStore.Source("", Path.Combine(folder, "other.v8i"))] = new() { Bases = [entry] };
        Assert.Throws<ArgumentException>(() => store.Save(saved));
        Assert.Equal(bytes, File.ReadAllBytes(Settings(newRoot)));
    }

    [Fact] public void SavingWritesNewLocationAndKeepsUnknownFields()
    {
        WriteOldProfile(); Move();
        var settings = Settings(newRoot);
        var store = new LibraryStore(settings);
        store.Save(store.Load());
        var saved = JsonDocument.Parse(File.ReadAllText(settings)).RootElement;
        Assert.Equal(Path.GetFullPath(settings), saved.GetProperty("ProfilePath").GetString());
        Assert.Equal("keep", saved.GetProperty("future").GetString());
        Assert.DoesNotContain(saved.GetProperty("Sources").EnumerateObject(), x => x.Name.Contains("old", StringComparison.OrdinalIgnoreCase));
        new ActivityStore(settings).SetFavorite(ActivityStore.Source(settings, ""), Guid.NewGuid(), true);
        Assert.Equal(Path.GetFullPath(settings), new ActivityStore(settings).Load().ProfilePath);
        Assert.Equal(List(newRoot), new LibraryStore(settings).Load().ListPath);
    }

    [Fact] public void SharedListFavoritesMoveWithProfilePathOnly()
    {
        WriteOldProfile(withLocalHistory: false);
        // A current build wrote the anchor before the move.
        new ActivityStore(Settings(oldRoot)).AddFavorites(ActivityStore.Source("", List(oldRoot)), [SharedId]);
        Move();
        var sources = new ActivityStore(Settings(newRoot)).Load().Favorites.Select(x => x.Source).ToArray();
        Assert.Contains(ActivityStore.Source("", List(newRoot)), sources);
        Assert.DoesNotContain(sources, x => x.Contains("OLD", StringComparison.OrdinalIgnoreCase) && x.Contains("TEAM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public void CopiedFolderUsesOwnFilesAndLeavesOriginalUntouched()
    {
        WriteOldProfile();
        var before = File.ReadAllBytes(Settings(oldRoot));
        CopyDirectory(oldRoot, newRoot); ProfileRelocation.PortableRoot = newRoot;
        var copy = new LibraryStore(Settings(newRoot));
        copy.Save(copy.Load());
        Assert.Equal(List(newRoot), new LibraryStore(Settings(newRoot)).Load().ListPath);
        Assert.Equal(before, File.ReadAllBytes(Settings(oldRoot)));
    }

    [Fact] public void ProfileOutsidePortableFolderKeepsPathsButFindsLocalFavorites()
    {
        WriteOldProfile(); Move();
        ProfileRelocation.PortableRoot = null;
        var library = new LibraryStore(Settings(newRoot)).Load();
        Assert.Equal(List(oldRoot), library.ListPath);
        Assert.Contains(ActivityStore.Source(Settings(newRoot), ""), library.Sources.Keys);
    }

    [Fact] public void UnmovedAndAmbiguousProfilesAreUnchanged()
    {
        WriteOldProfile();
        ProfileRelocation.PortableRoot = oldRoot;
        Assert.Equal(List(oldRoot), new LibraryStore(Settings(oldRoot)).Load().ListPath);
        Assert.Null(ProfileRelocation.Detect(null, ["local:" + Path.Combine(folder, "a.json"), "local:" + Path.Combine(folder, "b.json")], Settings(oldRoot)));
        Assert.Null(ProfileRelocation.Detect(null, [ActivityStore.Source("", Network)], Settings(oldRoot)));
    }

    [Fact] public void SimilarFolderPrefixIsNotMoved()
    {
        WriteOldProfile(); Move();
        var detected = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        var sibling = oldRoot + "-other" + Path.DirectorySeparatorChar + "list.v8i";
        Assert.Equal(sibling, detected.Relocate(sibling));
        Assert.Equal(Path.Combine(newRoot, "x.v8i"), detected.Relocate(Path.Combine(oldRoot, "x.v8i")));
    }

    // Codex F7: a file repository inside the moved portable folder follows it; a tcp:// one stays.
    [Fact] public void RepositoryFolderFollowsMovedProfile()
    {
        ProfileRelocation.PortableRoot = newRoot;
        var moved = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        BaseEntry Entry(string repository) => new() { Name = "A", Kind = ConnectionKind.Server, Location = "srv\\a",
            Authentication = new AuthenticationSettings { Repository = true, RepositoryPath = repository } };
        var result = moved.Apply(new Library { Bases = [Entry(Path.Combine(oldRoot, "repo")), Entry("tcp://server/repo")] });
        Assert.Equal([Path.Combine(newRoot, "repo"), "tcp://server/repo"], result.Bases.Select(x => x.Authentication!.RepositoryPath));
    }

    // Stage 11: the backup folder of a base and the general one follow the moved portable folder; a relative one stays.
    [Fact] public void BackupFoldersFollowMovedProfile()
    {
        ProfileRelocation.PortableRoot = newRoot;
        var moved = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        BaseEntry Entry(string backup) => new() { Name = "A", Kind = ConnectionKind.Server, Location = "srv\\a", BackupFolder = backup };
        var result = moved.Apply(new Library { BackupFolder = Path.Combine(oldRoot, "copies"), Bases = [Entry(Path.Combine(oldRoot, "a")), Entry("relative")] });
        Assert.Equal(Path.Combine(newRoot, "copies"), result.BackupFolder);
        Assert.Equal([Path.Combine(newRoot, "a"), "relative"], result.Bases.Select(x => x.BackupFolder));
    }

    // Codex audit 13.10 (group folders), P2-1: a group folder inside the moved portable folder follows it, in every list.
    [Fact] public void GroupFoldersFollowMovedProfile()
    {
        ProfileRelocation.PortableRoot = newRoot;
        var moved = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        var inside = new GroupFolder { Group = "A", Folder = Path.Combine(oldRoot, "bases") };
        var outside = new GroupFolder { Group = "B", Folder = Path.Combine(Path.GetTempPath(), "elsewhere") };
        var result = moved.Apply(new Library { GroupFolders = [inside, outside],
            Sources = new() { ["local:x"] = new SourceSettings { GroupFolders = [inside] } } });
        Assert.Equal([Path.Combine(newRoot, "bases"), outside.Folder], result.GroupFolders.Select(x => x.Folder));
        Assert.Equal(Path.Combine(newRoot, "bases"), Assert.Single(result.Sources.Values.Single().GroupFolders).Folder);
    }

    [Fact] public void ListViewsFollowMovedProfileAndNewerViewWins()
    {
        ProfileRelocation.PortableRoot = newRoot;
        var moved = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        var oldList = ActivityStore.Source("", List(oldRoot)); var newList = ActivityStore.Source("", List(newRoot));
        var result = moved.Apply(new Activity { Views = [
            new ListView(ActivityStore.Source(Settings(oldRoot), "")) { Selected = "old-local" },
            new ListView(oldList) { Selected = "old-list" }, new ListView(newList) { Selected = "new-list" },
            new ListView(ActivityStore.Source("", Network)) { Selected = "network" }] });
        Assert.Equal("old-local", result.Views.Single(x => x.Source == ActivityStore.Source(Settings(newRoot), "")).Selected);
        Assert.Equal("new-list", result.Views.Single(x => x.Source == newList).Selected);
        Assert.Equal("network", result.Views.Single(x => x.Source == ActivityStore.Source("", Network)).Selected);
        Assert.Equal(3, result.Views.Count);
    }

    [Fact] public void OtherLayoutOrStaleKeysOnlyRemapLocalOrNothing()
    {
        WriteOldProfile(); Move();
        // Settings outside the portable root: keys follow the file, paths are not moved.
        ProfileRelocation.PortableRoot = Path.Combine(folder, "unrelated");
        var outside = ProfileRelocation.Detect(null, [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot))!;
        Assert.Equal(List(oldRoot), outside.Relocate(List(oldRoot)));
        Assert.Equal(ActivityStore.Source(Settings(newRoot), ""), outside.Key(ActivityStore.Source(Settings(oldRoot), "")));
        Assert.Equal("invalid:x", outside.Key("invalid:x"));
        // Different file name inside the same root: not the same portable profile.
        ProfileRelocation.PortableRoot = newRoot;
        var renamed = ProfileRelocation.Detect(null, ["local:" + Path.Combine(oldRoot, "Settings", "other.json")], Settings(newRoot))!;
        Assert.Equal(List(oldRoot), renamed.Relocate(List(oldRoot)));
        // A relative anchor is ignored; a third unknown local key makes the move ambiguous.
        Assert.NotNull(ProfileRelocation.Detect("relative.json", [ActivityStore.Source(Settings(oldRoot), "")], Settings(newRoot)));
        Assert.Null(ProfileRelocation.Detect(Settings(oldRoot), [ActivityStore.Source(Path.Combine(folder, "third.json"), "")], Settings(newRoot)));
        Assert.Null(ProfileRelocation.Detect(Settings(newRoot), [], Settings(newRoot)));
    }

    [Fact] public void ExistingKeyAtNewPlaceWins()
    {
        WriteOldProfile(); Move();
        var detected = ProfileRelocation.Detect(Settings(oldRoot), [], Settings(newRoot))!;
        var newer = new SourceSettings { Groups = ["Newer"] };
        var library = new Library { Sources = new()
        {
            [ActivityStore.Source("", List(newRoot))] = newer,
            [ActivityStore.Source("", List(oldRoot))] = new() { Groups = ["Older"] }
        } };
        Assert.Equal(["Newer"], detected.Apply(library).Sources[ActivityStore.Source("", List(newRoot))].Groups);
        var duplicates = new Activity { Favorites = [new(ActivityStore.Source("", List(oldRoot)), SharedId), new(ActivityStore.Source("", List(newRoot)), SharedId)] };
        Assert.Single(detected.Apply(duplicates).Favorites);
    }

    private (Guid FileId, string Stored) AddListFileBase(string root)
    {
        var demo = Path.Combine(root, "Bases", "demo"); Directory.CreateDirectory(demo);
        var fileId = Guid.NewGuid();
        File.AppendAllText(List(root), $"[Demo]\nID={fileId}\nConnect=File=\"{demo}\";\n");
        return (fileId, demo);
    }

    private static Library Open(string settings, out V8iStore list, out LibraryStore store)
    {
        store = new LibraryStore(settings);
        var profile = store.Load();
        list = new V8iStore(profile.ListPath);
        return list.Load(LibrarySources.Select(profile, settings, profile.ListPath));
    }

    private static BaseEntry LocalFileBase(string settings) =>
        LibrarySources.Select(new LibraryStore(settings).Load(), settings, "").Bases.Single(x => x.Id == LocalId);

    [Fact] public void ListContentsAreNeverRewrittenAndProfileMovesOnce()
    {
        WriteOldProfile();
        var (fileId, stored) = AddListFileBase(oldRoot);
        Move();
        var settings = Settings(newRoot);
        var bytes = File.ReadAllBytes(List(newRoot));
        var loaded = Open(settings, out var list, out var store);
        Assert.NotNull(loaded.Moved);
        // .v8i addresses stay as stored, like in 1C itself.
        Assert.Equal(stored, loaded.Bases.Single(x => x.Id == fileId).Location);
        ProfileRelocation.Complete(loaded, store);
        Assert.Equal(bytes, File.ReadAllBytes(List(newRoot)));
        Assert.False(File.Exists(List(newRoot) + ".bak"));

        // The profile's own file base moved once; nothing is detected afterwards.
        Assert.Null(new LibraryStore(settings).Load().Moved);
        Assert.Equal(Path.Combine(newRoot, "Bases", "demo"), LocalFileBase(settings).Location);

        // A later explicit address under the old folder name is kept exactly.
        var chosen = Path.Combine(oldRoot, "Bases", "external");
        var profile = new LibraryStore(settings);
        var local = LibrarySources.Select(profile.Load(), settings, "");
        profile.Save(local with { Bases = [.. local.Bases.Select(x => x.Id == LocalId ? x with { Location = chosen } : x)] });
        Assert.Equal(chosen, LocalFileBase(settings).Location);
    }

    [Fact] public void FailedWriteIsDetectedAgainWithoutDoubling()
    {
        WriteOldProfile();
        // A -> A/nested: a second relocation of already moved paths would give nested/nested.
        var stage = Path.Combine(folder, "stage");
        Directory.Move(oldRoot, stage); Directory.CreateDirectory(oldRoot);
        var nested = Path.Combine(oldRoot, "nested"); Directory.Move(stage, nested);
        ProfileRelocation.PortableRoot = nested;
        var settings = Settings(nested);
        var loaded = new LibraryStore(settings).Load();
        using (new FileStream(settings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ProfileRelocation.Complete(loaded, new LibraryStore(settings)));
        for (var i = 0; i < 2; i++)
            Assert.Equal(Path.Combine(nested, "Bases", "demo"), LocalFileBase(settings).Location);
        ProfileRelocation.Complete(new LibraryStore(settings).Load(), new LibraryStore(settings));
        Assert.Equal(Path.Combine(nested, "Bases", "demo"), LocalFileBase(settings).Location);
        Assert.Contains(ActivityStore.Source("", List(nested)), new ActivityStore(settings).Load().Favorites.Select(x => x.Source));
    }

    [Fact] public void DamagedHistoryStopsTheWriteButNotTheMove()
    {
        WriteOldProfile(); Move();
        var settings = Settings(newRoot);
        File.WriteAllText(settings + ".activity.json", "{ damaged");
        var profileBytes = File.ReadAllBytes(settings);
        var loaded = new LibraryStore(settings).Load();
        Assert.Equal(List(newRoot), loaded.ListPath);
        // The window catches this and keeps the list in memory; nothing is written, the old anchor stays.
        Assert.Throws<InvalidDataException>(() => ProfileRelocation.Complete(loaded, new LibraryStore(settings)));
        Assert.Equal(profileBytes, File.ReadAllBytes(settings));
        Assert.Equal("{ damaged", File.ReadAllText(settings + ".activity.json"));
        // After the history is repaired the move completes normally.
        File.Delete(settings + ".activity.json");
        ProfileRelocation.Complete(new LibraryStore(settings).Load(), new LibraryStore(settings));
        Assert.Null(new LibraryStore(settings).Load().Moved);
    }

    [Fact] public void ReadOnlyProfileKeepsWorkingInMemory()
    {
        WriteOldProfile(); Move();
        var settings = Settings(newRoot);
        File.SetAttributes(settings, FileAttributes.ReadOnly);
        try
        {
            if (OperatingSystem.IsWindows())
                Assert.ThrowsAny<Exception>(() => ProfileRelocation.Complete(new LibraryStore(settings).Load(), new LibraryStore(settings)));
            for (var i = 0; i < 2; i++) Assert.Equal(List(newRoot), new LibraryStore(settings).Load().ListPath);
        }
        finally { File.SetAttributes(settings, FileAttributes.Normal); }
        var unchanged = new LibraryStore(settings).Load() with { Moved = null };
        Assert.Same(unchanged, ProfileRelocation.Complete(unchanged, new LibraryStore(settings)));
    }

    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public void HistoryAnchorReadsProfileLikeTheProfileStore(bool bom, bool lowercase)
    {
        WriteOldProfile(withLocalHistory: false);
        var json = File.ReadAllText(Settings(oldRoot));
        if (lowercase) json = json.Replace("\"Sources\"", "\"sources\"");
        File.WriteAllText(Settings(oldRoot), json, new System.Text.UTF8Encoding(bom));
        Move();
        var settings = Settings(newRoot);
        Assert.Equal(List(newRoot), new LibraryStore(settings).Load().ListPath);
        Assert.Contains(ActivityStore.Source("", List(newRoot)), new ActivityStore(settings).Load().Favorites.Select(x => x.Source));
        var store = new LibraryStore(settings); store.Save(store.Load());
        Assert.Contains(ActivityStore.Source("", List(newRoot)), new ActivityStore(settings).Load().Favorites.Select(x => x.Source));
    }

    [Fact] public void DamagedProfileGivesHistoryNoAnchor()
    {
        WriteOldProfile(withLocalHistory: false); Move();
        File.WriteAllText(Settings(newRoot), "{ damaged");
        Assert.Contains(ActivityStore.Source("", List(oldRoot)), new ActivityStore(Settings(newRoot)).Load().Favorites.Select(x => x.Source));
    }

    [Fact] public void OnlyNormalizedPathsInsideTheFolderMove()
    {
        WriteOldProfile(); Move();
        var detected = ProfileRelocation.Detect(Settings(oldRoot), [], Settings(newRoot))!;
        var escaped = oldRoot + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "outside" + Path.DirectorySeparatorChar + "db";
        Assert.Equal(escaped, detected.Relocate(escaped));
        Assert.Equal(newRoot, detected.Relocate(oldRoot));
        Assert.Equal(Path.Combine(newRoot, "Bases", "demo"), detected.Relocate(Path.Combine(oldRoot, "Bases", ".", "demo")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(newRoot, "Lists", "team.v8i"), detected.Relocate(oldRoot.Replace('\\', '/') + "/Lists/team.v8i"));
    }

    [Fact] public void HistoryOfPreviousBuildWithoutLocalRecordsFollowsProfile()
    {
        WriteOldProfile(withLocalHistory: false); Move();
        var settings = Settings(newRoot);
        var activity = new ActivityStore(settings);
        Assert.Contains(ActivityStore.Source("", List(newRoot)), activity.Load().Favorites.Select(x => x.Source));
        // Saving the profile first must not strand the history on the old key.
        var store = new LibraryStore(settings); store.Save(store.Load());
        activity.SetFavorite(ActivityStore.Source(settings, ""), LocalId, true);
        var saved = new ActivityStore(settings).Load();
        Assert.Contains(ActivityStore.Source("", List(newRoot)), saved.Favorites.Select(x => x.Source));
        Assert.Equal(Path.GetFullPath(settings), saved.ProfilePath);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target));
    }
}
