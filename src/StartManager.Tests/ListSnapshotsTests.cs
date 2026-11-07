using System.IO.Compression;
using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Снимки списка информационных баз»: .v8sx copies of a list and putting one back into the shared .v8i.
public sealed class ListSnapshotsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-snap-" + Guid.NewGuid().ToString("N"));
    private string List => Path.Combine(folder, "ibases.v8i");
    private ListSnapshots Store => new(Path.Combine(folder, "Snapshots"));
    public ListSnapshotsTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static byte[] Text(string value) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(value)];
    private const string Two = "[A]\r\nConnect=File=\"C:\\a\";\r\nID=11111111-1111-1111-1111-111111111111\r\nFolder=/\r\nCustom=keep\r\n[B]\r\nConnect=Srvr=\"s\";Ref=\"b\";\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/G\r\n[G]\r\nID=33333333-3333-3333-3333-333333333333\r\nFolder=/\r\n";

    [Fact] public void SnapshotsAreCreatedListedReadAndDeleted()
    {
        var first = Store.Create(List, Text(Two), " До правки ");
        var second = Store.Create("Локальный список", Text("[X]\r\nConnect=File=\"D:\\x\";\r\n"), "Второй");
        Assert.Equal(2, first.Bases); Assert.Equal("До правки", first.Description); Assert.Equal(1, second.Bases);
        Assert.Equal([second.File, first.File], Store.List().Select(x => x.File));
        Assert.Equal(Text(Two), Store.Read(first));
        // The format of the original: a ZIP with one root ibases.v8i, which the import reads too.
        using (var archive = ZipFile.OpenRead(Path.Combine(Store.Folder, first.File)))
            Assert.Equal("ibases.v8i", Assert.Single(archive.Entries).FullName);
        Store.Delete([first]);
        Assert.Equal([second.File], Store.List().Select(x => x.File));
        Assert.False(File.Exists(Path.Combine(Store.Folder, first.File)));
    }

    [Fact] public void ADamagedCatalogIsNotOverwritten()
    {
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Path.Combine(Store.Folder, "snapshots.json"), "{broken");
        Assert.Throws<InvalidDataException>(() => Store.Create(List, Text(Two), "x"));
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(Store.Folder, "snapshots.json")));
        Assert.Empty(Directory.GetFiles(Store.Folder, "*.v8sx"));
    }

    [Fact] public void AForeignArchiveIsRefused()
    {
        var item = Store.Create(List, Text(Two), "x");
        var path = Path.Combine(Store.Folder, item.File);
        File.Delete(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) { archive.CreateEntry("ibases.v8i"); archive.CreateEntry("IBASES.V8I"); }
        Assert.Throws<InvalidDataException>(() => Store.Read(item));
        File.WriteAllText(path, "not a zip");
        Assert.Throws<InvalidDataException>(() => Store.Read(item));
    }

    [Fact] public void RestorePutsTheSnapshotBackWithABackup()
    {
        File.WriteAllBytes(List, Text(Two));
        var store = new V8iStore(List); var library = store.Load(new());
        var snapshot = Store.Create(List, File.ReadAllBytes(List), "x");
        store.Save(library with { Bases = [library.Bases[0] with { Name = "Изменено" }] });
        var changed = File.ReadAllBytes(List);
        var again = new V8iStore(List); again.Load(new());
        again.Restore(Store.Read(snapshot));
        Assert.Equal(Text(Two), File.ReadAllBytes(List));
        Assert.Equal(changed, File.ReadAllBytes(List + ".bak"));
        // The store must be loaded again before the next save.
        Assert.Throws<InvalidOperationException>(() => again.Save(library));
        Assert.Equal(["A", "B"], new V8iStore(List).Load(new()).Bases.Select(x => x.Name));
    }

    [Fact] public void RestoreRefusesAChangedFileAndAnUnreadableSnapshot()
    {
        File.WriteAllBytes(List, Text(Two));
        var store = new V8iStore(List); store.Load(new());
        File.AppendAllText(List, "; external edit");
        var external = File.ReadAllBytes(List);
        Assert.Throws<ListChangedException>(() => store.Restore(Text(Two)));
        Assert.Equal(external, File.ReadAllBytes(List));
        var fresh = new V8iStore(List); fresh.Load(new());
        Assert.Throws<InvalidDataException>(() => fresh.Restore(Text("[A]\r\nConnect=File=x;\r\nID=11111111-1111-1111-1111-111111111111\r\n[B]\r\nConnect=File=y;\r\nID=11111111-1111-1111-1111-111111111111\r\n")));
        Assert.Equal(external, File.ReadAllBytes(List));
        // Readable text without a single section is not an empty list.
        Assert.Throws<InvalidDataException>(() => fresh.Restore([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => fresh.Restore(Text("не список\r\n")));
        Assert.Equal(external, File.ReadAllBytes(List));
        fresh.Restore(Text("\r\n"));
        Assert.Equal(Text("\r\n"), File.ReadAllBytes(List));
    }

    // Codex full audit 24.10, P1-2: checking a snapshot before a restore (and the import of a list) read the text through a
    // copy in the shared temporary folder, readable by other users on Linux with umask 022. The text is read in memory now:
    // no copy appears in the temporary folder while it is checked.
    [Fact] public void ASnapshotWithASecretIsCheckedWithoutACopyInTheTemporaryFolder()
    {
        const string secret = "[W]\r\nConnect=ws=\"https://web/base\";\r\nID=44444444-4444-4444-4444-444444444444\r\nUseProxy=1\r\nProxySrv=proxy\r\nProxyPort=3128\r\nProxyUser=u\r\nProxyPassword=s3cret-proxy\r\n";
        File.WriteAllBytes(List, Text(Two));
        var store = new V8iStore(List); store.Load(new());
        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(Path.GetTempPath()) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        watcher.Created += (_, e) => created.Add(e.Name ?? "");
        store.Restore(Text(secret));
        Assert.Equal("W", Assert.Single(V8iStore.Read(Text(secret)).Bases).Name);
        var source = Path.Combine(folder, "original.v8i"); File.WriteAllBytes(source, Text(secret));
        LegacyImport.Preview(new Library(), source, null, new(Clients: false, Favorites: false));
        Thread.Sleep(300);
        Assert.DoesNotContain(created, x => x.StartsWith("bn-restore-", StringComparison.Ordinal) ||
            x.StartsWith("bn-snapshot-", StringComparison.Ordinal) || x.StartsWith("bn-import-", StringComparison.Ordinal));
        Assert.Equal(Text(secret), File.ReadAllBytes(List));
    }

    [Fact] public void ALocalSnapshotKeepsTheOwnStateOfItsBases()
    {
        var entry = new BaseEntry { Name = "A", Kind = ConnectionKind.File, Location = @"C:\a", Group = "G", Executable = "/opt/1cv8/x86_64/8.3.27.2214/1cv8c",
            PlatformRule = new() { Version = "8.3.27" }, Extra = new() { ["future"] = System.Text.Json.JsonSerializer.SerializeToElement(42) } };
        var library = new Library { Bases = [entry], Groups = ["G", "Пустая"] };
        var item = Store.Create("Локальный список", ListMaintenance.Utf8WithBom(ListMaintenance.ToV8i(library)), "x", LocalListState.Write(library));
        var state = LocalListState.Read(Store.ReadState(item)!);
        var back = Assert.Single(state.Bases);
        Assert.Equal((entry.Id, entry.Executable, "8.3.27", ""), (back.Id, back.Executable, back.PlatformRule?.Version, back.PlatformVersion));
        Assert.Equal(42, back.Extra!["future"].GetInt32());
        Assert.Equal(["G", "Пустая"], state.Groups);
        Assert.Null(Store.ReadState(Store.Create(List, Text(Two), "shared")));
    }

    [Fact] public void AHeldSnapshotKeepsItsRecord()
    {
        var item = Store.Create(List, Text(Two), "x");
        using (new FileStream(Path.Combine(Store.Folder, item.File), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = Store.Delete([item]);
            // Windows refuses to delete an open file; Linux removes it.
            if (OperatingSystem.IsWindows()) { Assert.Single(failed); Assert.Single(Store.List()); }
            else { Assert.Empty(failed); Assert.Empty(Store.List()); }
        }
    }

    [Fact] public void AStateTooLargeToReadBackIsRefusedBeforeWriting()
    {
        var huge = new byte[LegacyImport.MaximumBytes + 1];
        Assert.Throws<InvalidDataException>(() => Store.Create("Локальный список", Text(Two), "x", huge));
        Assert.False(Directory.Exists(Store.Folder) && Directory.EnumerateFiles(Store.Folder, "*.v8sx").Any());
        Assert.Empty(Store.List());
    }

    [Fact] public void LegacyImportReadsOurSnapshot()
    {
        var item = Store.Create(List, Text(Two), "x");
        var plan = LegacyImport.Preview(new Library(), Path.Combine(Store.Folder, item.File), null, new(false, false, false));
        Assert.Equal(2, plan.Added.Count);
    }
}
