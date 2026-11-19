using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PowerTraces.Dashboard.Query;

/// <summary>Optional capability for dashboard stores that keep small named values (every built-in
/// provider does, in its Settings table or collection). The dashboard keeps people's verdicts on
/// replies here.</summary>
public interface IDashboardSettingsStore
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct);

    Task SetSettingAsync(string key, string value, CancellationToken ct);
}

/// <summary>A step (span name in a service) that runs in most traces of the same operation but not
/// in this one. <see cref="Share"/> is the fraction of those traces that have it.</summary>
public sealed record MissingStepDto(string Name, string? ServiceName, double Share);

/// <summary>How one reply in a trace compares with the replies the same call gave in other
/// traces of the same operation that didn't fail.
///
/// Shapes are the set of JSON paths in the body with their value types (<c>data.id:string</c>,
/// <c>errors[]:string</c>, <c>data:null</c>), so nothing depends on what the fields are called.
/// <see cref="Usual"/> are paths in at least 80% of the other replies; <see cref="Missing"/> those
/// this reply lacks; <see cref="Added"/> this reply's paths seen in under 10% of them.
/// <see cref="ShapeKey"/> identifies the call and this shape, for <see cref="Verdict"/>s.</summary>
public sealed record ReplyComparisonDto(
    string SpanId,
    string ShapeKey,
    int SampleSize,
    double SameShapeShare,
    IReadOnlyList<string> Usual,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Added,
    long? UsualBytes,
    long? Bytes,
    string? Verdict);

/// <summary>A trace compared with recent traces of the same operation that didn't fail:
/// <see cref="SampleSize"/> of them.</summary>
public sealed record TraceBaselineDto(int SampleSize, IReadOnlyList<MissingStepDto> MissingSteps, IReadOnlyList<ReplyComparisonDto> Replies)
{
    public static readonly TraceBaselineDto Empty = new(0, [], []);
}

/// <summary>A person's call on replies of one shape from one call: <c>failure</c>, <c>fine</c>,
/// or null to forget it.</summary>
public sealed record ReplyVerdictRequest(string ShapeKey, string? Verdict);

/// <summary>A stored verdict.</summary>
public sealed record ReplyVerdictEntry(string Verdict, DateTime At);

/// <summary>Compares a trace with its peers — see <see cref="TraceBaselineDto"/>.</summary>
public static class TraceBaselines
{
    /// <summary>Fewer comparable replies or traces than this, and nothing is said about them.</summary>
    public const int MinSample = 5;

    /// <summary>How many recent traces of the same operation are compared.</summary>
    public const int PeerCount = 30;

    /// <summary>The settings key verdicts are stored under, as one JSON object.</summary>
    public const string VerdictsSettingKey = "reply-verdicts";

    /// <summary>The oldest verdicts are dropped beyond this many.</summary>
    public const int MaxVerdicts = 2000;

    public const string FailureVerdict = "failure";
    public const string FineVerdict = "fine";

    private const double UsualShare = 0.8;
    private const double RareShare = 0.1;
    private const int MaxListed = 40;
    private const int MaxDepth = 8;
    private const int MaxPaths = 300;

    public static TraceBaselineDto Compare(TraceDetailDto trace, IReadOnlyList<TraceDetailDto> peers, IReadOnlyDictionary<string, ReplyVerdictEntry> verdicts)
    {
        var others = peers.Where(p => !string.Equals(p.TraceId, trace.TraceId, StringComparison.OrdinalIgnoreCase)).ToList();
        return new TraceBaselineDto(others.Count, MissingSteps(trace, others), Replies(trace, others, verdicts));
    }

    private static List<MissingStepDto> MissingSteps(TraceDetailDto trace, List<TraceDetailDto> peers)
    {
        if (peers.Count < MinSample)
        {
            return [];
        }

        var own = new HashSet<string>(trace.Spans.Select(StepKey), StringComparer.Ordinal);
        var counts = new Dictionary<string, (int Count, SpanDto Example)>(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            foreach (var span in peer.Spans.GroupBy(StepKey, StringComparer.Ordinal).Select(g => g.First()))
            {
                var key = StepKey(span);
                counts[key] = counts.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.Example) : (1, span);
            }
        }

        return counts
            .Where(kv => !own.Contains(kv.Key) && (double)kv.Value.Count / peers.Count >= UsualShare)
            .Select(kv => new MissingStepDto(StepName(kv.Value.Example), kv.Value.Example.ServiceName, Math.Round((double)kv.Value.Count / peers.Count, 2)))
            .OrderByDescending(s => s.Share)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(10)
            .ToList();
    }

    private static List<ReplyComparisonDto> Replies(TraceDetailDto trace, List<TraceDetailDto> peers, IReadOnlyDictionary<string, ReplyVerdictEntry> verdicts)
    {
        var result = new List<ReplyComparisonDto>();
        var peerSpans = peers.SelectMany(p => p.Spans).ToList();

        foreach (var span in trace.Spans)
        {
            if (ResponseBody(span) is not { } body)
            {
                continue;
            }

            var callKey = StepKey(span);
            var others = peerSpans
                .Where(s => string.Equals(StepKey(s), callKey, StringComparison.Ordinal))
                .Select(ResponseBody)
                .OfType<string>()
                .ToList();
            var shape = ShapeOf(body);
            var shapeKey = $"{callKey}#{shape.Key}";
            verdicts.TryGetValue(shapeKey, out var verdict);

            if (others.Count < MinSample)
            {
                // Too few replies to compare with, but a verdict on this shape still counts.
                if (verdict is not null)
                {
                    result.Add(new ReplyComparisonDto(span.SpanId, shapeKey, others.Count, 0, [], [], [], null, ByteCount(body), verdict.Verdict));
                }

                continue;
            }

            var shapes = others.Select(ShapeOf).ToList();
            var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in shapes.SelectMany(s => s.Paths))
            {
                pathCounts[path] = pathCounts.TryGetValue(path, out var n) ? n + 1 : 1;
            }

            double ShareOf(string path) => pathCounts.TryGetValue(path, out var n) ? (double)n / shapes.Count : 0;
            var usual = pathCounts.Keys.Where(p => ShareOf(p) >= UsualShare).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var missing = usual.Where(p => !shape.Paths.Contains(p)).ToList();
            var added = shape.Paths.Where(p => ShareOf(p) < RareShare).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var same = (double)shapes.Count(s => s.Key == shape.Key) / shapes.Count;
            var sizes = others.Select(ByteCount).OrderBy(n => n).ToList();

            result.Add(new ReplyComparisonDto(
                span.SpanId,
                shapeKey,
                shapes.Count,
                Math.Round(same, 3),
                usual.Take(MaxListed).ToList(),
                missing.Take(MaxListed).ToList(),
                added.Take(MaxListed).ToList(),
                sizes[sizes.Count / 2],
                ByteCount(body),
                verdict?.Verdict));
        }

        return result;
    }

    /// <summary>A step of an operation: the service, the span name, and what it called or
    /// served — span names like "POST" alone don't tell calls apart.</summary>
    public static string StepKey(SpanDto span) => $"{span.ServiceName}|{span.Name}|{Target(span)}";

    private static string? Target(SpanDto span) =>
        Text(span, "http.route") ?? Text(span, "server.address") ?? Text(span, "db.system.name") ?? Text(span, "db.system") ?? Text(span, "rpc.service");

    private static string StepName(SpanDto span) =>
        Target(span) is { } target && span.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0 ? $"{span.Name} {target}" : span.Name;

    /// <summary>A response body recorded as an attribute — any key that ends with "response body"
    /// once separators are ignored (<c>http.response.body</c>, <c>response_body</c>, …).</summary>
    public static string? ResponseBody(SpanDto span)
    {
        foreach (var kv in span.Attributes)
        {
            var letters = new string(kv.Key.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
            if (letters.EndsWith("responsebody", StringComparison.Ordinal) && ValueText(kv.Value) is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? Text(SpanDto span, string key) =>
        span.Attributes.TryGetValue(key, out var value) ? ValueText(value) is { Length: > 0 } text ? text : null : null;

    private static string? ValueText(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement e => e.GetRawText(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static long ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    internal sealed record Shape(string Key, HashSet<string> Paths);

    /// <summary>The body's JSON paths with their value types; a body that isn't JSON has the
    /// single shape "text".</summary>
    internal static Shape ShapeOf(string body)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            Walk(doc.RootElement, "", 0, paths);
        }
        catch (JsonException)
        {
            paths.Add(":text");
        }

        return new Shape(Hash(string.Join("\n", paths.OrderBy(p => p, StringComparer.Ordinal))), paths);
    }

    private static void Walk(JsonElement element, string path, int depth, HashSet<string> paths)
    {
        if (paths.Count >= MaxPaths)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object when depth < MaxDepth:
                var any = false;
                foreach (var property in element.EnumerateObject())
                {
                    any = true;
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, paths);
                }

                if (!any)
                {
                    paths.Add($"{path}:empty object");
                }

                break;
            case JsonValueKind.Array when depth < MaxDepth:
                var count = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (count++ >= 3)
                    {
                        break;
                    }

                    Walk(item, $"{path}[]", depth + 1, paths);
                }

                if (count == 0)
                {
                    paths.Add($"{path}:empty list");
                }

                break;
            default:
                paths.Add($"{path}:{TypeName(element.ValueKind)}");
                break;
        }
    }

    private static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "list",
        _ => "value",
    };

    /// <summary>FNV-1a, 64-bit: short, stable across processes and platforms.</summary>
    private static string Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the stored verdicts; an unreadable value counts as none.</summary>
    public static Dictionary<string, ReplyVerdictEntry> ParseVerdicts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ReplyVerdictEntry>>(json!, VerdictJson);
            return parsed is null ? new(StringComparer.Ordinal) : new(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Applies one verdict (or removes it) and serializes the set, oldest dropped past
    /// <see cref="MaxVerdicts"/>.</summary>
    public static string WithVerdict(Dictionary<string, ReplyVerdictEntry> verdicts, string shapeKey, string? verdict, DateTime now)
    {
        if (verdict is null)
        {
            verdicts.Remove(shapeKey);
        }
        else
        {
            verdicts[shapeKey] = new ReplyVerdictEntry(verdict, now);
        }

        var kept = verdicts.Count <= MaxVerdicts
            ? verdicts
            : verdicts.OrderByDescending(kv => kv.Value.At).Take(MaxVerdicts).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(kept, VerdictJson);
    }

    private static readonly JsonSerializerOptions VerdictJson = new(JsonSerializerDefaults.Web);
}
