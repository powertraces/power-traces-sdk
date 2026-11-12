namespace PowerTraces.Dashboard.Query;

/// <summary>The contract every database backend implements to serve the dashboard's UI/API.
/// SQL Server, PostgreSQL, MongoDB, and MySQL ship built in — each provider package registers its
/// implementation under an <see cref="PowerTracesDatabaseProvider"/> DI key via its own
/// <c>AddPowerTracesXxxDashboard(...)</c>. Implement this yourself and register it under your own key
/// to plug in any other database (matching the exporter side's <see cref="Storage.IStorageWriter"/>
/// extension point).</summary>
public interface IDashboardStore
{
    /// <summary>Creates whatever tables/collections/indexes this provider needs, if they don't
    /// already exist. Called automatically (once, lazily) before the first query.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    Task<PagedResult<LogListItemDto>> ListLogsAsync(
        DateTime? from, DateTime? to, string? service, int? severity, string? search,
        IReadOnlyList<AttributeFilter>? attributeFilters,
        int page, int pageSize, CancellationToken ct);

    Task<LogDetailDto?> GetLogAsync(long id, CancellationToken ct);

    /// <summary>Searches logs — see <see cref="LogSearchQuery"/>. Every built-in provider
    /// implements it; the default (for custom stores written before it existed) falls back to
    /// <see cref="ListLogsAsync"/> with what it can express.</summary>
    Task<PagedResult<LogListItemDto>> SearchLogsAsync(LogSearchQuery query, CancellationToken ct) =>
        ListLogsAsync(
            query.From, query.To, query.Services?.FirstOrDefault(), query.Severities?.FirstOrDefault() is { } s and > 0 ? s : null,
            query.Text is { Count: > 0 } text ? string.Join(" ", text) : null,
            query.Filters?.Where(f => f.Match != FieldMatch.NotEquals).Select(f => new AttributeFilter(f.Key, f.Value, f.Match == FieldMatch.HasWord)).ToList(),
            query.Page, query.PageSize, ct);

    /// <summary>How a log search's matches split by severity level and service, for the filter
    /// panel. Each facet is counted without its own part of <paramref name="query"/>, so it shows
    /// its alternatives. Empty by default.</summary>
    Task<LogFacetsDto> GetLogFacetsAsync(LogSearchQuery query, CancellationToken ct) =>
        Task.FromResult(new LogFacetsDto([], [], false));

    Task<PagedResult<TraceListItemDto>> ListTracesAsync(
        DateTime? from, DateTime? to, string? service, bool? hasError, string? search,
        IReadOnlyList<AttributeFilter>? attributeFilters,
        int page, int pageSize, CancellationToken ct);

    Task<TraceDetailDto?> GetTraceAsync(string traceId, CancellationToken ct);

    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ListServiceNamesAsync(CancellationToken ct);

    /// <summary>Distinct attribute keys seen recently in the requested scope's stored rows — a
    /// best-effort sample (not an exhaustive scan of the whole table), cached by the
    /// implementation. Powers the dashboard's attribute-based search and column picker.</summary>
    Task<IReadOnlyList<string>> ListAttributeKeysAsync(AttributeScope scope, CancellationToken ct);

    /// <summary>Per-bucket trace/error/log counts and the busiest root operations between
    /// <paramref name="from"/> and <paramref name="to"/>, for the dashboard's charts. Every
    /// built-in provider implements it; the default (for custom stores written before it existed)
    /// returns empty buckets.</summary>
    /// <summary>Searches traces through the search index — see <see cref="TraceSearchQuery"/>.
    /// Every built-in provider implements it; the default (for custom stores written before it
    /// existed) falls back to <see cref="ListTracesAsync"/> with what it can express.</summary>
    Task<PagedResult<TraceListItemDto>> SearchTracesAsync(TraceSearchQuery query, CancellationToken ct) =>
        ListTracesAsync(
            query.From, query.To, query.Services?.FirstOrDefault(), query.HasError, query.Text,
            query.Filters?.Where(f => f.Match != FieldMatch.NotEquals).Select(f => new AttributeFilter(f.Key, f.Value, f.Match == FieldMatch.HasWord)).ToList(),
            query.Page, query.PageSize, ct);

    /// <summary>How a search's matches split by endpoint and status, for the filter panel. The
    /// endpoint and status parts of <paramref name="query"/> are ignored, so each facet shows its
    /// alternatives. Empty by default.</summary>
    Task<TraceFacetsDto> GetTraceFacetsAsync(TraceSearchQuery query, CancellationToken ct) =>
        Task.FromResult(new TraceFacetsDto([], 0, 0, false));

    /// <summary>The most common values of a trace field (an attribute key or a path inside a JSON
    /// attribute) since <paramref name="since"/>, optionally starting with
    /// <paramref name="prefix"/> — lowercase, as the search index stores them. Empty by default.</summary>
    Task<IReadOnlyList<FacetValueDto>> ListFieldValuesAsync(string key, string? prefix, DateTime since, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FacetValueDto>>([]);

    Task<DashboardOverviewDto> GetOverviewAsync(DateTime from, DateTime to, int bucketCount, CancellationToken ct) =>
        Task.FromResult(DashboardOverview.Assemble(from, to, DashboardOverview.BucketSeconds(from, to, bucketCount), [], [], []));
}

/// <summary>Optional capability for dashboard stores that can persist exporter ignore rules (every
/// built-in provider implements it). Rules affect only telemetry exported after they are created;
/// existing telemetry is never deleted.</summary>
public interface IIgnoreRuleStore : Storage.IIgnoreRuleSource
{
    Task AddIgnoreRuleAsync(IgnoreRuleKind kind, string name, CancellationToken ct);
    Task DeleteIgnoreRuleAsync(Guid id, CancellationToken ct);
}
