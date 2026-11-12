namespace PowerTraces.Dashboard.Query;

/// <summary>One page of a list query. Counting every match of a broad filter over a large table
/// costs as much as the query itself, so providers stop counting at <see cref="CountLimit"/>; when
/// they do, <see cref="TotalCountCapped"/> is set and <see cref="TotalCount"/> is a lower bound
/// ("10,000+").</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize, bool TotalCountCapped = false)
{
    public const int CountLimit = 10_000;
}

public record LogListItemDto(
    long Id,
    DateTime Timestamp,
    int SeverityNumber,
    string? SeverityText,
    string? Body,
    string? ServiceName,
    string? TraceId,
    string? SpanId,
    Dictionary<string, object?> Attributes);

public record LogDetailDto(
    long Id,
    DateTime Timestamp,
    DateTime? ObservedTimestamp,
    int SeverityNumber,
    string? SeverityText,
    string? Body,
    string? ServiceName,
    string? TraceId,
    string? SpanId,
    string? ScopeLibraryName,
    string? ScopeLibraryVersion,
    string? SchemaUrl,
    Dictionary<string, object?> Attributes);

/// <summary><see cref="Match"/> is set by searches with text or field filters: where in the trace
/// the search matched.</summary>
public record TraceListItemDto(
    string TraceId,
    string? RootName,
    string? ServiceName,
    DateTime StartTime,
    long DurationMs,
    bool HasError,
    int SpanCount,
    Dictionary<string, object?> Attributes,
    TraceMatchDto? Match = null);

public record SpanEventDto(string Name, DateTime Time, Dictionary<string, object?> Attributes);

public record SpanLinkDto(string TraceId, string SpanId, Dictionary<string, object?> Attributes);

public record SpanDto(
    string SpanId,
    string ParentSpanId,
    string Name,
    string? ServiceName,
    string? Kind,
    DateTime StartTime,
    DateTime? EndTime,
    long DurationMs,
    string? Status,
    string? StatusMessage,
    bool HasError,
    string? HttpStatus,
    Dictionary<string, object?> Attributes,
    IReadOnlyList<SpanEventDto> Events,
    IReadOnlyList<SpanLinkDto> Links);

/// <summary><see cref="IsPinned"/> is filled in by the dashboard endpoint (from
/// <see cref="IRetentionStore"/>), not by the store that loads the spans.</summary>
public record TraceDetailDto(
    string TraceId,
    string? RootName,
    string? ServiceName,
    DateTime StartTime,
    DateTime EndTime,
    long DurationMs,
    bool HasError,
    IReadOnlyList<SpanDto> Spans,
    bool IsPinned = false);

public record DashboardSummaryDto(
    int LogsLast24h,
    int TracesLast24h,
    int ErrorTracesLast24h,
    double ErrorRatePercent,
    IReadOnlyList<LogListItemDto> RecentLogs,
    IReadOnlyList<TraceListItemDto> RecentTraces);

public enum IgnoreRuleKind { Span, Trace }

/// <summary>HTTP-facing ignore rule. <see cref="Kind"/> is deliberately a lowercase string so
/// clients need not depend on an application's global JSON enum-converter configuration.</summary>
public record IgnoreRuleDto(Guid Id, string Kind, string Name, DateTime CreatedAt);

/// <summary>One time bucket of the dashboard overview: traces started in it (and how many errored,
/// and their mean duration) and log records written in it (and how many were warnings/errors).</summary>
public record TimeBucketDto(DateTime Start, int Traces, int ErrorTraces, double AvgDurationMs, int Logs, int WarningLogs, int ErrorLogs);

/// <summary>Request/error/duration figures for one root operation (root span name + service).</summary>
public record OperationStatsDto(string Name, string? ServiceName, int Count, int ErrorCount, double AvgDurationMs, long MaxDurationMs);

/// <summary>What the dashboard's charts plot for a time window: evenly sized buckets from
/// <see cref="From"/>, plus the busiest root operations in the window.</summary>
public record DashboardOverviewDto(
    DateTime From,
    DateTime To,
    int BucketSeconds,
    IReadOnlyList<TimeBucketDto> Buckets,
    IReadOnlyList<OperationStatsDto> TopOperations);
