using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// «Создать снимок списка ИБ для 8.3 / 8.2 / 8.1» and the column «Версия» of the original's snapshots.
public sealed class SnapshotBranchesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-snapbranch-" + Guid.NewGuid().ToString("N"));
    public SnapshotBranchesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact] public void TheVersionIsKeptInTheCatalog()
    {
        var snapshots = new ListSnapshots(Path.Combine(folder, "Snapshots"));
        var list = new UTF8Encoding(true).GetBytes("[A]\r\nConnect=File=\"C:\\a\";\r\nID=11111111-1111-1111-1111-111111111111\r\n");
        snapshots.Create("C:\\lists\\ibases.v8i", list, "Снимок ИБ", version: "8.1");
        snapshots.Create("C:\\lists\\other.v8i", list, "Без версии");
        var read = new ListSnapshots(Path.Combine(folder, "Snapshots")).List();
        Assert.Equal("8.1", read.Single(x => x.Description == "Снимок ИБ").Version);
        Assert.Null(read.Single(x => x.Description == "Без версии").Version);
    }

    // A catalog written before the column existed is read as it is: its snapshots show no version.
    [Fact] public void AnOlderCatalogWithoutTheVersionIsRead()
    {
        var root = Path.Combine(folder, "Snapshots"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "old.v8sx"), []);
        File.WriteAllText(Path.Combine(root, "snapshots.json"),
            """[{"File":"old.v8sx","Source":"C:\\l.v8i","Created":"2026-10-01T10:00:00","Bases":3,"Description":"Старый","Auto":false}]""");
        var item = Assert.Single(new ListSnapshots(root).List());
        Assert.Null(item.Version);
        Assert.Equal("Старый", item.Description);
    }

    // A field written by a newer version stays in the catalog when this one adds a snapshot.
    [Fact] public void UnknownCatalogFieldsSurviveARewrite()
    {
        var root = Path.Combine(folder, "Snapshots"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "old.v8sx"), []);
        File.WriteAllText(Path.Combine(root, "snapshots.json"),
            """[{"File":"old.v8sx","Source":"C:\\l.v8i","Created":"2026-10-01T10:00:00","Bases":3,"Description":"Старый","FutureMetadata":{"keep":true}}]""");
        var snapshots = new ListSnapshots(root);
        snapshots.Create("C:\\l.v8i", new UTF8Encoding(true).GetBytes("[A]\r\nConnect=File=\"C:\\a\";\r\n"), "Новый", version: "8.3");
        var text = File.ReadAllText(Path.Combine(root, "snapshots.json"));
        Assert.Contains("\"FutureMetadata\"", text);
        Assert.Contains("\"keep\": true", text);
    }

    [Fact] public void AListLargerThanTheLimitIsRefusedBeforeItIsRead()
    {
        var path = Path.Combine(folder, "huge.v8i");
        using (var stream = File.Create(path)) stream.SetLength(LegacyImport.MaximumBytes + 1);
        Assert.Throws<InvalidDataException>(() => ListSnapshots.ReadList(path));
        var small = Path.Combine(folder, "small.v8i"); File.WriteAllText(small, "[A]\r\nConnect=File=\"C:\\a\";\r\n");
        Assert.Equal(File.ReadAllBytes(small), ListSnapshots.ReadList(small));
    }

    [Fact] public void TheAutomaticSnapshotCarriesTheVersion()
    {
        var snapshots = new ListSnapshots(Path.Combine(folder, "Snapshots"));
        var created = snapshots.CreateAutoIfNeeded("C:\\std.v8i", () => (new UTF8Encoding(true).GetBytes("[A]\r\nConnect=File=\"C:\\a\";\r\n"), null), "Авто", 20, "8.3");
        Assert.Equal("8.3", created!.Version);
        Assert.True(created.Auto);
    }

    [Fact] public void EachBranchHasItsStandardList()
    {
        Assert.Equal(V8iStore.StandardPath, SnapshotBranches.ListPath("8.3"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(V8iStore.StandardPath, SnapshotBranches.ListPath("8.2"));
            Assert.EndsWith(Path.Combine("1C", "1Cv81", "ibases.v8i"), SnapshotBranches.ListPath("8.1"));
        }
        else
        {
            Assert.Null(SnapshotBranches.ListPath("8.2"));
            Assert.Null(SnapshotBranches.ListPath("8.1"));
        }
        Assert.Null(SnapshotBranches.ListPath("8.4"));
        Assert.All(SnapshotBranches.Available(), x => Assert.True(File.Exists(x.Path)));
    }
}
