namespace PowerTraces.Dashboard.Query;

/// <summary>Assembles a <see cref="TraceDetailDto"/> from a trace's spans — shared by every
/// provider so they agree on which span represents the trace (the real root, else the earliest
/// span) and how its bounds are computed.</summary>
public static class TraceDetails
{
    public static TraceDetailDto? Build(string traceId, IReadOnlyList<SpanDto> spans)
    {
        if (spans.Count == 0)
        {
            return null;
        }

        var ordered = spans
            .OrderBy(s => s.StartTime)
            .ThenBy(s => s.SpanId, StringComparer.Ordinal)
            .ToList();

        var root = ordered.FirstOrDefault(s => s.ParentSpanId.Length == 0) ?? ordered[0];
        var start = ordered[0].StartTime;
        var end = ordered.Max(s => s.EndTime ?? s.StartTime);

        return new TraceDetailDto(
            traceId,
            root.Name,
            root.ServiceName,
            start,
            end,
            (long)(end - start).TotalMilliseconds,
            ordered.Any(s => s.HasError),
            ordered);
    }
}
