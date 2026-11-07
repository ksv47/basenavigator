using System.Data.Common;
using System.Text;

namespace StartManager.Core;

public sealed record ImportResult(Library Library, int Added, int Duplicates, IReadOnlyList<string> Warnings);

public static class V8iImporter
{
    public static string ReadText(string path)
    {
        try { return File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return File.ReadAllText(path, Encoding.GetEncoding(1251));
        }
    }

    public static ImportResult Import(Library source, string text)
    {
        var entries = source.Bases.ToList();
        var groups = new HashSet<string>(Groups.All(source), StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        int added = 0, duplicates = 0;
        string? name = null;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Finish()
        {
            if (name is null) return;
            try
            {
                var group = Groups.Normalize(properties.GetValueOrDefault("Folder", ""));
                if (!properties.TryGetValue("Connect", out var connection))
                {
                    var folder = Groups.Normalize(group + "/" + name);
                    if (folder.Length == 0) throw new ArgumentException("Пустое имя группы.");
                    groups.Add(folder); return;
                }
                if (properties.GetValueOrDefault("Version", "").StartsWith("7.")) throw new ArgumentException("Поддерживается только платформа 1С 8.x.");
                var parts = new DbConnectionStringBuilder { ConnectionString = connection };
                string Part(string key) => parts.TryGetValue(key, out var value) ? value.ToString()! : "";
                var file = Part("File"); var server = Part("Srvr"); var reference = Part("Ref"); var web = Part("ws");
                if (file.Length == 0 && web.Length == 0 && (server.Length == 0 || reference.Length == 0))
                    throw new ArgumentException("Поддерживаются только File, Srvr/Ref или ws.");
                if (new[] { file.Length > 0, server.Length > 0 || reference.Length > 0, web.Length > 0 }.Count(x => x) > 1)
                    throw new ArgumentException("Неоднозначная строка подключения.");
                var kind = file.Length > 0 ? ConnectionKind.File : web.Length > 0 ? ConnectionKind.Web : ConnectionKind.Server;
                var entry = new BaseEntry { Name = name, Group = group, Kind = kind,
                    Location = kind switch { ConnectionKind.File => file, ConnectionKind.Web => web, _ => server + "\\" + reference },
                    PlatformVersion = ReadPlatformVersion(properties), ClientType = properties.GetValueOrDefault("App", "").Trim(),
                    ListArchitecture = PlatformCatalog.ParseBitness(properties.GetValueOrDefault("AppArch", "")),
                    AskCredentials = properties.GetValueOrDefault("WA", "").Trim() == "0",
                    WebServerAskCredentials = properties.GetValueOrDefault("WSA", "").Trim() switch { "0" => false, "1" => true, _ => null },
                    // «Normal» and «Low»; no line (or «Auto») is «Выбирать при запуске»; another value is kept apart (null), so that choosing
                    // a speed replaces it (Grok audit 14.10). UseProxy= 0, 1 or 2.
                    ConnectionSpeed = properties.GetValueOrDefault("ClientConnectionSpeed", "").Trim() switch
                    {
                        var x when x.Equals("Normal", StringComparison.OrdinalIgnoreCase) => "Normal", var x when x.Equals("Low", StringComparison.OrdinalIgnoreCase) => "Low",
                        var x when x.Length == 0 || x.Equals("Auto", StringComparison.OrdinalIgnoreCase) => "", _ => null
                    },
                    ListUseProxy = Clean(properties.GetValueOrDefault("UseProxy", "").Trim()),
                    UseProxy = properties.GetValueOrDefault("UseProxy", "").Trim() switch { "0" => ProxyMode.None, "1" => ProxyMode.Auto, "2" => ProxyMode.Manual, _ => null },
                    ProxyServer = Clean(properties.GetValueOrDefault("ProxyServer", "")), ProxyPort = Clean(properties.GetValueOrDefault("ProxyPort", "")),
                    ProxyUser = Clean(properties.GetValueOrDefault("ProxyUser", "")), ListProxyPassword = Clean(properties.GetValueOrDefault("ProxyPassword", "")) };
                entry.Validate();
                if (entries.Any(x => SameConnection(x, entry))) { duplicates++; return; }
                entries.Add(entry); added++;
                if (group.Length > 0) groups.Add(group);
            }
            catch (ArgumentException ex) { warnings.Add($"{name}: {ex.Message}"); }
        }
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { Finish(); name = line[1..^1]; properties.Clear(); }
            else if (name is not null && line.IndexOf('=') is var index && index > 0) properties[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        Finish();
        return new(source with { Bases = entries, Groups = groups.ToList() }, added, duplicates, warnings);
    }

    // A value 1C could not take (a tab inside) is not used; the line itself stays in the list as it is.
    private static string Clean(string value) => value.Any(char.IsControl) ? "" : value;

    // Version is the selection rule of 1CEStart; DefaultVersion is a service field that may name a removed build.
    private static string ReadPlatformVersion(IReadOnlyDictionary<string, string> properties)
    {
        var version = properties.GetValueOrDefault("Version", "");
        var fallback = properties.GetValueOrDefault("DefaultVersion", "");
        return PlatformCatalog.IsVersion(version) || !PlatformCatalog.IsVersion(fallback) && version.Length > 0 ? version : fallback;
    }

    private static bool SameConnection(BaseEntry left, BaseEntry right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind == ConnectionKind.Web) return BaseEntry.SameWebAddress(left.Location, right.Location);
        var comparison = left.Kind != ConnectionKind.File || OperatingSystem.IsWindows() || right.Location.Contains('\\') || right.Location.Contains(':')
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return left.Location.TrimEnd('\\', '/').Equals(right.Location.TrimEnd('\\', '/'), comparison);
    }
}
