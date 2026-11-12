namespace PowerTraces.Dashboard.Query;

/// <summary>A log search — the log counterpart of <see cref="TraceSearchQuery"/>. Every part is
/// optional and they combine with AND.
///
/// Each entry of <see cref="Text"/> is one condition on the message: it must contain the text
/// (case-insensitive); <c>-text</c> means it must not; <c>"quoted text"</c> is taken literally; and
/// a 32-character trace id matches that trace's logs too.</summary>
public sealed record LogSearchQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public IReadOnlyList<string>? Text { get; init; }

    /// <summary>Services — any of them.</summary>
    public IReadOnlyList<string>? Services { get; init; }

    /// <summary>Severity levels — any of them, each named by its lowest number (see
    /// <see cref="LogSeverity"/>): 1 Trace, 5 Debug, 9 Info, 13 Warn, 17 Error, 21 Fatal, 0
    /// unspecified. Any number inside a level selects that level.</summary>
    public IReadOnlyList<int>? Severities { get; init; }

    /// <summary>Conditions on the log's attributes: <see cref="FieldMatch.Equals"/> (the whole
    /// value), <see cref="FieldMatch.NotEquals"/> (absent, or another value) or
    /// <see cref="FieldMatch.HasWord"/> (contains the text).</summary>
    public IReadOnlyList<FieldFilter>? Filters { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    /// <summary>The query <see cref="IDashboardStore.ListLogsAsync"/>'s parameters describe:
    /// <paramref name="search"/> as one text condition, <paramref name="severity"/> as its level,
    /// each attribute filter as an exact (or, with <c>Contains</c>, substring) field filter.</summary>
    public static LogSearchQuery FromLegacy(
        DateTime? from, DateTime? to, string? service, int? severity, string? search,
        IReadOnlyList<AttributeFilter>? attributeFilters, int page, int pageSize) => new()
    {
        From = from,
        To = to,
        Services = string.IsNullOrWhiteSpace(service) ? null : [service],
        Severities = severity is { } s ? [s] : null,
        Text = string.IsNullOrWhiteSpace(search) ? null : [search],
        Filters = attributeFilters?.Select(f => new FieldFilter(f.Key, f.Value, f.Contains ? FieldMatch.HasWord : FieldMatch.Equals)).ToList(),
        Page = page,
        PageSize = pageSize,
    };
}

/// <summary>How many of a search's matches are at one severity level.</summary>
public sealed record SeverityCountDto(int Severity, int Count);

/// <summary>Breakdown of a log search's matches, for the filter side panel — each facet counted
/// with its own selection left out, over the newest <see cref="LogFacets.SampleSize"/> matches;
/// <see cref="Sampled"/> says when there were more.</summary>
public sealed record LogFacetsDto(IReadOnlyList<SeverityCountDto> Severities, IReadOnlyList<FacetValueDto> Services, bool Sampled);

public static class LogFacets
{
    public const int SampleSize = 20_000;
    public const int MaxServices = 50;
}

/// <summary>OpenTelemetry severity numbers grouped into levels of four (1–4 Trace … 21–24 Fatal).</summary>
public static class LogSeverity
{
    /// <summary>The lowest number of the level <paramref name="severityNumber"/> is in (0 for unspecified).</summary>
    public static int Level(int severityNumber) =>
        severityNumber <= 0 ? 0 : Math.Min(21, (severityNumber - 1) / 4 * 4 + 1);

    /// <summary>The numbers a level covers.</summary>
    public static (int Low, int High) Range(int level)
    {
        var start = Level(level);
        return start == 0 ? (0, 0) : (start, start + 3);
    }

    /// <summary>The query's distinct levels, or null when it has none.</summary>
    public static IReadOnlyList<int>? Levels(IReadOnlyList<int>? severities) =>
        severities is { Count: > 0 } ? severities.Select(Level).Distinct().Order().ToList() : null;
}

/// <summary>One parsed <see cref="LogSearchQuery.Text"/> entry.</summary>
public readonly record struct LogTextTerm(string Text, bool Exclude, bool IsTraceId)
{
    public static IEnumerable<LogTextTerm> Parse(IReadOnlyList<string>? text)
    {
        foreach (var raw in text ?? [])
        {
            var piece = raw?.Trim();
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            var exclude = false;
            if (piece.Length > 1 && piece[0] == '-')
            {
                exclude = true;
                piece = piece[1..].Trim();
            }

            if (piece.Length >= 2 && piece[0] == '"' && piece[^1] == '"')
            {
                piece = piece[1..^1];
            }

            if (piece.Length == 0)
            {
                continue;
            }

            yield return new LogTextTerm(piece, exclude, !exclude && piece.Length == 32 && piece.All(char.IsAsciiHexDigit));
        }
    }
}
