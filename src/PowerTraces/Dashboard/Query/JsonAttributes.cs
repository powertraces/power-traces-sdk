using System.Text.Json;

namespace PowerTraces.Dashboard.Query;

/// <summary>Parses the JSON blobs every storage provider writes (attributes/events/links) back
/// into the shapes <see cref="IDashboardStore"/> implementations return — shared so each provider
/// package doesn't reimplement the same two lines.</summary>
public static class JsonAttributes
{
    // Events and links are stored camelCase ("name", "time", "traceId") while the DTO records are
    // PascalCase, so binding must ignore case or every field silently comes back null/default.
    private static readonly JsonSerializerOptions ListOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses stored attribute JSON — plain or <see cref="Storage.StoredJson">compressed</see>.</summary>
    public static Dictionary<string, object?> Parse(string? stored) =>
        Storage.StoredJson.Unpack(stored) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? []
            : [];

    /// <summary>Parses a stored event/link list — plain or <see cref="Storage.StoredJson">compressed</see>.</summary>
    public static List<T> ParseList<T>(string? stored) =>
        Storage.StoredJson.Unpack(stored) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<T>>(json, ListOptions) ?? []
            : [];
}
