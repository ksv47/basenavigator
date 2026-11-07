using System.Text;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// The rest of stage 5: formatting the names of a list, the search rules and the automatic snapshots.
public sealed class StageFiveTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "bn-stage5-" + Guid.NewGuid().ToString("N"));
    private string List => Path.Combine(folder, "ibases.v8i");
    public StageFiveTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static BaseEntry Base(string name, string group = "") => new() { Name = name, Kind = ConnectionKind.File, Location = @"C:\" + Guid.NewGuid().ToString("N"), Group = group };

    [Theory]
    [InlineData(NameCase.Upper, "бухгалтерия предприятия ", "БУХГАЛТЕРИЯ ПРЕДПРИЯТИЯ ")]
    [InlineData(NameCase.Lower, "Бухгалтерия ЗУП", "бухгалтерия зуп")]
    [InlineData(NameCase.Sentence, "бухгалтерия ЗУП", "Бухгалтерия зуп")]
    [InlineData(NameCase.Words, "бухгалтерия предприятия", "Бухгалтерия Предприятия")]
    [InlineData(NameCase.None, "Без Изменений", "Без Изменений")]
    public void NamesChangeTheirCase(NameCase mode, string name, string expected) =>
        Assert.Equal(expected, ListFormatting.Format(name, new(mode)));

    [Fact] public void TrailingSpacesAndReplacement() =>
        Assert.Equal("Бух (копия)", ListFormatting.Format("Бух_копия  ", new(TrimEnd: true, ReplaceFrom: "_копия", ReplaceTo: " (копия)")));

    [Fact] public void OneSaveKeepsTheGroupRecordsAndUnknownFields()
    {
        File.WriteAllText(List, "[работа]\r\nID=11111111-1111-1111-1111-111111111111\r\nFolder=/\r\nUnknownGroup=keep\r\n" +
            "[отдел]\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/работа\r\n" +
            "[бух]\r\nConnect=File=\"C:\\b\";\r\nID=33333333-3333-3333-3333-333333333333\r\nFolder=/работа/отдел\r\nCustom=keep\r\n", new UTF8Encoding(true));
        var store = new V8iStore(List); var library = store.Load(new());
        var plan = ListFormatting.Plan(library, new(NameCase.Upper));
        Assert.Equal((1, 2), (plan.Bases, plan.Groups));
        store.Save(plan.Library, groupMap: plan.GroupMap);
        var text = File.ReadAllText(List);
        Assert.Contains("[РАБОТА]\r\nID=11111111-1111-1111-1111-111111111111", text); Assert.Contains("UnknownGroup=keep", text);
        Assert.Contains("[ОТДЕЛ]\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/РАБОТА", text);
        Assert.Contains("[БУХ]", text); Assert.Contains("Folder=/РАБОТА/ОТДЕЛ", text); Assert.Contains("Custom=keep", text);
        var again = new V8iStore(List).Load(new());
        Assert.Equal(["РАБОТА", "РАБОТА/ОТДЕЛ"], Groups.All(again).Order(StringComparer.Ordinal));
    }

    [Fact] public void AnUnsupportedRecordInARenamedGroupLeavesTheFileAndBackupAsTheyWere()
    {
        var original = "[aaa]\r\nID=11111111-1111-1111-1111-111111111111\r\nFolder=/\r\n[bbb]\r\nID=22222222-2222-2222-2222-222222222222\r\nFolder=/\r\n" +
            "[Web]\r\nConnect=Other=unsupported;\r\nFolder=/bbb\r\n";
        File.WriteAllText(List, original, new UTF8Encoding(false));
        var store = new V8iStore(List); var library = store.Load(new());
        var plan = ListFormatting.Plan(library, new(NameCase.Upper));
        Assert.Throws<InvalidOperationException>(() => store.Save(plan.Library, groupMap: plan.GroupMap));
        Assert.Equal(original, File.ReadAllText(List));
        Assert.False(File.Exists(List + ".bak"));
    }

    [Theory]
    [InlineData("team", @"parent\team")]
    [InlineData("x", "a/b")]
    public void ASeparatorInAGroupNameIsRefused(string from, string to) =>
        Assert.Throws<ArgumentException>(() => ListFormatting.Plan(new Library { Groups = ["team", "x"] }, new(ReplaceFrom: from, ReplaceTo: to)));

    [Fact] public void NamesAreJudgedAsTheListStoresThem()
    {
        // «p» → «m » is «M» once the list trims it: the same name as «m» → «M».
        Assert.Throws<ArgumentException>(() => ListFormatting.Plan(new Library { Groups = ["m", "p"] }, new(NameCase.Upper, ReplaceFrom: "p", ReplaceTo: "m ")));
        Assert.Throws<ArgumentException>(() => ListFormatting.Plan(new Library { Groups = ["A b", "A_b"] }, new(ReplaceFrom: "_", ReplaceTo: " ")));
        Assert.Throws<ArgumentException>(() => ListFormatting.Plan(new Library { Bases = [Base("x")] }, new(ReplaceFrom: "x", ReplaceTo: "")));
        // An intermediate clash of a one-by-one rename does not matter for one save: «a» → «aa» and «aa» → «aaaa».
        var plan = ListFormatting.Plan(new Library { Groups = ["a", "aa"], Bases = [Base("b", "a")] }, new(ReplaceFrom: "a", ReplaceTo: "aa"));
        Assert.Equal(["aa", "aaaa"], plan.Library.Groups.Order(StringComparer.Ordinal));
        Assert.Equal("aa", plan.Library.Bases[0].Group);
        Assert.Equal("x", ListFormatting.Plan(new Library { Bases = [Base("x")] }, new(NameCase.Upper, Bases: false)).Library.Bases[0].Name);
    }

    [Fact] public void SearchRulesFilterByDateAndSize()
    {
        const long mb = 1048576;
        FoundBase Of(long size, DateTime? modified = null) => new(folder, size, modified ?? new DateTime(2026, 9, 10, 15, 0, 0));
        Assert.True(new FoundFilter().Matches(Of(5 * mb)));
        Assert.True(new FoundFilter(NotLaterThan: new DateTime(2026, 9, 10)).Matches(Of(1, new DateTime(2026, 9, 10, 23, 59, 59))));
        Assert.False(new FoundFilter(NotLaterThan: new DateTime(2026, 9, 10)).Matches(Of(1, new DateTime(2026, 9, 11))));
        Assert.False(new FoundFilter(Size: SizeCondition.Greater, Megabytes: 4).Matches(Of(4 * mb)));
        Assert.True(new FoundFilter(Size: SizeCondition.Greater, Megabytes: 4).Matches(Of(4 * mb + 1)));
        Assert.True(new FoundFilter(Size: SizeCondition.Less, Megabytes: 4).Matches(Of(4 * mb - 1)));
        // «Равен 5» — the whole megabytes: from 5 МБ up to, not including, 6 МБ.
        foreach (var size in new[] { 5 * mb, 5 * mb + 1, 5 * mb + mb / 2, 6 * mb - 1 }) Assert.True(new FoundFilter(Size: SizeCondition.Equal, Megabytes: 5).Matches(Of(size)));
        foreach (var size in new[] { 5 * mb - 1, 6 * mb }) Assert.False(new FoundFilter(Size: SizeCondition.Equal, Megabytes: 5).Matches(Of(size)));
    }

    [Fact] public void AutomaticSnapshotsOncePerDayAndOnlyTheNewestKept()
    {
        var store = new ListSnapshots(Path.Combine(folder, "Snapshots"));
        var list = Encoding.UTF8.GetBytes("[A]\r\nConnect=File=\"C:\\a\";\r\n");
        var reads = 0;
        (byte[], byte[]?) Content() { reads++; return (list, null); }
        Assert.NotNull(store.CreateAutoIfNeeded("L", Content, "авто", 20));
        Assert.Null(store.CreateAutoIfNeeded("L", Content, "авто", 20));
        Assert.Equal(1, reads);
        Assert.False(store.NeedsAuto("L", DateTime.Now));
        Assert.True(store.NeedsAuto("Другой", DateTime.Now));
        Assert.True(store.NeedsAuto("L", DateTime.Now.AddDays(1)));
        var manual = store.Create("L", list, "руками");
        for (var i = 0; i < 4; i++) store.Create("L", list, "авто " + i, auto: true);
        store.PruneAuto("L", 2);
        var left = store.List();
        Assert.Equal(2, left.Count(x => x.Auto));
        Assert.Contains(left, x => x.File == manual.File && !x.Auto);
    }
}
