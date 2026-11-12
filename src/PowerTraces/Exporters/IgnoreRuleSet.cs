using PowerTraces.Dashboard.Query;

namespace PowerTraces.Exporters;

/// <summary>The ignore rules in force for one export batch, as name lookups.</summary>
internal sealed class IgnoreRuleSet
{
    public static readonly IgnoreRuleSet Empty = new([], []);

    private IgnoreRuleSet(HashSet<string> spanNames, HashSet<string> traceRootNames)
    {
        SpanNames = spanNames;
        TraceRootNames = traceRootNames;
    }

    /// <summary>Spans with one of these names are dropped on their own.</summary>
    public HashSet<string> SpanNames { get; }

    /// <summary>Traces whose root span has one of these names are dropped entirely.</summary>
    public HashSet<string> TraceRootNames { get; }

    public bool IsEmpty => SpanNames.Count == 0 && TraceRootNames.Count == 0;

    public static IgnoreRuleSet From(IReadOnlyList<IgnoreRuleDto> rules)
    {
        if (rules.Count == 0)
        {
            return Empty;
        }

        var spans = new HashSet<string>(StringComparer.Ordinal);
        var traces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            (string.Equals(rule.Kind, "span", StringComparison.OrdinalIgnoreCase) ? spans : traces).Add(rule.Name);
        }

        return new IgnoreRuleSet(spans, traces);
    }
}

/// <summary>A fixed-capacity set of recent trace ids — oldest entries fall out first. Used to
/// remember which traces were ignored (so their late-ending spans are dropped too) and which
/// traces already have spans on disk (so they can be purged once their root turns out to be
/// ignored), without growing for the lifetime of the process.</summary>
internal sealed class RecentTraceIds(int capacity)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _lock = new();

    public bool Contains(string traceId)
    {
        lock (_lock)
        {
            return _set.Contains(traceId);
        }
    }

    public void Add(string traceId)
    {
        lock (_lock)
        {
            if (!_set.Add(traceId))
            {
                return;
            }

            _order.Enqueue(traceId);
            while (_order.Count > capacity)
            {
                _set.Remove(_order.Dequeue());
            }
        }
    }
}
