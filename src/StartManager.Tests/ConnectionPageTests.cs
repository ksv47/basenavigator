using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

/// <summary>The page «Подключение»: «Кластер серверов» and «Имя инф. базы в кластере» of a server base.</summary>
public sealed class ConnectionPageTests
{
    [Theory]
    [InlineData(@"srv1c\accounting", "srv1c", "accounting")]
    [InlineData(@"srv1c:1541\acc", "srv1c:1541", "acc")]
    [InlineData(@"srv1,srv2\acc", "srv1,srv2", "acc")]
    [InlineData(@"srv\a\b", "srv", @"a\b")]
    [InlineData("srv", "srv", "")]
    public void ServerLocationSplitsAtTheFirstBackslash(string location, string cluster, string infobase)
    {
        Assert.Equal((cluster, infobase), BaseEdit.SplitServer(location));
        if (infobase.Length > 0) Assert.Equal(location, BaseEdit.ServerLocation(cluster, infobase));
    }

    [Fact]
    public void TwoFieldsMakeTheConnectionOfTheList()
    {
        var entry = new BaseEntry { Name = "Бух", Kind = ConnectionKind.Server, Location = BaseEdit.ServerLocation("  srv1c:1541 ", " accounting ") };
        entry.Validate();
        Assert.Equal("Srvr=\"srv1c:1541\";Ref=\"accounting\";", BaseClipboard.ConnectionString(entry));
    }

    // Codex audit 13.10: untouched fields keep the connection as it was, so saving only a comment changes nothing there and a
    // connection changed in another window meanwhile stays.
    [Fact]
    public void UntouchedServerFieldsKeepTheConnection()
    {
        var shown = new BaseEntry { Name = "Бух", Kind = ConnectionKind.Server, Location = "srv\\ acc" };
        var (cluster, infobase) = BaseEdit.SplitServer(shown.Location);
        var returned = BaseEdit.AsDialogReturns(shown) with { Comment = "заметка", Location = BaseEdit.ServerLocation(cluster, infobase, shown.Location) };
        Assert.Equal("srv\\ acc", returned.Location);
        var now = shown with { Location = "newserver\\newbase" };
        Assert.Equal("newserver\\newbase", BaseEdit.OnCurrent([now], BaseEdit.AsDialogReturns(shown), returned)!.Location);
        // A field the user edited counts.
        Assert.Equal("srv\\acc2", BaseEdit.ServerLocation(cluster, "acc2", shown.Location));
    }

    [Theory]
    [InlineData("", "acc")]
    [InlineData("srv", " ")]
    [InlineData(@"srv\x", "acc")]
    public void IncompleteOrAmbiguousServerIsRefused(string cluster, string infobase) =>
        Assert.Throws<ArgumentException>(() => BaseEdit.ServerLocation(cluster, infobase));
}
