namespace StartManager.Core;

/// <summary>The place and the connection string of the new base window: the string is made from the place until it is edited (the
/// pencil, a server only) or pasted whole with parameters the place cannot hold. The place is the visible target: a later edit of it
/// by the user makes the string from it again, so that a stale edited string never names another base (Codex audit 27.10, P2).</summary>
public sealed class NewBaseConnection(ConnectionKind kind)
{
    private readonly bool onServer = kind == ConnectionKind.Server;
    private string location = "";

    public string Location => location;
    /// <summary>The string as shown: made from the place (values in single quotes, as it goes to 1C) or the edited one.</summary>
    public string Text { get; private set; } = Make(kind == ConnectionKind.Server, "");
    /// <summary>The string is the user's (edited or pasted whole); the place is not sent then.</summary>
    public bool Edited { get; private set; }
    /// <summary>The string field accepts typing: after the pencil or a whole paste.</summary>
    public bool Editable { get; private set; }

    /// <summary>The place changed by the user. True when an edited string was dropped (the caller says so).</summary>
    public bool SetLocation(string value)
    {
        location = value;
        var dropped = Edited;
        Edited = false; Editable = false;
        Text = Make(onServer, location);
        return dropped;
    }

    /// <summary>The pencil: the string field accepts typing (a server only).</summary>
    public void BeginEdit() { if (onServer) Editable = true; }

    /// <summary>The string typed by the user in the open field.</summary>
    public void EditText(string value)
    {
        if (!onServer || !Editable) return;
        Text = value; Edited = true;
    }

    /// <summary>«Вставить»: the place, and the whole string when it has other parameters.</summary>
    public void Paste(PastedConnection pasted)
    {
        SetLocation(pasted.Location);
        if (!onServer || pasted.ConnectionString.Length == 0) return;
        Text = pasted.ConnectionString; Edited = true; Editable = true;
    }

    /// <summary>The place and the string of <see cref="NewBaseRequest"/>: one of them, never both stale.</summary>
    public (string Location, string ConnectionString) ForRequest() =>
        onServer && Edited ? ("", Text.Trim()) : (location.Trim(), "");

    /// <summary>The place of a connected existing base: an edited server string is what the user means — its server and name, as for a
    /// creation (<see cref="ArgumentException"/> when it names none). Not checked further here.</summary>
    public string ConnectTarget()
    {
        var (place, connection) = ForRequest();
        return connection.Length == 0 ? place
            : InfobaseCreation.ToEntry(new NewBaseRequest { Client = "x", Kind = ConnectionKind.Server, ConnectionString = connection }, "x", "", "").Location;
    }

    private static string Make(bool onServer, string location)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var text = location.Trim();
        if (!onServer) return $"File={Quote(text)};";
        return text.IndexOf('\\') is var split and > 0 ? $"Srvr={Quote(text[..split])};Ref={Quote(text[(split + 1)..])};" : $"Srvr={Quote(text)};Ref='';";
    }
}
