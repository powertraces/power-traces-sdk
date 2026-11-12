using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Reads the dashboard's data straight from MongoDB via the official driver. The trace
/// list queries the Traces collection (one document per trace, maintained on write), where every
/// filter — time, error flag, endpoint, service, search terms — is a condition on that one document
/// and served by its indexes; the log list filters log documents directly, attributes included via
/// their indexed (key, value) array. Index creation is delegated to the exporter's own
/// <see cref="MongoSchemaInitializer"/>, and the BSON document shapes are the exporter's too, so
/// both sides always agree on field names.</summary>
public sealed partial class MongoDashboardStore(PowerTracesDashboardOptions options) : IDashboardStore, IIgnoreRuleStore
{
    private const int SummaryListSize = 20;

    private readonly AttributeKeyCache _traceAttributeKeyCache = new(TimeSpan.FromSeconds(30));
    private readonly AttributeKeyCache _logAttributeKeyCache = new(TimeSpan.FromSeconds(30));

    private IMongoDatabase Database => MongoConnection.GetDatabase(options.ConnectionString);

    private IMongoCollection<ResourceDocument> Resources => Database.GetCollection<ResourceDocument>(MongoNames.Resources(options.Schema));

    private IMongoCollection<SpanDocument> Spans => Database.GetCollection<SpanDocument>(MongoNames.Spans(options.Schema));

    private IMongoCollection<LogDocument> Logs => Database.GetCollection<LogDocument>(MongoNames.Logs(options.Schema));

    private IMongoCollection<TraceDocument> Traces => Database.GetCollection<TraceDocument>(MongoNames.Traces(options.Schema));

    // MongoSchemaInitializer guards its own idempotency (shared with MongoStorageWriter for the
    // same database) — nothing to duplicate here.
    public Task EnsureSchemaAsync(CancellationToken ct) =>
        MongoSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    // ---------------------------------------------------------------- traces

    /// <summary>The original trace list query, kept for callers of the interface: it runs as a
    /// <see cref="SearchTracesAsync"/> — <paramref name="search"/> as search text, each attribute
    /// filter as an exact (or, with <c>Contains</c>, word) field filter.</summary>
    public Task<PagedResult<TraceListItemDto>> ListTracesAsync(
        DateTime? from, DateTime? to, string? service, bool? hasError, string? search,
        IReadOnlyList<AttributeFilter>? attributeFilters, int page, int pageSize, CancellationToken ct) =>
        SearchTracesAsync(new TraceSearchQuery
        {
            From = from,
            To = to,
            Services = string.IsNullOrWhiteSpace(service) ? null : [service],
            HasError = hasError,
            Text = search,
            Filters = attributeFilters?.Select(f => new FieldFilter(f.Key, f.Value, f.Contains ? FieldMatch.HasWord : FieldMatch.Equals)).ToList(),
            Page = page,
            PageSize = pageSize,
        }, ct);

    public async Task<TraceDetailDto?> GetTraceAsync(string traceId, CancellationToken ct)
    {
        traceId = traceId.Trim().ToLowerInvariant();
        var docs = await Spans.Find(Builders<SpanDocument>.Filter.Eq(s => s.TraceId, traceId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var serviceNames = await GetServiceNamesByResourceIdAsync(docs.Select(d => d.ResourceId), ct).ConfigureAwait(false);

        var spans = docs.Select(s => new SpanDto(
            s.SpanId,
            s.ParentSpanId,
            s.Name,
            s.ResourceId is { } rid && serviceNames.TryGetValue(rid, out var name) ? name : null,
            s.Kind,
            Utc(s.StartTime),
            s.EndTime is { } end ? Utc(end) : null,
            (long)((s.EndTime ?? s.StartTime) - s.StartTime).TotalMilliseconds,
            s.Status,
            s.StatusMessage,
            s.HasError,
            s.HttpStatus,
            JsonAttributes.Parse(s.AttributesJson),
            JsonAttributes.ParseList<SpanEventDto>(s.EventsJson),
            JsonAttributes.ParseList<SpanLinkDto>(s.LinksJson))).ToList();

        return TraceDetails.Build(traceId, spans);
    }

    // ---------------------------------------------------------------- logs

    public Task<PagedResult<LogListItemDto>> ListLogsAsync(
        DateTime? from, DateTime? to, string? service, int? severity, string? search,
        IReadOnlyList<AttributeFilter>? attributeFilters, int page, int pageSize, CancellationToken ct) =>
        SearchLogsCoreAsync(LogSearchQuery.FromLegacy(from, to, service, severity, search, attributeFilters, page, pageSize), countTotal: true, ct);

    public Task<PagedResult<LogListItemDto>> SearchLogsAsync(LogSearchQuery query, CancellationToken ct) =>
        SearchLogsCoreAsync(query, countTotal: true, ct);

    private async Task<PagedResult<LogListItemDto>> SearchLogsCoreAsync(LogSearchQuery query, bool countTotal, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var filter = await BuildLogFilterAsync(query, withSeverities: true, withServices: true, ct).ConfigureAwait(false);
        if (filter is null)
        {
            return new PagedResult<LogListItemDto>([], 0, page, pageSize);
        }

        var (total, capped) = countTotal ? await CountAsync(Logs, filter, ct).ConfigureAwait(false) : (0, false);

        var rows = await Logs.Find(filter)
            .Sort(Builders<LogDocument>.Sort.Descending(l => l.Timestamp).Descending(l => l.Id))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project<LogDocument>(Builders<LogDocument>.Projection.Exclude(l => l.Attributes))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var serviceNames = await GetServiceNamesByResourceIdAsync(rows.Select(r => r.ResourceId), ct).ConfigureAwait(false);

        var items = rows.Select(r => new LogListItemDto(
            r.Id,
            Utc(r.Timestamp),
            r.SeverityNumber,
            r.SeverityText,
            r.Body,
            r.ResourceId is { } rid && serviceNames.TryGetValue(rid, out var name) ? name : null,
            r.TraceId,
            r.SpanId,
            JsonAttributes.Parse(r.AttributesJson))).ToList();

        return new PagedResult<LogListItemDto>(items, countTotal ? total : items.Count, page, pageSize, capped);
    }

    /// <summary>Severity and service counts over the newest <see cref="LogFacets.SampleSize"/>
    /// matches — each with the query's own severity (or service) selection left out.</summary>
    public async Task<LogFacetsDto> GetLogFacetsAsync(LogSearchQuery query, CancellationToken ct)
    {
        var sampled = false;
        var levels = new Dictionary<int, int>();
        var services = new Dictionary<string, int>(StringComparer.Ordinal);
        var sort = Builders<LogDocument>.Sort.Descending(l => l.Timestamp);

        if (await BuildLogFilterAsync(query, withSeverities: false, withServices: true, ct).ConfigureAwait(false) is { } severityFilter)
        {
            var groups = await Logs.Aggregate()
                .Match(severityFilter).Sort(sort).Limit(LogFacets.SampleSize)
                .Group(l => l.SeverityNumber, g => new { Severity = g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var g in groups)
            {
                var level = LogSeverity.Level(g.Severity);
                levels[level] = levels.GetValueOrDefault(level) + g.Count;
            }

            sampled |= groups.Sum(g => g.Count) >= LogFacets.SampleSize;
        }

        if (await BuildLogFilterAsync(query, withSeverities: true, withServices: false, ct).ConfigureAwait(false) is { } serviceFilter)
        {
            var groups = await Logs.Aggregate()
                .Match(serviceFilter).Sort(sort).Limit(LogFacets.SampleSize)
                .Group(l => l.ResourceId, g => new { ResourceId = g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
            var names = await GetServiceNamesByResourceIdAsync(groups.Select(g => g.ResourceId), ct).ConfigureAwait(false);
            foreach (var g in groups)
            {
                var name = g.ResourceId is { } rid && names.TryGetValue(rid, out var n) ? n ?? "" : "";
                services[name] = services.GetValueOrDefault(name) + g.Count;
            }

            sampled |= groups.Sum(g => g.Count) >= LogFacets.SampleSize;
        }

        return new LogFacetsDto(
            levels.OrderBy(l => l.Key).Select(l => new SeverityCountDto(l.Key, l.Value)).ToList(),
            services.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(LogFacets.MaxServices).Select(s => new FacetValueDto(s.Key, s.Value)).ToList(),
            sampled);
    }

    /// <summary>The query as one filter, optionally without its severity or service part (for
    /// facets); null when nothing can match (only services that have never reported).</summary>
    private async Task<FilterDefinition<LogDocument>?> BuildLogFilterAsync(LogSearchQuery query, bool withSeverities, bool withServices, CancellationToken ct)
    {
        var f = Builders<LogDocument>.Filter;
        var filters = new List<FilterDefinition<LogDocument>>();

        if (query.From is { } fromValue)
        {
            filters.Add(f.Gte(l => l.Timestamp, fromValue));
        }

        if (query.To is { } toValue)
        {
            filters.Add(f.Lte(l => l.Timestamp, toValue));
        }

        if (withSeverities && LogSeverity.Levels(query.Severities) is { } levels)
        {
            filters.Add(f.Or(levels.Select(level =>
            {
                var (low, high) = LogSeverity.Range(level);
                return f.And(f.Gte(l => l.SeverityNumber, low), f.Lte(l => l.SeverityNumber, high));
            })));
        }

        foreach (var term in LogTextTerm.Parse(query.Text))
        {
            var bodyMatch = f.Regex(l => l.Body, new BsonRegularExpression(Regex.Escape(term.Text), "i"));
            filters.Add(term.Exclude
                ? f.Not(bodyMatch)
                : term.IsTraceId
                    ? f.Or(f.Eq(l => l.TraceId, term.Text.ToLowerInvariant()), bodyMatch)
                    : bodyMatch);
        }

        if (withServices && query.Services is { Count: > 0 } serviceNames)
        {
            var resourceIds = new List<Guid>();
            foreach (var service in serviceNames.Distinct(StringComparer.Ordinal))
            {
                resourceIds.AddRange(await GetResourceIdsForServiceAsync(service, ct).ConfigureAwait(false));
            }

            if (resourceIds.Count == 0)
            {
                return null;
            }

            filters.Add(f.In(l => l.ResourceId, resourceIds.Select(id => (Guid?)id)));
        }

        filters.AddRange(FieldFilters<LogDocument>(query.Filters));
        return filters.Count == 0 ? f.Empty : f.And(filters);
    }

    public async Task<LogDetailDto?> GetLogAsync(long id, CancellationToken ct)
    {
        var row = await Logs.Find(Builders<LogDocument>.Filter.Eq(l => l.Id, id))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var serviceNames = await GetServiceNamesByResourceIdAsync([row.ResourceId], ct).ConfigureAwait(false);

        return new LogDetailDto(
            row.Id, Utc(row.Timestamp), row.ObservedTimestamp is { } observed ? Utc(observed) : null,
            row.SeverityNumber, row.SeverityText, row.Body,
            row.ResourceId is { } rid && serviceNames.TryGetValue(rid, out var name) ? name : null,
            row.TraceId, row.SpanId, row.ScopeLibraryName, row.ScopeLibraryVersion, row.SchemaUrl,
            JsonAttributes.Parse(row.AttributesJson));
    }

    // ---------------------------------------------------------------- summary / lookups

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);

        var logsLast24h = await Logs.CountDocumentsAsync(
            Builders<LogDocument>.Filter.Gte(l => l.Timestamp, since), cancellationToken: ct).ConfigureAwait(false);

        var recent = Builders<TraceDocument>.Filter.Gte(t => t.StartTime, since);
        var tracesLast24h = await Traces.CountDocumentsAsync(recent, cancellationToken: ct).ConfigureAwait(false);
        var errorTracesLast24h = await Traces.CountDocumentsAsync(
            recent & Builders<TraceDocument>.Filter.Eq(t => t.HasError, true), cancellationToken: ct).ConfigureAwait(false);

        var errorRate = tracesLast24h == 0 ? 0 : Math.Round(100.0 * errorTracesLast24h / tracesLast24h, 1);

        var recentLogs = await SearchLogsCoreAsync(new LogSearchQuery { PageSize = SummaryListSize }, countTotal: false, ct).ConfigureAwait(false);
        var recentTraces = await SearchTracesCoreAsync(new TraceSearchQuery { PageSize = SummaryListSize }, countTotal: false, ct).ConfigureAwait(false);

        return new DashboardSummaryDto(
            (int)logsLast24h, (int)tracesLast24h, (int)errorTracesLast24h, errorRate,
            recentLogs.Items, recentTraces.Items);
    }

    /// <summary>Server-side $group pipelines over the window: trace documents per bucket, logs per
    /// bucket, trace documents per root operation.</summary>
    public async Task<DashboardOverviewDto> GetOverviewAsync(DateTime from, DateTime to, int bucketCount, CancellationToken ct)
    {
        var bucketSeconds = DashboardOverview.BucketSeconds(from, to, bucketCount);
        var fromDate = new BsonDateTime(from);
        BsonDocument Bucket(string field) => new("$floor", new BsonDocument("$divide", new BsonArray
        {
            new BsonDocument("$subtract", new BsonArray { field, fromDate }),
            bucketSeconds * 1000L,
        }));
        BsonDocument CountIf(BsonDocument condition) => new("$sum", new BsonDocument("$cond", new BsonArray { condition, 1, 0 }));
        var duration = new BsonDocument("$subtract", new BsonArray { "$EndTime", "$StartTime" });

        var traceMatch = Builders<TraceDocument>.Filter.Gte(t => t.StartTime, from) & Builders<TraceDocument>.Filter.Lt(t => t.StartTime, to);
        var traceRows = (await Traces.Aggregate()
                .Match(traceMatch)
                .Group(new BsonDocument
                {
                    { "_id", Bucket("$StartTime") },
                    { "Count", new BsonDocument("$sum", 1) },
                    { "Errors", CountIf(new BsonDocument("$eq", new BsonArray { "$HasError", true })) },
                    { "Avg", new BsonDocument("$avg", duration) },
                })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d["_id"].ToInt64(), d["Count"].ToInt32(), d["Errors"].ToInt32(), d["Avg"].IsBsonNull ? 0 : d["Avg"].ToDouble()));

        var logRows = (await Logs.Aggregate()
                .Match(Builders<LogDocument>.Filter.Gte(l => l.Timestamp, from) & Builders<LogDocument>.Filter.Lt(l => l.Timestamp, to))
                .Group(new BsonDocument
                {
                    { "_id", Bucket("$Timestamp") },
                    { "Count", new BsonDocument("$sum", 1) },
                    { "Warnings", CountIf(new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$gte", new BsonArray { "$SeverityNumber", 13 }),
                            new BsonDocument("$lt", new BsonArray { "$SeverityNumber", 17 }),
                        })) },
                    { "Errors", CountIf(new BsonDocument("$gte", new BsonArray { "$SeverityNumber", 17 })) },
                })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d["_id"].ToInt64(), d["Count"].ToInt32(), d["Warnings"].ToInt32(), d["Errors"].ToInt32()));

        var operations = (await Traces.Aggregate()
                .Match(traceMatch)
                .Group(new BsonDocument
                {
                    { "_id", new BsonDocument { { "Name", "$RootName" }, { "Service", "$ServiceName" } } },
                    { "Count", new BsonDocument("$sum", 1) },
                    { "Errors", CountIf(new BsonDocument("$eq", new BsonArray { "$HasError", true })) },
                    { "Avg", new BsonDocument("$avg", duration) },
                    { "Max", new BsonDocument("$max", duration) },
                })
                .Sort(new BsonDocument("Count", -1))
                .Limit(DashboardOverview.TopOperationCount)
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => new OperationStatsDto(
                d["_id"]["Name"].IsBsonNull ? "(unnamed)" : d["_id"]["Name"].AsString,
                d["_id"].AsBsonDocument.GetValue("Service", BsonNull.Value) is { IsString: true } svc ? svc.AsString : null,
                d["Count"].ToInt32(),
                d["Errors"].ToInt32(),
                Math.Round(d["Avg"].IsBsonNull ? 0 : d["Avg"].ToDouble(), 1),
                d["Max"].IsBsonNull ? 0 : d["Max"].ToInt64()))
            .ToList();

        return DashboardOverview.Assemble(from, to, bucketSeconds, traceRows, logRows, operations);
    }

    public async Task<IReadOnlyList<string>> ListServiceNamesAsync(CancellationToken ct)
    {
        var cursor = await Resources.DistinctAsync(
            r => r.ServiceName,
            Builders<ResourceDocument>.Filter.Ne(r => r.ServiceName, null),
            cancellationToken: ct).ConfigureAwait(false);
        var names = await cursor.ToListAsync(ct).ConfigureAwait(false);

        return names.Where(n => n is not null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every attribute key recorded for the scope (see <see cref="MongoAttributeKeys"/>),
    /// cached briefly.</summary>
    public Task<IReadOnlyList<string>> ListAttributeKeysAsync(AttributeScope scope, CancellationToken ct)
    {
        var cache = scope == AttributeScope.Traces ? _traceAttributeKeyCache : _logAttributeKeyCache;
        return cache.GetOrRefreshAsync(async refreshCt =>
        {
            var keys = await MongoAttributeKeys.Collection(Database, options.Schema)
                .Find(Builders<AttributeKeyDocument>.Filter.Eq(d => d.Source, PowerTraces.Storage.Sql.SqlAttributeKeys.SourceOf(scope)))
                .SortBy(d => d.Key)
                .Project(d => d.Key)
                .ToListAsync(refreshCt).ConfigureAwait(false);
            return (IReadOnlyList<string>)keys;
        }, ct);
    }

    // ---------------------------------------------------------------- ignore rules

    public Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct) =>
        MongoIgnoreRules.ListAsync(options.ConnectionString, options.Schema, ct);

    public Task AddIgnoreRuleAsync(IgnoreRuleKind kind, string name, CancellationToken ct) =>
        MongoIgnoreRules.AddAsync(options.ConnectionString, options.Schema, kind, name, ct);

    public Task DeleteIgnoreRuleAsync(Guid id, CancellationToken ct) =>
        MongoIgnoreRules.DeleteAsync(options.ConnectionString, options.Schema, id, ct);

    // ---------------------------------------------------------------- helpers

    /// <summary>One <c>$elemMatch</c> per filter on the document's indexed (K, V) array — equality,
    /// its negation for not-equals, or a case-insensitive, regex-escaped substring match for has-word.</summary>
    private static IEnumerable<FilterDefinition<TDocument>> FieldFilters<TDocument>(IReadOnlyList<FieldFilter>? filters)
    {
        if (filters is null)
        {
            yield break;
        }

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Key) || filter.Value is null)
            {
                continue;
            }

            var value = AttributeText.Truncate(filter.Value, AttributeText.MaxValueLength);
            var match = new BsonDocument
            {
                { "K", AttributeText.Truncate(filter.Key.Trim(), AttributeText.MaxKeyLength) },
                // Typed as BsonValue on both branches: a bare string here would convert implicitly
                // to BsonRegularExpression, which parses "/a/b" as the pattern "a" with options "b".
                { "V", filter.Match == FieldMatch.HasWord ? new BsonRegularExpression(Regex.Escape(value), "i") : (BsonValue)new BsonString(value) },
            };
            var elemMatch = new BsonDocument("$elemMatch", match);
            yield return new BsonDocument("Attributes", filter.Match == FieldMatch.NotEquals ? new BsonDocument("$not", elemMatch) : elemMatch);
        }
    }

    /// <summary>Counts matches up to <see cref="PagedResult{T}.CountLimit"/> (one past it, to know
    /// whether the limit was hit).</summary>
    private static async Task<(int Total, bool Capped)> CountAsync<TDocument>(IMongoCollection<TDocument> collection, FilterDefinition<TDocument> filter, CancellationToken ct)
    {
        var limit = PagedResult<object>.CountLimit;
        var count = await collection.CountDocumentsAsync(filter, new CountOptions { Limit = limit + 1 }, ct).ConfigureAwait(false);
        return count > limit ? (limit, true) : ((int)count, false);
    }

    /// <summary>Spans/logs store ResourceId, not ServiceName, so filtering "by service" means first
    /// finding which Resource documents have that name.</summary>
    private async Task<List<Guid>> GetResourceIdsForServiceAsync(string service, CancellationToken ct)
    {
        var cursor = await Resources.DistinctAsync(
            r => r.Id,
            Builders<ResourceDocument>.Filter.Eq(r => r.ServiceName, service),
            cancellationToken: ct).ConfigureAwait(false);

        return await cursor.ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Batch-resolves ServiceName for the ResourceIds on a page of logs/spans — the
    /// read-side equivalent of the SQL providers' LEFT JOIN Resources.</summary>
    private async Task<Dictionary<Guid, string?>> GetServiceNamesByResourceIdAsync(IEnumerable<Guid?> resourceIds, CancellationToken ct)
    {
        var ids = resourceIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var docs = await Resources.Find(Builders<ResourceDocument>.Filter.In(r => r.Id, ids))
            .Project(r => new { r.Id, r.ServiceName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return docs.ToDictionary(d => d.Id, d => d.ServiceName);
    }

    /// <summary>BSON dates come back as UTC already; this just makes the Kind explicit so JSON
    /// serialization always emits the "Z".</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
