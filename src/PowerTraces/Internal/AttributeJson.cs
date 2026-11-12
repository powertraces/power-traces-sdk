using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerTraces.Internal;

/// <summary>Flattens OTel tag/attribute collections into the compact JSON blobs stored alongside
/// each row — same shape a reader can just JSON-parse back, no separate attributes table.</summary>
internal static class AttributeJson
{
    // Relaxed escaping: the default encoder writes quotes, < > & and every non-ASCII character as
    // \uXXXX, which makes stored values (a JSON response body, non-English text) unreadable to
    // anyone looking at the database. These strings are stored, never embedded in HTML.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>How attribute values that are .NET objects (a bound request model, an anonymous
    /// object, a logged <c>{@Value}</c>) are turned into JSON when the host sets no options of its
    /// own: ASP.NET Core's web defaults, so a request model is stored with the camelCase names its
    /// API reads and writes — not the C# property names. <c>[JsonPropertyName]</c> wins, and
    /// dictionary keys are kept exactly as they are.</summary>
    internal static readonly JsonSerializerOptions DefaultObjectOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="tags">The attributes. Keys are stored exactly as given, whatever their case;
    /// so are string values (a captured JSON body stays byte-for-byte what was received) and
    /// already-parsed JSON (<see cref="JsonElement"/>, <see cref="JsonNode"/>,
    /// <see cref="JsonDocument"/>).</param>
    /// <param name="objectOptions">Used for every other object value — see
    /// <see cref="PowerTracesExporterOptions.AttributeJsonOptions"/>.</param>
    public static string Serialize(IEnumerable<KeyValuePair<string, object?>>? tags, JsonSerializerOptions? objectOptions = null)
    {
        if (tags is null)
        {
            return "{}";
        }

        Dictionary<string, object?>? dict = null;
        foreach (var tag in tags)
        {
            dict ??= new Dictionary<string, object?>();
            dict[tag.Key] = Normalize(tag.Value, objectOptions);
        }

        return dict is null ? "{}" : JsonSerializer.Serialize(dict, Options);
    }

    /// <param name="events">The span's events.</param>
    /// <param name="enrichException">Called with the attributes of each <c>exception</c> event
    /// before it is written, to add to them.</param>
    public static string SerializeEvents(IEnumerable<ActivityEvent> events, Action<Dictionary<string, object?>>? enrichException = null)
    {
        var list = new List<object>();
        foreach (var e in events)
        {
            var attributes = ToDictionary(e.Tags);
            if (enrichException is not null && e.Name == "exception")
            {
                enrichException(attributes);
            }

            list.Add(new
            {
                name = e.Name,
                time = e.Timestamp.UtcDateTime,
                attributes,
            });
        }

        return JsonSerializer.Serialize(list, Options);
    }

    public static string SerializeLinks(IEnumerable<ActivityLink> links)
    {
        var list = new List<object>();
        foreach (var l in links)
        {
            list.Add(new
            {
                traceId = l.Context.TraceId.ToHexString(),
                spanId = l.Context.SpanId.ToHexString(),
                attributes = ToDictionary(l.Tags),
            });
        }

        return JsonSerializer.Serialize(list, Options);
    }

    /// <summary>Strings, primitives, arrays of them and parsed JSON pass through untouched; any
    /// other object becomes JSON with <paramref name="objectOptions"/>. A value that can't be
    /// serialized (a cycle, a stream) is stored as its <c>ToString()</c> rather than failing the
    /// whole batch.</summary>
    private static object? Normalize(object? value, JsonSerializerOptions? objectOptions)
    {
        switch (value)
        {
            case null or string or bool or char or Enum:
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
            case DateTime or DateTimeOffset or TimeSpan or Guid:
            case JsonElement or JsonNode or JsonDocument:
            case string[] or bool[] or int[] or long[] or double[]:
                return value;
        }

        try
        {
            return JsonSerializer.SerializeToElement(value, value.GetType(), objectOptions ?? DefaultObjectOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return value.ToString();
        }
    }

    private static Dictionary<string, object?> ToDictionary(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        var dict = new Dictionary<string, object?>();
        if (tags is null)
        {
            return dict;
        }

        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        return dict;
    }
}
