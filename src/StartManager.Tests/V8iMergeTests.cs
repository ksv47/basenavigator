using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>A save after another program changed the shared list: the change goes over theirs when the records differ.</summary>
public sealed class V8iMergeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "v8i-merge-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "ibases.v8i");
    public V8iMergeTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private const string A = "11111111-1111-1111-1111-111111111111", B = "22222222-2222-2222-2222-222222222222", G = "33333333-3333-3333-3333-333333333333";
    private const string List = "[Бухгалтерия]\r\nConnect=File=\"C:\\buh\";\r\nID=" + A + "\r\nFolder=/Работа\r\nCustom=a\r\n" +
        "[Склад]\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nID=" + B + "\r\nFolder=/\r\n" +
        "[Работа]\r\nID=" + G + "\r\nFolder=/\r\n" +
        "[Web]\r\nConnect=wsdl=\"https://example.invalid/base\";\r\nFolder=/\r\nWebSetting=keep\r\n";

    private (V8iStore Store, Library Library) Open()
    {
        File.WriteAllText(PathName, List, new UTF8Encoding(false));
        var store = new V8iStore(PathName); return (store, store.Load(new()));
    }
    private void External(string from, string to) => File.WriteAllText(PathName, File.ReadAllText(PathName).Replace(from, to), new UTF8Encoding(false));
    private static BaseEntry Base(Library library, string id) => library.Bases.Single(x => x.Id == Guid.Parse(id));

    [Fact] public void ChangeOfAnotherRecordGoesOverTheirs()
    {
        var (store, library) = Open();
        External("Ref=\"stock\";\r\n", "Ref=\"stock\";\r\nTheirs=1\r\n");
        var next = library with { Bases = [Base(library, A) with { Name = "Бухгалтерия 3.0" }, Base(library, B)] };
        Assert.Throws<ListChangedException>(() => store.Save(next));
        var (fresh, saved) = store.SaveOverExternalChange(next);
        var text = File.ReadAllText(PathName);
        Assert.Contains("[Бухгалтерия 3.0]", text); Assert.Contains("Custom=a", text);
        Assert.Contains("Theirs=1", text); Assert.Contains("WebSetting=keep", text);
        Assert.Equal("Бухгалтерия 3.0", Base(saved, A).Name);
        // The returned store is the one to keep: it saves again without a conflict.
        fresh.Save(saved with { Bases = [Base(saved, A) with { Name = "Ещё раз" }, Base(saved, B)] });
        Assert.Contains("[Ещё раз]", File.ReadAllText(PathName));
    }

    [Theory]
    [InlineData("Custom=a", "Custom=theirs")]               // an unknown line of the same record
    [InlineData("Connect=File=\"C:\\buh\";", "Connect=File=\"D:\\buh\";")]
    [InlineData("[Бухгалтерия]\r\nConnect=File=\"C:\\buh\";\r\nID=" + A + "\r\nFolder=/Работа\r\nCustom=a\r\n", "")] // removed by them
    public void SameRecordChangedOnBothSidesIsNotOverwritten(string from, string to)
    {
        var (store, library) = Open();
        External(from, to);
        var theirs = File.ReadAllBytes(PathName);
        var next = library with { Bases = [Base(library, A) with { Name = "Моя правка" }, Base(library, B)] };
        var error = Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(next));
        Assert.Equal(["Бухгалтерия"], error.Names);
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
    }

    [Fact] public void AddedAndRemovedOnDifferentSidesBothStay()
    {
        var (store, library) = Open();
        External("[Web]", "[Их новая]\r\nConnect=File=\"C:\\new\";\r\nID=44444444-4444-4444-4444-444444444444\r\nFolder=/\r\n[Web]");
        var mine = new BaseEntry { Name = "Моя новая", Kind = ConnectionKind.File, Location = "C:\\mine" };
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [Base(library, A), mine] }); // Склад removed here
        Assert.Equal(["Бухгалтерия", "Их новая", "Моя новая"], saved.Bases.Select(x => x.Name).Order());
        var text = File.ReadAllText(PathName);
        Assert.DoesNotContain("[Склад]", text); Assert.Contains("[Их новая]", text); Assert.Contains("[Моя новая]", text);
    }

    [Fact] public void GroupRenameConflictsWithAChangeInsideTheGroup()
    {
        var (store, library) = Open();
        External("Custom=a", "Custom=theirs");
        var renamed = Groups.Rename(library, "Работа", "Проекты");
        Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(renamed, "Работа", "Проекты"));
        Assert.Contains("Custom=theirs", File.ReadAllText(PathName));
    }

    [Fact] public void GroupRenameGoesOverAChangeElsewhere()
    {
        var (store, library) = Open();
        External("Ref=\"stock\";\r\n", "Ref=\"stock\";\r\nTheirs=1\r\n");
        var renamed = Groups.Rename(library, "Работа", "Проекты");
        var (_, saved) = store.SaveOverExternalChange(renamed, "Работа", "Проекты");
        Assert.Equal("Проекты", Base(saved, A).Group);
        var text = File.ReadAllText(PathName);
        Assert.Contains("[Проекты]", text); Assert.Contains("ID=" + G, text); Assert.Contains("Theirs=1", text);
    }

    [Theory, InlineData(true), InlineData(false)]
    public void RenameIntoAGroupTheyMadeMeanwhileIsAConflict(bool theirsAfter)
    {
        // Their new group «Проекты» before or after ours: whichever comes first, one record would be dropped.
        var (store, library) = Open();
        var group = "[Проекты]\r\nID=55555555-5555-5555-5555-555555555555\r\nFolder=/\r\nForeign=keep\r\n";
        External(theirsAfter ? "[Web]" : "[Бухгалтерия]", theirsAfter ? group + "[Web]" : group + "[Бухгалтерия]");
        var theirs = File.ReadAllBytes(PathName);
        Assert.Contains("Проекты", Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(Groups.Rename(library, "Работа", "Проекты"), "Работа", "Проекты")).Names);
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
        // The same through the formatting of names.
        var (formatting, loaded) = Open(); File.WriteAllBytes(PathName, theirs);
        var map = new Dictionary<string, string> { ["Работа"] = "Проекты" };
        Assert.Throws<ListConflictException>(() => formatting.SaveOverExternalChange(Groups.Rename(loaded, "Работа", "Проекты"), groupMap: map));
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
    }

    [Fact] public void RemovedGroupThatGotAMemberThereIsAConflict()
    {
        // «Пустая» is empty when loaded; they put a base into it while it is removed here.
        File.WriteAllText(PathName, List.Replace("[Работа]", "[Пустая]\r\nID=77777777-7777-7777-7777-777777777777\r\nFolder=/\r\n[Работа]"), new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        External("[Web]", "[Их база]\r\nConnect=File=\"C:\\theirs\";\r\nID=66666666-6666-6666-6666-666666666666\r\nFolder=/Пустая\r\n[Web]");
        var theirs = File.ReadAllBytes(PathName);
        Assert.Contains("Пустая", Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(Groups.RemoveEmpty(library, "Пустая"))).Names);
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
    }

    [Fact] public void FixingAnIdThatIsThereAlreadyIsNoChangeOfTheRecord()
    {
        // The properties dialog always asks to fix the ID: with an ID in the file, a local change goes over their edit.
        var (store, library) = Open();
        External("Custom=a", "Custom=theirs");
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [Base(library, A) with { Comment = "локально" }, Base(library, B)] }, ensureId: Guid.Parse(A));
        Assert.Contains("Custom=theirs", File.ReadAllText(PathName));
        Assert.Equal("локально", Base(saved, A).Comment);
    }

    [Fact] public void RetryFromAnOldDialogKeepsWhatTheyChangedAndTheUserDidNot()
    {
        var (_, library) = Open();
        var original = Base(library, A);
        // Read again after the conflict: they moved the base to D:\buh.
        var now = library with { Bases = [original with { Location = "D:\\buh" }, Base(library, B)] };
        var renamed = BaseEdit.OnCurrent(now.Bases, original, original with { Name = "Моя" })!;
        Assert.Equal(("Моя", "D:\\buh"), (renamed.Name, renamed.Location));
        var moved = BaseEdit.OnCurrent(now.Bases, original, original with { Location = "E:\\buh" })!;
        Assert.Equal(("Бухгалтерия", "E:\\buh"), (moved.Name, moved.Location));
        Assert.Null(BaseEdit.OnCurrent([Base(library, B)], original, original with { Name = "Моя" }));
    }

    private const string Implicit = "[A]\r\nConnect=File=\"C:\\a\";\r\nID=" + A + "\r\nFolder=/Old\r\n[B]\r\nConnect=File=\"C:\\b\";\r\nID=" + B + "\r\nFolder=/\r\n";
    private const string TheirNew = "[TheirNew]\r\nConnect=File=\"C:\\t\";\r\nID=88888888-8888-8888-8888-888888888888\r\nFolder=/Old\r\n";

    [Theory, InlineData("delete"), InlineData("move-out"), InlineData("move-in")]
    public void BasesMovingThroughAGroupWithoutARecordAreNoGroupOperation(string operation)
    {
        // «Old» has no record of its own: it comes and goes with the bases in it. Different bases on both sides merge.
        File.WriteAllText(PathName, Implicit, new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        var (a, b) = (Base(library, A), Base(library, B));
        Library next;
        if (operation == "move-in")
        {
            External("ID=" + B + "\r\nFolder=/", "ID=" + B + "\r\nFolder=/New");
            next = library with { Bases = [a with { Group = "New" }, b] };
        }
        else
        {
            External("[B]", TheirNew + "[B]");
            next = library with { Bases = operation == "delete" ? [b] : [a with { Group = "" }, b] };
        }
        var (_, saved) = store.SaveOverExternalChange(next);
        var names = new V8iStore(PathName).Load(new()).Bases.ToDictionary(x => x.Name, x => x.Group);
        if (operation == "move-in") Assert.Equal(("New", "New"), (names["A"], names["B"]));
        else { Assert.Equal("Old", names["TheirNew"]); Assert.Equal(operation == "delete", !names.ContainsKey("A")); }
        Assert.Equal(names.Count, saved.Bases.Count);
    }

    [Theory, InlineData("make"), InlineData("rename"), InlineData("remove")]
    public void GroupOperationDoesNotConflictWithAnotherGroupWithoutARecord(string operation)
    {
        // «Other» lives only in the Folder of base A; the operation is on the empty explicit «Old»; they edit base B.
        File.WriteAllText(PathName, "[A]\r\nConnect=File=\"C:\\a\";\r\nID=" + A + "\r\nFolder=/Other\r\n[B]\r\nConnect=File=\"C:\\b\";\r\nID=" + B +
            "\r\nFolder=/\r\n[Old]\r\nID=" + G + "\r\nFolder=/\r\n", new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        External("ID=" + B + "\r\n", "ID=" + B + "\r\nForeign=keep\r\n");
        var (_, saved) = operation switch
        {
            "make" => store.SaveOverExternalChange(Groups.Rename(library, "", "New")),
            "rename" => store.SaveOverExternalChange(Groups.Rename(library, "Old", "New"), "Old", "New"),
            _ => store.SaveOverExternalChange(Groups.RemoveEmpty(library, "Old")),
        };
        var text = File.ReadAllText(PathName);
        Assert.Contains("Foreign=keep", text); Assert.Equal("Other", Base(saved, A).Group);
        Assert.Equal(operation != "remove", text.Contains("[New]"));
        Assert.Equal(operation == "make", text.Contains("[Old]"));
    }

    [Theory, InlineData("make"), InlineData("rename"), InlineData("remove")]
    public void GroupLeftEmptyThereDoesNotComeBack(string operation)
    {
        // They move A, the only base of the implicit «Other», out of it while a group operation is saved here.
        File.WriteAllText(PathName, "[A]\r\nConnect=File=\"C:\\a\";\r\nID=" + A + "\r\nFolder=/Other\r\n[B]\r\nConnect=File=\"C:\\b\";\r\nID=" + B +
            "\r\nFolder=/\r\n[Old]\r\nID=" + G + "\r\nFolder=/\r\n", new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        External("Folder=/Other", "Folder=/Elsewhere");
        _ = operation switch
        {
            "make" => store.SaveOverExternalChange(Groups.Rename(library, "", "New")),
            "rename" => store.SaveOverExternalChange(Groups.Rename(library, "Old", "New"), "Old", "New"),
            _ => store.SaveOverExternalChange(Groups.RemoveEmpty(library, "Old")),
        };
        var reloaded = new V8iStore(PathName).Load(new());
        Assert.DoesNotContain("Other", Groups.All(reloaded));
        Assert.Equal("Elsewhere", Base(reloaded, A).Group);
    }

    [Fact] public void RecordAddedAfterTheLastOneIsNoChangeOfIt()
    {
        // The file ends with a line break; they add a base at the end, after the record edited here.
        File.WriteAllText(PathName, "[A]\r\nConnect=File=\"C:\\a\";\r\nID=" + A + "\r\nFolder=/\r\n", new UTF8Encoding(false));
        var store = new V8iStore(PathName); var library = store.Load(new());
        File.AppendAllText(PathName, "[New]\r\nConnect=File=\"C:\\new\";\r\nFolder=/\r\nKeep=1\r\n");
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [Base(library, A) with { Name = "A2" }] });
        var text = File.ReadAllText(PathName);
        Assert.Contains("[A2]", text); Assert.Contains("Keep=1", text);
        Assert.Equal(["A2", "New"], saved.Bases.Select(x => x.Name).Order());
    }

    [Fact] public void PropertiesOfABaseRemovedThereAreAConflictNotAnError()
    {
        var (store, library) = Open();
        External("[Склад]\r\nConnect=Srvr=\"srv\";Ref=\"stock\";\r\nID=" + B + "\r\nFolder=/\r\n", "");
        var theirs = File.ReadAllBytes(PathName);
        var next = library with { Bases = [Base(library, A), Base(library, B) with { Comment = "локально" }] };
        Assert.Equal(["Склад"], Assert.Throws<ListConflictException>(() => store.SaveOverExternalChange(next, ensureId: Guid.Parse(B))).Names);
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
    }

    [Fact] public void LocalOnlyChangeKeepsTheirListAsItIs()
    {
        var (store, library) = Open();
        External("Ref=\"stock\";\r\n", "Ref=\"stock\";\r\nTheirs=1\r\n");
        var theirs = File.ReadAllBytes(PathName);
        var (_, saved) = store.SaveOverExternalChange(library with { Bases = [Base(library, A) with { Comment = "локально" }, Base(library, B)] });
        Assert.Equal(theirs, File.ReadAllBytes(PathName));
        Assert.Equal("локально", Base(saved, A).Comment);
    }
}
