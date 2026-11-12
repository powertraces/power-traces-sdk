using System.Text.Json;

namespace PowerTraces.Storage;

/// <summary>Turns an attribute JSON blob into the (key, text value) pairs providers index for
/// attribute filtering. A string attribute's text is the string itself; anything else (number,
/// boolean, array, object) is its compact JSON text — so <c>http.response.status_code = 500</c>
/// matches the filter value <c>"500"</c> whether the attribute was recorded as a number or a
/// string. Null values aren't indexed (a null never equals or contains anything).</summary>
public static class AttributeText
{
    /// <summary>Longest key stored in an attribute index — longer keys are truncated. Sized so
    /// (key, value, timestamp) fits in one B-tree index entry on every supported database.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Longest value stored in an attribute index — longer values are truncated, so an
    /// equality filter compares the first <see cref="MaxValueLength"/> characters and a
    /// contains-filter only searches that prefix.</summary>
    public const int MaxValueLength = 400;

    public static IReadOnlyList<KeyValuePair<string, string>> Flatten(string? attributesJson)
    {
        if (string.IsNullOrEmpty(attributesJson) || attributesJson == "{}")
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(attributesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var list = new List<KeyValuePair<string, string>>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => property.Value.GetString(),
                    _ => property.Value.GetRawText(),
                };

                if (text is not null)
                {
                    list.Add(new(Truncate(property.Name, MaxKeyLength), Truncate(text, MaxValueLength)));
                }
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The indexed form of a key or value — used on both the write side and when binding
    /// filter values, so a long filter value compares against the same truncated prefix that was
    /// stored.</summary>
    public static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public static IReadOnlyList<KeyValuePair<string, string>> ValuesOf(SpanRow row) =>
        row.AttributeValues ?? Flatten(row.AttributesJson);

    public static IReadOnlyList<KeyValuePair<string, string>> ValuesOf(LogRow row) =>
        row.AttributeValues ?? Flatten(row.AttributesJson);
}
