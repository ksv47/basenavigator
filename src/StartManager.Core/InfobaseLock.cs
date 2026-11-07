using System.Globalization;
using System.Text;

namespace StartManager.Core;

/// <summary>The lock of a file infobase: the file 1Cv8.cdn in the base folder, as the platform and the standard subsystems
/// write it: <c>{1,yyyyMMddHHmmss,yyyyMMddHHmmss,"message","code","parameter"[,more]}</c>, UTF-8 with BOM, quotes doubled
/// inside strings, CRLF inside the message, 00010101000000 for a date that is not set.</summary>
/// <param name="On">The first field: 1 the lock is set; a file with 0 is shown, not treated as a lock.</param>
/// <param name="Parameter">The standard subsystems keep their data here; kept as read when the lock is changed.</param>
/// <param name="Tail">The fields after the parameter (platform 8.3 adds one), written back as they were read.</param>
/// <param name="Framed">The message is written between the original's lines of «=»; a lock set by 1С itself keeps its text as it is.</param>
public sealed record InfobaseLock(bool On, DateTime? Start, DateTime? End, string Message, string Code, string Parameter = "", string Tail = "", bool Framed = true)
{
    public const string FileName = "1Cv8.cdn";
    private const string NoDate = "00010101000000";
    // The original frames the text with these lines; reading removes them, so a reopened lock shows the text as typed.
    private static readonly string Frame = new('=', 50);

    public static string PathIn(string folder) => Path.Combine(folder.Trim(), FileName);

    /// <summary>The lock of the base in the folder; null when there is no lock file.</summary>
    /// <exception cref="InvalidDataException">The file is not a lock record.</exception>
    public static InfobaseLock? Read(string folder)
    {
        var path = PathIn(folder);
        if (!File.Exists(path)) return null;
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static InfobaseLock Parse(string text)
    {
        var fields = new List<(string Value, bool Quoted, string Raw)>();
        var i = 0; var lenient = false;
        void Blank() { while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '﻿')) i++; }
        Blank();
        if (i >= text.Length || text[i] != '{') throw Bad();
        i++;
        while (true)
        {
            Blank();
            if (i >= text.Length) throw Bad();
            var begin = i;
            if (text[i] == '"')
            {
                var value = new StringBuilder(); i++;
                while (true)
                {
                    if (i >= text.Length) throw Bad();
                    if (text[i] != '"') { value.Append(text[i++]); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i += 2; continue; }
                    // The original wrote quotes undoubled: a quote ends the string only before a comma or the closing brace.
                    var next = i + 1;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next >= text.Length || text[next] is ',' or '}') { i++; break; }
                    value.Append(text[i++]); lenient = true;
                }
                fields.Add((value.ToString(), true, text[begin..i]));
            }
            else if (text[i] == '{')
            {
                // A nested record in the tail is kept as raw text.
                var depth = 0; var quoted = false;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '"') quoted = !quoted;
                    else if (!quoted && text[i] == '{') depth++;
                    else if (!quoted && text[i] == '}' && --depth == 0) { i++; break; }
                }
                if (depth != 0) throw Bad();
                if (fields.Count < 6) throw Bad();
                fields.Add((text[begin..i], false, text[begin..i]));
            }
            else
            {
                while (i < text.Length && text[i] is not (',' or '}')) i++;
                var raw = text[begin..i].Trim();
                if (raw.Length == 0) throw Bad();
                fields.Add((raw, false, raw));
            }
            Blank();
            if (i >= text.Length) throw Bad();
            if (text[i] == ',') { i++; continue; }
            if (text[i] == '}') { i++; break; }
            throw Bad();
        }
        Blank();
        // The shape of each field: flag and dates plain, message, code and parameter quoted. A file read only by allowing
        // undoubled quotes must be exactly the original's record (six fields, empty parameter); anything else could have
        // moved the code into another field, so it is called damaged rather than guessed.
        if (i != text.Length || fields.Count < 5 || fields[0].Quoted || fields[0].Value is not ("0" or "1") || fields[1].Quoted || fields[2].Quoted ||
            !fields[3].Quoted || !fields[4].Quoted || fields.Count > 5 && !fields[5].Quoted || lenient && (fields.Count != 6 || fields[5].Value.Length > 0))
            throw Bad();
        var (message, framed) = Unframe(fields[3].Value);
        return new InfobaseLock(fields[0].Value == "1", Date(fields[1].Value), Date(fields[2].Value), message, fields[4].Value,
            fields.Count > 5 ? fields[5].Value : "", string.Join(",", fields.Skip(6).Select(x => x.Raw)), framed);
    }

    private static InvalidDataException Bad() => new("Файл блокировки 1Cv8.cdn повреждён или имеет неизвестный формат.");

    private static DateTime? Date(string value)
    {
        if (value == NoDate) return null;
        if (value.Length == 14 && DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw Bad();
    }

    private static (string Message, bool Framed) Unframe(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        return lines.Length >= 2 && lines[0] == Frame && lines[^1] == Frame ? (string.Join("\r\n", lines[1..^1]), true) : (message, false);
    }

    /// <summary>The file text: the message framed as the original does, strings quoted with doubled quotes.</summary>
    public string Format()
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        static string Stamp(DateTime? date) => date?.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) ?? NoDate;
        var message = Message.ReplaceLineEndings("\r\n");
        if (Framed) message = Frame + "\r\n" + message + "\r\n" + Frame;
        return "{" + (On ? "1" : "0") + "," + Stamp(Start) + "," + Stamp(End) + "," + Quote(message) + "," + Quote(Code) + "," + Quote(Parameter) +
            (Tail.Length > 0 ? "," + Tail : "") + "}";
    }

    /// <summary>Writes the lock file through a temporary file in the same folder; the folder must exist.</summary>
    /// <param name="expected">The file text the change was made against (null: no file). It is compared once the new file is
    /// staged, right before the replacement; a program writing in that last moment can still be overwritten, as the file
    /// has no lock 1С would respect.</param>
    /// <exception cref="InfobaseLockChangedException">The file is not what the change was made against.</exception>
    public void Write(string folder, string? expected)
    {
        var path = PathIn(folder);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Каталог информационной базы не найден: " + folder.Trim());
        Check(path, expected);
        FilePersistence.Replace(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Format())], File.Exists(path) ? path : null, () => { Staged?.Invoke(path); Check(path, expected); });
    }

    /// <summary>Removes the lock: the file is deleted, as the platform and the original do. No file is not an error.</summary>
    /// <param name="expected">The file text the removal was asked for; see <see cref="Write"/>.</param>
    public static void Remove(string folder, string? expected)
    {
        var path = PathIn(folder);
        Check(path, expected);
        File.Delete(path);
    }

    // Tests: runs once the new file is staged, before the last comparison.
    internal static Action<string>? Staged;

    private static void Check(string path, string? expected)
    {
        if ((File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null) != expected) throw new InfobaseLockChangedException();
    }

    /// <summary>The message with the original's placeholders replaced: {ДатаНач}/{DateB} and {ДатаКон}/{DateE}.</summary>
    public static string Expand(string message, DateTime? start, DateTime? end)
    {
        static string Text(DateTime? date) => date?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "<Дата не определена>";
        foreach (var (key, date) in new[] { ("{ДатаНач}", start), ("{DateB}", start), ("{ДатаКон}", end), ("{DateE}", end) })
            message = message.Replace(key, Text(date), StringComparison.OrdinalIgnoreCase);
        return message;
    }

    /// <summary>The state for the page: active, waiting, expired or switched off.</summary>
    public string Describe(DateTime now) =>
        !On ? "В файле блокировка выключена." :
        End is { } end && end <= now ? $"Блокировка истекла {end:dd.MM.yyyy HH:mm:ss}." :
        Start is { } start && start > now ? $"Блокировка начнётся {start:dd.MM.yyyy HH:mm:ss}." :
        "Блокировка действует.";
}

/// <summary>The lock file was changed by 1С or another program after it was read.</summary>
public sealed class InfobaseLockChangedException() : IOException("Файл блокировки изменён другой программой после открытия страницы «Блокировка».");
