namespace PowerTraces.Storage;

/// <summary>One batch's contribution to a trace's summary row (the <see cref="PowerTracesSchema.TracesTable"/>
/// table/collection). Providers merge it into whatever is already stored for the trace: start is
/// the minimum, end the maximum, the error flag is OR-ed, span counts add up, and the
/// root fields are replaced when this batch carries the real root span (or, while no root has been
/// seen, when this batch's earliest span starts before anything stored so far).</summary>
public sealed record TraceSummary(
    string TraceId,
    DateTime StartTime,
    DateTime EndTime,
    bool HasError,
    int SpanCount,
    bool HasRoot,
    string RootName,
    string? ServiceName,
    string RootAttributesJson)
{
    /// <summary>Groups a batch of spans by trace, one summary per trace, ordered by trace id so
    /// concurrent writers upsert rows in the same order (fewer lock-order deadlocks).</summary>
    public static IReadOnlyList<TraceSummary> FromBatch(IReadOnlyList<SpanRow> rows, ResourceInfo resource)
    {
        var byTrace = new Dictionary<string, List<SpanRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byTrace.TryGetValue(row.TraceId, out var list))
            {
                byTrace[row.TraceId] = list = [];
            }

            list.Add(row);
        }

        var result = new List<TraceSummary>(byTrace.Count);
        foreach (var (traceId, spans) in byTrace)
        {
            SpanRow? root = null;
            var earliest = spans[0];
            var start = spans[0].StartTime;
            var end = spans[0].EndTime ?? spans[0].StartTime;
            var hasError = false;

            foreach (var span in spans)
            {
                if (span.ParentSpanId.Length == 0)
                {
                    root ??= span;
                }

                if (span.StartTime < earliest.StartTime ||
                    (span.StartTime == earliest.StartTime && string.CompareOrdinal(span.SpanId, earliest.SpanId) < 0))
                {
                    earliest = span;
                }

                if (span.StartTime < start)
                {
                    start = span.StartTime;
                }

                var spanEnd = span.EndTime ?? span.StartTime;
                if (spanEnd > end)
                {
                    end = spanEnd;
                }

                hasError |= span.HasError;
            }

            var representative = root ?? earliest;
            result.Add(new TraceSummary(
                traceId, start, end, hasError, spans.Count, root is not null,
                representative.Name, resource.ServiceName, representative.AttributesJson));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.TraceId, b.TraceId));
        return result;
    }
}
