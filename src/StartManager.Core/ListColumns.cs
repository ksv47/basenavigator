using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ListColumn>))]
public enum ListColumn { Name, Platform, Date, Size }

/// <summary>Sorting and visible columns of the list, as the original's header (sort glyphs, «Колонки»).</summary>
public sealed record ListColumns
{
    public ListColumn Sort { get; init; } = ListColumn.Name;
    public bool Descending { get; init; }
    /// <summary>Hidden columns; the name is always shown.</summary>
    public List<ListColumn> Hidden { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public bool Visible(ListColumn column) => column == ListColumn.Name || !Hidden.Contains(column);

    /// <summary>A click on the header: the same column reverses the order, another one sorts ascending.</summary>
    public ListColumns SortBy(ListColumn column) => !Visible(column) ? this
        : Sort == column ? this with { Descending = !Descending } : this with { Sort = column, Descending = false };

    /// <summary>Shows or hides a column; a hidden sort column returns the sort to the name.</summary>
    public ListColumns Toggle(ListColumn column)
    {
        if (column == ListColumn.Name) return this;
        if (!Visible(column)) return this with { Hidden = [.. Hidden.Where(x => x != column)] };
        var hidden = this with { Hidden = [.. Hidden.Append(column).Order()] };
        return Sort == column ? hidden with { Sort = ListColumn.Name, Descending = false } : hidden;
    }

    /// <summary>Bases in the chosen order; unknown values (not yet read, no file, no platform) go last, ties by name.</summary>
    public IEnumerable<BaseEntry> Order(IEnumerable<BaseEntry> entries, Func<Guid, BaseInfo?> info)
    {
        var names = StringComparer.CurrentCultureIgnoreCase;
        if (Sort == ListColumn.Name)
            return Descending ? entries.OrderByDescending(x => x.Name, names) : entries.OrderBy(x => x.Name, names);
        IComparable? Key(BaseEntry entry) => info(entry.Id) is not { } data ? null : Sort switch
        {
            ListColumn.Platform => PlatformVersion(data.Platform),
            ListColumn.Date => data.Modified,
            ListColumn.Size => data.Size,
            _ => null
        };
        var keyed = entries.Select(x => (Entry: x, Key: Key(x))).ToList();
        var known = keyed.Where(x => x.Key is not null);
        var ordered = Descending ? known.OrderByDescending(x => x.Key) : known.OrderBy(x => x.Key);
        return ordered.ThenBy(x => x.Entry.Name, names).Select(x => x.Entry)
            .Concat(keyed.Where(x => x.Key is null).OrderBy(x => x.Entry.Name, names).Select(x => x.Entry));
    }

    // "8.3.27.2214", "8.3", "8.3.27 *" (the version of the record when the client is not found); other texts are unknown.
    private static Version? PlatformVersion(string text)
    {
        var first = text.Split(' ', 2)[0];
        if (!first.Contains('.')) return null;
        return Version.TryParse(first, out var version) ? version : null;
    }
}

/// <summary>The columns are a convenience: a damaged or unknown value of a known field gives its default instead of
/// blocking favorites and history; the other fields, for example of a newer version, are kept.</summary>
public sealed class ListColumnsConverter : JsonConverter<ListColumns>
{
    public override ListColumns Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
        static ListColumn? Column(JsonElement value) => value.ValueKind == JsonValueKind.String &&
            Enum.GetNames<ListColumn>().FirstOrDefault(x => x.Equals(value.GetString(), StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<ListColumn>(name) : null;
        var sort = ListColumn.Name; var descending = false; var hidden = new List<ListColumn>();
        var extra = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject())
            switch (property.Name)
            {
                case nameof(ListColumns.Sort): sort = Column(property.Value) ?? ListColumn.Name; break;
                case nameof(ListColumns.Descending): descending = property.Value.ValueKind == JsonValueKind.True; break;
                case nameof(ListColumns.Hidden):
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in property.Value.EnumerateArray())
                            if (Column(item) is { } column && column != ListColumn.Name && !hidden.Contains(column)) hidden.Add(column);
                    break;
                default: extra[property.Name] = property.Value.Clone(); break;
            }
        var columns = new ListColumns { Sort = sort, Descending = descending, Hidden = hidden, Extra = extra.Count > 0 ? extra : null };
        return columns.Visible(sort) ? columns : columns with { Sort = ListColumn.Name, Descending = false };
    }

    public override void Write(Utf8JsonWriter writer, ListColumns value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value);
}
