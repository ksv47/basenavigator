using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartManager.Core;

public sealed record Favorite(string Source, Guid Id)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record LaunchRecord(string Source, Guid Id, string Name, bool Designer, DateTimeOffset Time)
{
    public bool PersistentIdentity { get; init; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
/// <summary>List view of one source: the selected row and closed groups, restored on the next start.</summary>
public sealed record ListView(string Source)
{
    public string? Selected { get; init; }
    public List<string> Collapsed { get; init; } = [];
    public List<string> CollapsedSections { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record Activity
{
    public int Version { get; init; } = 1;
    public List<Favorite> Favorites { get; init; } = [];
    public List<LaunchRecord> History { get; init; } = [];
    [JsonConverter(typeof(ListViewsConverter))] public List<ListView> Views { get; init; } = [];
    /// <summary>Sorting and visible columns, common to all lists as in the original.</summary>
    [JsonConverter(typeof(ListColumnsConverter))] public ListColumns Columns { get; init; } = new();
    /// <summary>Where the file was last written; detects a moved portable folder.</summary>
    public string? ProfilePath { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The view is a convenience: a damaged record is dropped instead of blocking favorites and history.</summary>
public sealed class ListViewsConverter : JsonConverter<List<ListView>>
{
    public override List<ListView> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var views = new List<ListView>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return views;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            ListView? view;
            try { view = item.Deserialize<ListView>(); }
            catch (JsonException) { continue; }
            if (view is { Source.Length: > 0, Collapsed: not null, CollapsedSections: not null } &&
                !view.Collapsed.Contains(null!) && !view.CollapsedSections.Contains(null!)) views.Add(view);
        }
        return views;
    }

    public override void Write(Utf8JsonWriter writer, List<ListView> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var view in value) JsonSerializer.Serialize(writer, view);
        writer.WriteEndArray();
    }
}

/// <summary>Local preferences, independent of the shared list. Mutations reread under an exclusive lock.</summary>
public sealed class ActivityStore(string settingsPath)
{
    public string PathName { get; } = Path.GetFullPath(settingsPath) + ".activity.json";
    public static string Source(string settingsPath, string listPath)
    {
        var path = Path.GetFullPath(string.IsNullOrEmpty(listPath) ? settingsPath : listPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return (string.IsNullOrEmpty(listPath) ? "local:" : "v8i:") + path;
    }

    public Activity Load() => Relocated(Read(), null);

    private Activity Read()
    {
        if (!File.Exists(PathName)) return new();
        Activity data;
        try
        {
            data = JsonSerializer.Deserialize<Activity>(File.ReadAllText(PathName))
                ?? throw new InvalidDataException("Пустой файл избранного и истории.");
            if (data.Views is null) data = data with { Views = [] };
            if (data.Columns is null) data = data with { Columns = new() };
        }
        catch (JsonException ex) { throw new InvalidDataException("Файл избранного и истории содержит некорректный JSON.", ex); }
        Validate(data);
        return data;
    }

    private Activity Relocated(Activity data, ProfileRelocation? profileMove)
    {
        var keys = data.Favorites.Select(x => x.Source).Concat(data.History.Select(x => x.Source)).Concat(data.Views.Select(x => x.Source)).ToArray();
        var moved = ProfileRelocation.Detect(data.ProfilePath, keys, settingsPath);
        // Files of 0.2.0.19 without any local record carry no anchor; use the move of the same profile.
        if (moved is null && data.ProfilePath is null && !keys.Any(x => x.StartsWith("local:", StringComparison.Ordinal)))
            moved = profileMove ?? ProfileAnchor();
        return moved is null ? data : moved.Apply(data);
    }

    // Read with the profile's own rules (BOM, name case); a damaged profile gives no anchor.
    private ProfileRelocation? ProfileAnchor()
    {
        try
        {
            return LibraryStore.ReadStored(settingsPath) is { } profile
                ? ProfileRelocation.Detect(profile.ProfilePath, profile.Sources.Keys, settingsPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>Rewrites the history for a profile that has just been moved, before the profile gets its new anchor.</summary>
    public void Adopt(ProfileRelocation profileMove) => Change(data => data, profileMove);

    private static void Validate(Activity data)
    {
        if (data.Version != 1 || data.Favorites is null || data.History is null ||
            data.Favorites.Any(x => x is null || string.IsNullOrEmpty(x.Source)) ||
            data.History.Any(x => x is null || string.IsNullOrEmpty(x.Source) || x.Name is null))
            throw new InvalidDataException("Повреждён файл избранного и истории или его версия не поддерживается.");
    }

    public Activity SetFavorite(string source, Guid id, bool enabled) => Change(data =>
    {
        var favorites = data.Favorites.Where(x => x.Source != source || x.Id != id).ToList();
        if (enabled) favorites.Add(data.Favorites.FirstOrDefault(x => x.Source == source && x.Id == id) ?? new(source, id));
        return data with { Favorites = favorites };
    });

    public Activity Record(string source, BaseEntry entry, bool designer, bool persistentIdentity = true) => Change(data => data with
    {
        History = new[] { new LaunchRecord(source, entry.Id, entry.Name, designer, DateTimeOffset.UtcNow) { PersistentIdentity = persistentIdentity } }
            .Concat(data.History).Take(100).ToList()
    });

    public Activity SaveView(ListView view, ListColumns? columns = null) => Change(data => data with
    {
        Views = [.. data.Views.Where(x => x.Source != view.Source),
            view with { Extra = data.Views.FirstOrDefault(x => x.Source == view.Source)?.Extra ?? view.Extra }],
        Columns = columns is null ? data.Columns : columns with { Extra = data.Columns.Extra ?? columns.Extra }
    });

    public Activity AddFavorites(string source, IEnumerable<Guid> ids) => Change(data =>
    {
        var favorites = data.Favorites.ToList();
        foreach (var id in ids.Distinct())
            if (!favorites.Any(x => x.Source == source && x.Id == id)) favorites.Add(new(source, id));
        return data with { Favorites = favorites };
    });

    /// <summary>Toggles the mark by the file read under the lock: another window may have changed it a moment ago.</summary>
    public (Activity Activity, bool Enabled) ToggleFavorite(string source, Guid id, bool removeOnly)
    {
        var enabled = false;
        var result = Change(data =>
        {
            enabled = !removeOnly && !data.Favorites.Any(x => x.Source == source && x.Id == id);
            var favorites = data.Favorites.Where(x => x.Source != source || x.Id != id).ToList();
            if (enabled) favorites.Add(new(source, id));
            return data with { Favorites = favorites };
        });
        return (result, enabled);
    }

    private Activity Change(Func<Activity, Activity> update, ProfileRelocation? profileMove = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        // Keep the lock file in place: unlinking it allows a second lock on a different inode on Linux.
        using var gate = FilePersistence.Lock(PathName);
        // Windows of this program wait for the lock; an editor does not. A file changed while the update was being made is
        // read again and the update applied to the fresh text, so that edit is not overwritten.
        for (var attempt = 1; ; attempt++)
        {
            var previous = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
            var data = update(Relocated(Read(), profileMove)) with { ProfilePath = Path.GetFullPath(settingsPath) };
            Validate(data);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
            BeforeWrite?.Invoke();
            var now = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
            if (!Same(previous, now))
            {
                if (attempt < 3) continue;
                throw new IOException("Файл избранного и истории всё время меняет другая программа. Повторите позже.");
            }
            FilePersistence.Replace(FilePersistence.Target(PathName), bytes, previous is null ? null : PathName,
                () => { if (previous is not null) FilePersistence.Replace(PathName + ".bak", previous, PathName); });
            return data;
        }
    }

    /// <summary>Tests only: runs between building the new text and checking the file on disk.</summary>
    internal Action? BeforeWrite { get; set; }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

}
