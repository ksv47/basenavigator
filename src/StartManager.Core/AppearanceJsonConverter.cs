using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

// Cosmetic incompatibility must not block the library. Preserve the complete original
// subtree on unrelated saves; only an explicit style save/reset replaces invalid values.
public sealed class AppearanceJsonConverter : JsonConverter<AppearanceSettings>
{
    private static readonly string[] Known = ["Theme", "List", "Interface", "Headers", "Dialogs", "MinimumRowHeight", "SelectionIntensity", "ListTooltips", "GroupsOnTop"];
    private static readonly string[] FontKnown = ["Family", "Size"];
    public override bool HandleNull => true;
    public override AppearanceSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var raw = document.RootElement;
        var defaults = new AppearanceSettings(); var invalid = raw.ValueKind != JsonValueKind.Object;
        JsonElement? Property(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
            ? parent.EnumerateObject().Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => (JsonElement?)x.Value).LastOrDefault() : null;
        double Number(JsonElement? node, double fallback, double minimum, double maximum)
        {
            if (node is null) return fallback;
            if (node.Value.ValueKind == JsonValueKind.Number && node.Value.TryGetDouble(out var number) &&
                double.IsFinite(number) && number >= minimum && number <= maximum) return number;
            invalid = true; return fallback;
        }
        Dictionary<string, JsonElement>? Extra(JsonElement node, string[] known)
        {
            if (node.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, JsonElement>();
            foreach (var property in node.EnumerateObject())
                if (!known.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) result[property.Name] = property.Value.Clone();
            return result.Count == 0 ? null : result;
        }
        FontPreference Font(string name, FontPreference fallback)
        {
            var node = Property(raw, name);
            if (node is null) return fallback;
            if (node.Value.ValueKind != JsonValueKind.Object) { invalid = true; return fallback; }
            var family = fallback.Family; var field = Property(node.Value, "Family");
            if (field is { } value)
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 256 } text && !text.Any(char.IsControl)) family = text;
                else invalid = true;
            }
            return fallback with { Family = family, Size = Number(Property(node.Value, "Size"), fallback.Size, 10, 24), Extra = Extra(node.Value, FontKnown) };
        }
        // The slider gives whole steps; a fraction is not a value of this program and is kept as unrecognized text.
        double Whole(double value, double fallback)
        {
            if (value == Math.Floor(value)) return value;
            invalid = true; return fallback;
        }
        bool Flag(string name, bool fallback)
        {
            if (Property(raw, name) is not { } node) return fallback;
            if (node.ValueKind is JsonValueKind.True or JsonValueKind.False) return node.GetBoolean();
            invalid = true; return fallback;
        }
        var theme = defaults.Theme;
        if (Property(raw, "Theme") is { } themeNode)
        {
            if (themeNode.ValueKind == JsonValueKind.Number && themeNode.TryGetInt32(out var number) && Enum.IsDefined((AppearanceTheme)number)) theme = (AppearanceTheme)number;
            else invalid = true;
        }
        var result = defaults with
        {
            Theme = theme, List = Font("List", defaults.List), Interface = Font("Interface", defaults.Interface),
            Headers = Font("Headers", defaults.Headers), Dialogs = Font("Dialogs", defaults.Dialogs),
            MinimumRowHeight = Number(Property(raw, "MinimumRowHeight"), defaults.MinimumRowHeight, 20, 64),
            SelectionIntensity = Whole(Number(Property(raw, "SelectionIntensity"), defaults.SelectionIntensity, 0, 255), defaults.SelectionIntensity),
            ListTooltips = Flag("ListTooltips", defaults.ListTooltips), GroupsOnTop = Flag("GroupsOnTop", defaults.GroupsOnTop), Extra = Extra(raw, Known)
        };
        return invalid ? result with
        {
            UnrecognizedSource = raw.Clone(),
            LoadWarning = "Часть оформления не поддерживается. Использованы безопасные значения; исходный профиль сохранён до изменения через «Стиль»."
        } : result;
    }
    public override void Write(Utf8JsonWriter writer, AppearanceSettings value, JsonSerializerOptions options)
    {
        if (value.UnrecognizedSource is { } original) { original.WriteTo(writer); return; }
        value.Validate();
        writer.WriteStartObject(); writer.WriteNumber("Theme", (int)value.Theme);
        foreach (var (name, font) in new[] { ("List", value.List), ("Interface", value.Interface), ("Headers", value.Headers), ("Dialogs", value.Dialogs) })
        {
            writer.WritePropertyName(name); JsonSerializer.Serialize(writer, font, options);
        }
        writer.WriteNumber("MinimumRowHeight", value.MinimumRowHeight);
        writer.WriteNumber("SelectionIntensity", value.SelectionIntensity);
        writer.WriteBoolean("ListTooltips", value.ListTooltips); writer.WriteBoolean("GroupsOnTop", value.GroupsOnTop);
        if (value.Extra is not null)
            foreach (var (name, extra) in value.Extra)
                if (!Known.Contains(name, StringComparer.OrdinalIgnoreCase)) { writer.WritePropertyName(name); extra.WriteTo(writer); }
        writer.WriteEndObject();
    }
}
