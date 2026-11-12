using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PowerTraces.Dashboard.Query;

internal static class QueryEndpoints
{
    private static readonly JsonSerializerOptions AttributeFiltersJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void MapQueryEndpoints(this IEndpointRouteBuilder api, IDashboardStore store)
    {
        api.MapGet("/dashboard", async (CancellationToken ct) =>
            Results.Ok(await store.GetSummaryAsync(ct)));

        // The dashboard's charts: `range` is how far back from now (e.g. "1h", "24h", "7d"),
        // `buckets` how many points to split it into.
        api.MapGet("/overview", async (string? range, CancellationToken ct, int buckets = 96) =>
        {
            var span = ParseRange(range) ?? TimeSpan.FromHours(24);
            var to = DateTime.UtcNow;
            return Results.Ok(await store.GetOverviewAsync(to - span, to, Math.Clamp(buckets, 1, 500), ct));
        });

        api.MapGet("/logs", async (
                DateTime? from, DateTime? to, string? service, int? severity, string? search, string? attributes,
                CancellationToken ct, int page = 1, int pageSize = 100) =>
            Results.Ok(await store.ListLogsAsync(from, to, service, severity, search, ParseAttributeFilters(attributes), page, pageSize, ct)));

        // The log search (see LogSearchQuery): q (repeatable, one message condition each),
        // from/to or range, service and severity (repeatable), filters (a JSON array of
        // {key, value, match}), page/pageSize.
        api.MapGet("/logs/search", async (HttpRequest request, CancellationToken ct) =>
            Results.Ok(await store.SearchLogsAsync(ParseLogSearch(request.Query), ct)));

        // Severity and service counts for the same query, for the filter panel.
        api.MapGet("/logs/facets", async (HttpRequest request, CancellationToken ct) =>
            Results.Ok(await store.GetLogFacetsAsync(ParseLogSearch(request.Query), ct)));

        api.MapGet("/logs/{id:long}", async (long id, CancellationToken ct) =>
        {
            var log = await store.GetLogAsync(id, ct);
            return log is null ? Results.NotFound() : Results.Ok(log);
        });

        api.MapGet("/traces", async (
                DateTime? from, DateTime? to, string? service, bool? hasError, string? search, string? attributes,
                CancellationToken ct, int page = 1, int pageSize = 50) =>
            Results.Ok(await store.ListTracesAsync(from, to, service, hasError, search, ParseAttributeFilters(attributes), page, pageSize, ct)));

        // The trace search (see TraceSearchQuery): q, from/to or range, endpoint and service
        // (repeatable), errors (true/false), minMs/maxMs, filters (a JSON array of
        // {key, value, match}), page/pageSize.
        api.MapGet("/traces/search", async (HttpRequest request, CancellationToken ct) =>
            Results.Ok(await store.SearchTracesAsync(ParseTraceSearch(request.Query), ct)));

        // Endpoint and status counts for the same query, for the filter panel.
        api.MapGet("/traces/facets", async (HttpRequest request, CancellationToken ct) =>
            Results.Ok(await store.GetTraceFacetsAsync(ParseTraceSearch(request.Query), ct)));

        // The most common values of a trace field (default: over the last 24 hours).
        api.MapGet("/attributes/traces/values", async (string key, string? prefix, string? range, CancellationToken ct) =>
            Results.Ok(await store.ListFieldValuesAsync(key, prefix, DateTime.UtcNow - (ParseRange(range) ?? TimeSpan.FromHours(24)), ct)));

        api.MapGet("/traces/{traceId}", async (string traceId, CancellationToken ct) =>
        {
            var trace = await store.GetTraceAsync(traceId, ct);
            if (trace is null)
                return Results.NotFound();
            if (store is IRetentionStore retentionStore)
                trace = trace with { IsPinned = await retentionStore.IsTracePinnedAsync(trace.TraceId, ct) };
            return Results.Ok(trace);
        });

        // Pinning keeps a trace (and its logs) out of retention for good.
        api.MapPut("/traces/{traceId}/pin", async (string traceId, CancellationToken ct) =>
        {
            if (store is not IRetentionStore retentionStore)
                return RetentionNotSupported();
            var trace = await store.GetTraceAsync(traceId, ct);
            if (trace is null)
                return Results.NotFound();

            await retentionStore.PinTraceAsync(
                new PinnedTraceDto(NormalizeTraceId(trace.TraceId), trace.RootName, trace.ServiceName, trace.StartTime, trace.HasError, DateTime.UtcNow), ct);
            return Results.NoContent();
        });

        api.MapDelete("/traces/{traceId}/pin", async (string traceId, CancellationToken ct) =>
        {
            if (store is not IRetentionStore retentionStore)
                return RetentionNotSupported();
            await retentionStore.UnpinTraceAsync(NormalizeTraceId(traceId), ct);
            return Results.NoContent();
        });

        api.MapGet("/resources", async (CancellationToken ct) =>
            Results.Ok(await store.ListServiceNamesAsync(ct)));

        api.MapGet("/ignore-rules", async (CancellationToken ct) =>
            store is IIgnoreRuleStore ignoreStore
                ? Results.Ok(await ignoreStore.ListIgnoreRulesAsync(ct))
                : Results.Problem("This dashboard provider does not support persisted ignore rules.", statusCode: StatusCodes.Status501NotImplemented));

        api.MapPost("/ignore-rules", async (CreateIgnoreRuleRequest request, CancellationToken ct) =>
        {
            if (store is not IIgnoreRuleStore ignoreStore)
                return Results.Problem("This dashboard provider does not support persisted ignore rules.", statusCode: StatusCodes.Status501NotImplemented);
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("A span or trace name is required.");
            if (!Enum.TryParse<IgnoreRuleKind>(request.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest("Kind must be either 'span' or 'trace'.");

            await ignoreStore.AddIgnoreRuleAsync(kind, request.Name.Trim(), ct);
            return Results.NoContent();
        });

        api.MapDelete("/ignore-rules/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            if (store is not IIgnoreRuleStore ignoreStore)
                return Results.Problem("This dashboard provider does not support persisted ignore rules.", statusCode: StatusCodes.Status501NotImplemented);
            await ignoreStore.DeleteIgnoreRuleAsync(id, ct);
            return Results.NoContent();
        });

        api.MapGet("/retention", async (CancellationToken ct) =>
            store is IRetentionStore retentionStore
                ? Results.Ok(await retentionStore.GetRetentionAsync(ct))
                : RetentionNotSupported());

        api.MapPut("/retention", async (RetentionSettingsDto settings, CancellationToken ct) =>
        {
            if (store is not IRetentionStore retentionStore)
                return RetentionNotSupported();
            if (settings.Validate() is { } problem)
                return Results.BadRequest(problem);

            await retentionStore.SaveRetentionSettingsAsync(settings, ct);
            return Results.Ok(await retentionStore.GetRetentionAsync(ct));
        });

        // Runs a sweep now rather than waiting for the next scheduled one.
        api.MapPost("/retention/run", async (CancellationToken ct) =>
        {
            if (store is not IRetentionStore retentionStore)
                return RetentionNotSupported();
            var run = await retentionStore.ApplyRetentionAsync(ct);
            return run is null
                ? Results.Problem("Retention is disabled — enable it before running a cleanup.", statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(run);
        });

        api.MapGet("/retention/pinned", async (CancellationToken ct) =>
            store is IRetentionStore retentionStore
                ? Results.Ok(await retentionStore.ListPinnedTracesAsync(ct))
                : RetentionNotSupported());

        // Bound as a raw string and parsed case-insensitively rather than as `AttributeScope`
        // directly — ASP.NET Core's built-in enum route-value binder is case-sensitive, and the
        // frontend (and any other caller) naturally sends lowercase ("traces", "logs").
        api.MapGet("/attributes/{scope}", async (string scope, CancellationToken ct) =>
            Enum.TryParse<AttributeScope>(scope, ignoreCase: true, out var parsed)
                ? Results.Ok(await store.ListAttributeKeysAsync(parsed, ct))
                : Results.BadRequest($"Unknown attribute scope '{scope}'. Expected 'traces' or 'logs'."));
    }

    private static TraceSearchQuery ParseTraceSearch(IQueryCollection query)
    {
        var common = SearchQueryParts.Parse(query);
        long? Long(string name) => long.TryParse(query[name], CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

        return new TraceSearchQuery
        {
            From = common.From,
            To = common.To,
            Text = query["q"].ToString(),
            Endpoints = SearchQueryParts.Many(query, "endpoint"),
            Services = SearchQueryParts.Many(query, "service"),
            HasError = bool.TryParse(query["errors"], out var errors) ? errors : null,
            MinDurationMs = Long("minMs"),
            MaxDurationMs = Long("maxMs"),
            Filters = common.Filters,
            Page = common.Page,
            PageSize = common.PageSize,
        };
    }

    private static LogSearchQuery ParseLogSearch(IQueryCollection query)
    {
        var common = SearchQueryParts.Parse(query);
        var severities = query["severity"]
            .Select(v => int.TryParse(v, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : (int?)null)
            .OfType<int>()
            .ToList();

        return new LogSearchQuery
        {
            From = common.From,
            To = common.To,
            Text = SearchQueryParts.Many(query, "q"),
            Services = SearchQueryParts.Many(query, "service"),
            Severities = severities.Count > 0 ? severities : null,
            Filters = common.Filters,
            Page = common.Page,
            PageSize = common.PageSize,
        };
    }

    /// <summary>The parameters the trace and log searches share: from/to or range, filters,
    /// page/pageSize.</summary>
    private readonly record struct SearchQueryParts(DateTime? From, DateTime? To, List<FieldFilter>? Filters, int Page, int PageSize)
    {
        public static List<string>? Many(IQueryCollection query, string name) =>
            query[name].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList() is { Count: > 0 } list ? list : null;

        public static SearchQueryParts Parse(IQueryCollection query)
        {
            DateTime? Time(string name) =>
                DateTime.TryParse(query[name], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value) ? value : null;
            int Int(string name, int fallback) => int.TryParse(query[name], CultureInfo.InvariantCulture, out var value) ? value : fallback;

            var from = Time("from");
            if (from is null && ParseRange(query["range"]) is { } range)
            {
                from = DateTime.UtcNow - range;
            }

            List<FieldFilter>? filters = null;
            if (query["filters"].ToString() is { Length: > 0 } json)
            {
                try
                {
                    filters = JsonSerializer.Deserialize<List<FieldFilter>>(json, AttributeFiltersJsonOptions);
                }
                catch (JsonException)
                {
                    // Malformed filters are no filters, as for the attribute filters below.
                }
            }

            return new SearchQueryParts(from, Time("to"), filters, Int("page", 1), Int("pageSize", 50));
        }
    }

    private static IResult RetentionNotSupported() =>
        Results.Problem("This dashboard provider does not support retention.", statusCode: StatusCodes.Status501NotImplemented);

    private static string NormalizeTraceId(string traceId) => traceId.Trim().ToLowerInvariant();

    /// <summary>"15m", "6h", "7d" -> a TimeSpan; null for anything else.</summary>
    private static TimeSpan? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Length < 2 || !int.TryParse(range[..^1], out var n) || n <= 0)
        {
            return null;
        }

        return char.ToLowerInvariant(range[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(Math.Min(n, 90)),
            _ => null,
        };
    }

    /// <summary>The <c>attributes</c> query parameter is one JSON-encoded array of
    /// <see cref="AttributeFilter"/> (e.g. <c>[{"key":"http.method","value":"GET","contains":false}]</c>)
    /// rather than several parallel array-typed query parameters — minimal APIs bind those from
    /// repeated <c>key=a&amp;key=b</c> query strings, which is easy to get wrong keeping several
    /// arrays in sync client-side; one opaque string avoids that entirely. Malformed input is
    /// treated as "no filters" rather than a 400 — a filter list is an additive refinement, not a
    /// required part of the request shape.</summary>
    private static List<AttributeFilter>? ParseAttributeFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<AttributeFilter>>(json, AttributeFiltersJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Keep Kind as a string at the HTTP boundary: the SPA sends "span"/"trace", while
    // System.Text.Json's default enum converter only accepts numbers unless the host globally
    // opts into JsonStringEnumConverter.
    private sealed record CreateIgnoreRuleRequest(string Kind, string Name);
}
