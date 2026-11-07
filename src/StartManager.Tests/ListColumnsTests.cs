using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class ListColumnsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BaseNavigator-columns-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(directory, "library.json");
    public ListColumnsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static readonly BaseEntry Alpha = new() { Name = "alpha", Location = "/a" }, Beta = new() { Name = "Beta", Location = "/b" },
        Gamma = new() { Name = "gamma", Location = "/c" }, Delta = new() { Name = "Delta", Location = "/d" };
    private static readonly Dictionary<Guid, BaseInfo> Info = new()
    {
        [Alpha.Id] = new("8.3.27.2214", new DateTime(2026, 9, 1), 5_000, ""),
        [Beta.Id] = new("8.3.9.1818", new DateTime(2026, 9, 20), 70_000, ""),
        // Not found on the disk: no date and size; the version of the record with an asterisk.
        [Gamma.Id] = new("8.3.10 *", null, null, ""),
    };
    private static string[] Names(ListColumns columns) =>
        [.. columns.Order([Gamma, Delta, Beta, Alpha], id => Info.GetValueOrDefault(id)).Select(x => x.Name)];

    [Fact] public void NameSortIgnoresCaseAndReverses()
    {
        Assert.Equal(["alpha", "Beta", "Delta", "gamma"], Names(new()));
        Assert.Equal(["gamma", "Delta", "Beta", "alpha"], Names(new ListColumns().SortBy(ListColumn.Name)));
    }

    [Fact] public void ValuesSortNumericallyAndUnknownGoLast()
    {
        // 8.3.9 < 8.3.10 < 8.3.27: versions compare as numbers, not as text. Delta has no information yet.
        var platform = new ListColumns().SortBy(ListColumn.Platform);
        Assert.Equal(["Beta", "gamma", "alpha", "Delta"], Names(platform));
        Assert.Equal(["alpha", "gamma", "Beta", "Delta"], Names(platform.SortBy(ListColumn.Platform)));
        Assert.Equal(["alpha", "Beta", "Delta", "gamma"], Names(new ListColumns().SortBy(ListColumn.Date)));
        Assert.Equal(["Beta", "alpha", "Delta", "gamma"], Names(new ListColumns().SortBy(ListColumn.Size).SortBy(ListColumn.Size)));
    }

    [Fact] public void AnotherColumnSortsAscendingAndHiddenColumnsAreNotSorted()
    {
        var columns = new ListColumns().SortBy(ListColumn.Date).SortBy(ListColumn.Date).SortBy(ListColumn.Size);
        Assert.Equal((ListColumn.Size, false), (columns.Sort, columns.Descending));
        var hidden = columns.Toggle(ListColumn.Size);
        Assert.False(hidden.Visible(ListColumn.Size));
        Assert.Equal((ListColumn.Name, false), (hidden.Sort, hidden.Descending));
        Assert.Same(hidden, hidden.SortBy(ListColumn.Size));
        Assert.True(hidden.Toggle(ListColumn.Size).Visible(ListColumn.Size));
        Assert.Same(hidden, hidden.Toggle(ListColumn.Name));
        Assert.True(hidden.Visible(ListColumn.Name));
    }

    [Fact] public void ColumnsAreSavedWithTheViewAndKeepUnknownFields()
    {
        var store = new ActivityStore(Settings); var id = Guid.NewGuid();
        File.WriteAllText(store.PathName, "{\"Columns\":{\"Sort\":\"Name\",\"future\":3}}");
        store.SetFavorite("one", id, true);
        store.SaveView(new ListView("one"), new ListColumns { Sort = ListColumn.Date, Descending = true, Hidden = [ListColumn.Platform] });
        Assert.Contains("\"Date\"", File.ReadAllText(store.PathName));
        var columns = new ActivityStore(Settings).Load().Columns;
        Assert.Equal((ListColumn.Date, true), (columns.Sort, columns.Descending));
        Assert.Equal([ListColumn.Platform], columns.Hidden);
        Assert.Equal(3, columns.Extra!["future"].GetInt32());
        // A view saved without columns keeps the stored ones.
        store.SaveView(new ListView("two"));
        Assert.Equal(ListColumn.Date, store.Load().Columns.Sort);
    }

    [Theory]
    [InlineData("null")] [InlineData("42")] [InlineData("[]")] [InlineData("{\"Sort\":\"Color\"}")] [InlineData("{\"Sort\":7}")]
    [InlineData("{\"Hidden\":null}")] [InlineData("{\"Hidden\":[\"Name\"]}")] [InlineData("{\"Hidden\":[\"Other\"]}")] [InlineData("{\"Descending\":\"yes\"}")]
    public void DamagedColumnsGiveTheDefaultAndKeepFavorites(string columns)
    {
        var store = new ActivityStore(Settings); var id = Guid.NewGuid();
        File.WriteAllText(store.PathName, "{\"Favorites\":[{\"Source\":\"one\",\"Id\":\"" + id + "\"}],\"Columns\":" + columns + "}");
        var data = store.Load();
        Assert.Equal(id, Assert.Single(data.Favorites).Id);
        Assert.Equal(ListColumn.Name, data.Columns.Sort);
        Assert.Empty(data.Columns.Hidden);
    }

    [Theory]
    [InlineData("{\"Sort\":\"FutureColumn\",\"future\":42}")]
    [InlineData("{\"Sort\":\"Date\",\"Hidden\":[\"Size\",\"Other\"],\"Descending\":\"yes\",\"future\":42}")]
    public void UnknownValuesKeepTheOtherFields(string columns)
    {
        var store = new ActivityStore(Settings);
        File.WriteAllText(store.PathName, "{\"Columns\":" + columns + "}");
        var loaded = store.Load().Columns;
        store.SaveView(new ListView("probe"), loaded);
        var saved = new ActivityStore(Settings).Load().Columns;
        Assert.Equal(42, saved.Extra!["future"].GetInt32());
        Assert.False(saved.Descending);
        // Valid values survive next to damaged ones.
        if (columns.Contains("\"Date\"")) { Assert.Equal(ListColumn.Date, saved.Sort); Assert.Equal([ListColumn.Size], saved.Hidden); }
        else Assert.Equal(ListColumn.Name, saved.Sort);
    }

    [Fact] public void HiddenSortColumnFromTheFileFallsBackToTheName()
    {
        var store = new ActivityStore(Settings);
        File.WriteAllText(store.PathName, "{\"Columns\":{\"Sort\":\"Size\",\"Descending\":true,\"Hidden\":[\"Size\"]}}");
        var columns = store.Load().Columns;
        Assert.Equal((ListColumn.Name, false), (columns.Sort, columns.Descending));
        Assert.False(columns.Visible(ListColumn.Size));
    }
}
