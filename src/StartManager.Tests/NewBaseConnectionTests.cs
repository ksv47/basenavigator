using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

// The place and the string of the new base window (Codex audit 27.10, P2): the request never names a stale base. Synthetic names only.
public sealed class NewBaseConnectionTests
{
    private const string Full = "Srvr='demo-server';Ref='Nordwind';DBMS='PostgreSQL';DBSrvr='db-test';DB='nordwind';";

    private static string Target(NewBaseConnection state)
    {
        var (location, connection) = state.ForRequest();
        return InfobaseCreation.ToEntry(new NewBaseRequest { Client = "x", Kind = ConnectionKind.Server, Location = location, ConnectionString = connection }, "x", "", "").Location;
    }

    [Fact] public void StringIsMadeFromThePlace()
    {
        var state = new NewBaseConnection(ConnectionKind.Server);
        Assert.Equal("Srvr='';Ref='';", state.Text);
        Assert.False(state.SetLocation(@"demo-server\Nord'wind"));
        Assert.Equal("Srvr='demo-server';Ref='Nord''wind';", state.Text);
        Assert.False(state.Edited); Assert.False(state.Editable);
        Assert.Equal((@"demo-server\Nord'wind", ""), state.ForRequest());
        var file = new NewBaseConnection(ConnectionKind.File);
        file.SetLocation(@"D:\Bases\Demo"); file.BeginEdit(); file.EditText("x");
        Assert.Equal(@"File='D:\Bases\Demo';", file.Text);
        Assert.False(file.Editable); Assert.Equal((@"D:\Bases\Demo", ""), file.ForRequest());
    }

    [Fact] public void FullPasteThenPlaceEditTargetsTheNewPlace()
    {
        var state = new NewBaseConnection(ConnectionKind.Server);
        state.Paste(BaseClipboard.Paste(Full, ConnectionKind.Server));
        Assert.True(state.Edited); Assert.True(state.Editable);
        Assert.Equal(Full, state.Text);
        Assert.Equal(@"demo-server\Nordwind", state.Location);
        Assert.Equal(("", Full), state.ForRequest());
        Assert.Equal(@"demo-server\Nordwind", Target(state));
        // The user types another place: the string is made from it again, read-only; the caller is told it was dropped.
        Assert.True(state.SetLocation(@"srv-other\Demo"));
        Assert.False(state.Edited); Assert.False(state.Editable);
        Assert.Equal("Srvr='srv-other';Ref='Demo';", state.Text);
        Assert.Equal((@"srv-other\Demo", ""), state.ForRequest());
        Assert.Equal(@"srv-other\Demo", Target(state));
        Assert.Equal(@"srv-other\Demo", state.ConnectTarget());
        Assert.False(state.SetLocation(@"srv-other\Demo2"));
    }

    [Fact] public void PencilEditThenPlaceEditTargetsTheNewPlace()
    {
        var state = new NewBaseConnection(ConnectionKind.Server);
        state.SetLocation(@"demo-server\Nordwind");
        state.EditText("Srvr='srv-x';Ref='y';");
        Assert.False(state.Edited);   // read-only until the pencil
        state.BeginEdit();
        state.EditText(Full.Replace("Nordwind", "Edited"));
        Assert.True(state.Edited);
        Assert.Equal(@"demo-server\Edited", state.ConnectTarget());
        Assert.Equal(@"demo-server\Edited", Target(state));
        Assert.True(state.SetLocation(@"srv-other\Demo"));
        Assert.Equal(@"srv-other\Demo", Target(state));
        Assert.Equal(@"srv-other\Demo", state.ConnectTarget());
    }

    [Fact] public void SimplePasteAfterFullPasteGivesTheMadeReadOnlyString()
    {
        var state = new NewBaseConnection(ConnectionKind.Server);
        state.Paste(BaseClipboard.Paste(Full, ConnectionKind.Server));
        state.Paste(BaseClipboard.Paste("Srvr=\"srv-other\";Ref=\"Demo\";", ConnectionKind.Server));
        Assert.False(state.Edited); Assert.False(state.Editable);
        Assert.Equal("Srvr='srv-other';Ref='Demo';", state.Text);
        Assert.Equal(@"srv-other\Demo", Target(state));
    }

    [Fact] public void EditedStringWithoutABaseIsRefusedOnConnect()
    {
        var state = new NewBaseConnection(ConnectionKind.Server);
        state.BeginEdit(); state.EditText("DBMS='PostgreSQL';");
        Assert.Throws<ArgumentException>(() => state.ConnectTarget());
    }
}
