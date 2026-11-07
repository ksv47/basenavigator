using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class BaseSearchTests
{
    private static readonly BaseEntry File = new() { Name = "Торговля", Kind = ConnectionKind.File, Location = @"D:\Bases\Trade" };
    private static readonly BaseEntry Server = new() { Name = "ERP (bases)", Kind = ConnectionKind.Server, Location = @"srv1c:1541\erp_prod" };

    [Theory]
    [InlineData("торг", true, false)]
    [InlineData("bases", false, true)]
    [InlineData("  ", true, true)]
    [InlineData(null, true, true)]
    public void NameSearchLooksAtTheNameOnly(string? query, bool file, bool server)
    {
        Assert.Equal(file, BaseSearch.Matches(File, query, SearchMode.Name));
        Assert.Equal(server, BaseSearch.Matches(Server, query, SearchMode.Name));
    }

    [Theory]
    [InlineData(@"d:\bases", true, false)]
    [InlineData("SRV1C", false, true)]
    [InlineData("Ref=\"erp", false, true)]
    [InlineData("File=", true, false)]
    [InlineData("Торговля", false, false)]
    [InlineData(" erp_prod ", false, true)]
    public void ConnectionSearchLooksAtTheConnectionString(string query, bool file, bool server)
    {
        Assert.Equal(file, BaseSearch.Matches(File, query, SearchMode.Connection));
        Assert.Equal(server, BaseSearch.Matches(Server, query, SearchMode.Connection));
    }
}
