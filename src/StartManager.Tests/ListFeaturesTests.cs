using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class ListFeaturesTests
{
    private static BaseEntry Entry => new() { Name = "Base", Location = "/tmp/base", Group = "A/B" };
    [Fact] public void GroupsRenameSubtreeAndKeepUnrelatedEntries()
    {
        var library = new Library { Bases = [Entry, Entry with { Id = Guid.NewGuid(), Group = "Other" }] };
        Assert.Equal("A/B", Groups.Normalize(" / A \\ B/ "));
        Assert.Equal(new[] { "A", "A/B", "Other" }, Groups.All(library));
        var renamed = Groups.Rename(library, "A", "New");
        Assert.Equal(new[] { "New/B", "Other" }, renamed.Bases.Select(x => x.Group));
        Assert.Contains("Empty", Groups.Rename(library, "", "Empty").Groups);
        Assert.Empty(Groups.RemoveEmpty(new Library { Groups = ["Empty"] }, "Empty").Groups);
        Assert.Throws<ArgumentException>(() => Groups.RemoveEmpty(library, "A"));
        Assert.Throws<ArgumentException>(() => Groups.RemoveEmpty(new Library { Groups = ["A/B"] }, "A"));
    }
    [Theory] [InlineData("A", "")] [InlineData("A", "x\ny")] [InlineData("A", "Other")] [InlineData("Missing", "New")] [InlineData("A", "A/B/C")]
    public void BadGroupMovesRejected(string from, string to) => Assert.Throws<ArgumentException>(() => Groups.Rename(new Library { Groups = ["A/B", "Other"] }, from, to));
    [Fact] public void ImportHandlesFoldersServersDuplicatesAndMalformedRecords()
    {
        var result = V8iImporter.Import(new Library(), "\uFEFF; comment\n[Folder]\nFolder=/\n[Base]\nConnect=File=\"C:\\data\";\nFolder=/Folder\nDefaultVersion=8.3.27\n[Duplicate]\nConnect=File=\"c:\\data\\\";\n[Server]\nConnect=Srvr=host;Ref=ref;\n[Invalid]\nConnect=File=x;Srvr=y;\n[Web]\nConnect=wsdl=http://example.invalid;\n[Broken]\nConnect=bad\n[ ]\nFolder=/\n");
        Assert.Equal(2, result.Added); Assert.Equal(1, result.Duplicates); Assert.Equal(4, result.Warnings.Count);
        Assert.Contains("Folder", result.Library.Groups); Assert.Equal("8.3.27", result.Library.Bases[0].PlatformVersion);
        Assert.Equal(ConnectionKind.Server, result.Library.Bases[1].Kind);
    }
    [Theory] [InlineData("Group")] [InlineData("Executable")] [InlineData("PlatformVersion")]
    public void NullEntryPropertiesRejected(string property)
    {
        var entry = property switch { "Group" => Entry with { Group = null! }, "Executable" => Entry with { Executable = null! }, _ => Entry with { PlatformVersion = null! } };
        Assert.Throws<ArgumentException>(entry.Validate);
    }
    [Fact] public void InvalidGroupCollectionsRejected()
    {
        foreach (var groups in new List<string>?[] { null, [""], ["/A"], ["A", "a"] })
            Assert.Throws<InvalidDataException>(() => LibraryStore.Validate(new Library { Groups = groups! }));
        Assert.Throws<ArgumentException>(() => (Entry with { Group = "/A" }).Validate());
    }
    [Fact] public void FileInformationAndPerBasePlatform()
    {
        var folder = Path.Combine(Path.GetTempPath(), "base-info-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "1Cv8.1CD"), new byte[8192]);
            var bin = Path.Combine(folder, "8.3.27.2214", "bin"); Directory.CreateDirectory(bin);
            var executable = Path.Combine(bin, "1cv8"); File.WriteAllText(executable, "fixture");
            var entry = Entry with { Location = folder, Executable = executable };
            var info = BaseInfo.Read(entry, ""); Assert.Equal(8192, info.Size); Assert.NotNull(info.Modified); Assert.Equal("8.3.27.2214", info.Platform);
            Assert.Equal(executable, Launcher.Build("missing", entry, false).FileName);
            var server = entry with { Kind = ConnectionKind.Server, Location = "host\\ref", Executable = "", PlatformVersion = "8.3" };
            Assert.Null(BaseInfo.Read(server, "").Size); Assert.Equal("8.3 *", BaseInfo.Read(server, "").Platform);
            Assert.Null(BaseInfo.Read(Entry, "").Size);
            Assert.Null(BaseInfo.Read(Entry with { Location = "bad\0path" }, "").Size);
            Assert.NotNull(BaseInfo.Read(Entry, Environment.ProcessPath!));
            var plain = Path.Combine(folder, "1cv8"); File.WriteAllText(plain, "not executable");
            Assert.NotNull(BaseInfo.Read(Entry, plain));
            var text = "[База]\nConnect=File=\"/tmp/base\";";
            var list = Path.Combine(folder, "list.v8i"); File.WriteAllText(list, text);
            Assert.Equal(text, V8iImporter.ReadText(list));
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            File.WriteAllText(list, text, System.Text.Encoding.GetEncoding(1251));
            Assert.Equal(text, V8iImporter.ReadText(list));
        }
        finally { Directory.Delete(folder, true); }
    }
}
