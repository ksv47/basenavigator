namespace StartManager.Core;

/// <summary>The original's search types (pbSearchType): by the name or by the connection string.</summary>
public enum SearchMode { Name, Connection }

public static class BaseSearch
{
    public static bool Matches(BaseEntry entry, string? query, SearchMode mode)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0) return true;
        var text = mode == SearchMode.Connection ? BaseClipboard.ConnectionString(entry) : entry.Name;
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
