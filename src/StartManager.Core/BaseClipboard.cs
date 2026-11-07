using System.Data.Common;

namespace StartManager.Core;

/// <summary>The original's «Копировать» (tbClipboard): what can be copied of a base.</summary>
public enum BaseClipboardKind { Name, Id, Connection, Path }

public static class BaseClipboard
{
    /// <summary>Kinds applicable to the base, in the original's order; the path only for a file base.</summary>
    public static IReadOnlyList<BaseClipboardKind> Kinds(BaseEntry entry) => entry.Kind == ConnectionKind.File
        ? [BaseClipboardKind.Name, BaseClipboardKind.Id, BaseClipboardKind.Connection, BaseClipboardKind.Path]
        : [BaseClipboardKind.Name, BaseClipboardKind.Id, BaseClipboardKind.Connection];

    public static string Label(BaseClipboardKind kind) => kind switch
    {
        BaseClipboardKind.Name => "Копировать наименование",
        BaseClipboardKind.Id => "Копировать ID элемента",
        BaseClipboardKind.Connection => "Копировать строку соединения",
        BaseClipboardKind.Path => "Копировать путь к информационной базе",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The text for the clipboard; null when the kind does not apply (the path of a server base).</summary>
    public static string? Text(BaseEntry entry, BaseClipboardKind kind) => kind switch
    {
        BaseClipboardKind.Name => entry.Name,
        BaseClipboardKind.Id => entry.Id.ToString(),
        BaseClipboardKind.Connection => ConnectionString(entry),
        BaseClipboardKind.Path => entry.Kind == ConnectionKind.File ? entry.Location : null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The connection string as 1C writes it in Connect= and accepts in /IBConnectionString.</summary>
    public static string ConnectionString(BaseEntry entry)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        if (entry.Kind == ConnectionKind.File) return $"File={Quote(entry.Location)};";
        if (entry.Kind == ConnectionKind.Web) return $"ws={Quote(entry.Location)};";
        var split = entry.Location.IndexOf('\\');
        return split <= 0 ? $"Srvr={Quote(entry.Location)};"
            : $"Srvr={Quote(entry.Location[..split])};Ref={Quote(entry.Location[(split + 1)..])};";
    }

    /// <summary>The original's «Вставить» of the new base window: a connection string from the clipboard, as 1C writes it in Connect=
    /// (the <c>Connect=</c> prefix of a .v8i line is accepted). A server base gives <c>server\name</c>; when the string has other
    /// parameters (the DBMS, the database…) it is also returned whole, to be kept as an edited string. A file base gives the folder;
    /// its other parameters have no place in the window and are not taken. Throws <see cref="ArgumentException"/> with the message.</summary>
    public static PastedConnection Paste(string? text, ConnectionKind kind)
    {
        var value = text?.Trim() ?? "";
        if (value.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase)) value = value["Connect=".Length..].Trim();
        if (value.Length == 0) throw new ArgumentException("В буфере обмена нет текста.");
        if (value.Any(char.IsControl)) throw new ArgumentException("В буфере обмена не строка соединения: текст из нескольких строк.");
        DbConnectionStringBuilder parts;
        try { parts = new DbConnectionStringBuilder { ConnectionString = value }; }
        catch (ArgumentException) { throw new ArgumentException("В буфере обмена не строка соединения 1С."); }
        string Part(string key) => parts.TryGetValue(key, out var found) ? found?.ToString()?.Trim() ?? "" : "";
        if (kind == ConnectionKind.Server)
        {
            var server = Part("Srvr"); var name = Part("Ref");
            if (server.Length == 0 || name.Length == 0 || parts.ContainsKey("File") || parts.ContainsKey("ws"))
                throw new ArgumentException("В строке соединения из буфера обмена нет базы на сервере: нужны Srvr и Ref.");
            if (server.Contains('\\')) throw new ArgumentException("Имя сервера в строке соединения не должно содержать «\\».");
            return new PastedConnection(server + "\\" + name, parts.Count > 2 ? value : "");
        }
        var folder = Part("File");
        if (kind != ConnectionKind.File || folder.Length == 0 || parts.ContainsKey("Srvr") || parts.ContainsKey("Ref") || parts.ContainsKey("ws"))
            throw new ArgumentException("В строке соединения из буфера обмена нет файловой базы: нужен File.");
        // Usr=, Pwd= and the like are not taken; the window says so without naming the values (Grok audit 27.10, P3).
        return new PastedConnection(folder, "", parts.Count > 1 ? IgnoredFileParameters : null);
    }

    public const string IgnoredFileParameters = "Из строки соединения взят только каталог базы; остальные её параметры (пользователь, пароль и др.) не используются.";
}

/// <param name="Location">The field of the window: <c>server\name</c> or the folder.</param>
/// <param name="ConnectionString">The whole string when it has parameters the field cannot hold; otherwise empty.</param>
/// <param name="Notice">What of the string was not taken (never its values), or null.</param>
public sealed record PastedConnection(string Location, string ConnectionString, string? Notice = null);
