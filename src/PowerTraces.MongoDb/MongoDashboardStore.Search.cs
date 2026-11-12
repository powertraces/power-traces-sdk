using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Trace search over each trace document's <see cref="TraceDocument.Terms"/> array,
/// served by its multikey (Terms, StartTime) index: an exact term is an equality on the array, a
/// prefix an anchored, case-sensitive regex — both index range scans.</summary>
public sealed partial class MongoDashboardStore : ISearchIndexBackfill
{
    private const int FieldValueScanLimit = 5_000;
    private const int FieldValueCount = 20;

    public Task<PagedResult<TraceListItemDto>> SearchTracesAsync(TraceSearchQuery query, CancellationToken ct) =>
        SearchTracesCoreAsync(query, countTotal: true, ct);

    private async Task<PagedResult<TraceListItemDto>> SearchTracesCoreAsync(TraceSearchQuery query, bool countTotal, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var plan = TraceSearchPlan.Build(query);
        var filter = plan.MatchesNothing ? null : await BuildTraceFilterAsync(query, plan, facets: false, ct).ConfigureAwait(false);
        if (filter is null)
        {
            return new PagedResult<TraceListItemDto>([], 0, page, pageSize);
        }

        var (total, capped) = countTotal ? await CountAsync(Traces, filter, ct).ConfigureAwait(false) : (0, false);

        var docs = await Traces.Find(filter)
            .Sort(Builders<TraceDocument>.Sort.Descending(t => t.StartTime).Ascending(t => t.Id))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project<TraceDocument>(Builders<TraceDocument>.Projection
                .Exclude(t => t.SpanNames)
                .Exclude(t => t.ResourceIds)
                .Exclude(t => t.Attributes)
                .Exclude(t => t.Terms))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = docs.Select(d => new TraceListItemDto(
            d.Id,
            d.RootName,
            d.ServiceName,
            Utc(d.StartTime),
            (long)(d.EndTime - d.StartTime).TotalMilliseconds,
            d.HasError,
            d.SpanCount,
            JsonAttributes.Parse(d.RootAttributesJson))).ToList();

        if (items.Count > 0 && (plan.Words.Count > 0 || plan.Filters.Count > 0))
        {
            var spans = await LoadSpanFieldsAsync(items.Select(i => i.TraceId).ToList(), ct).ConfigureAwait(false);
            items = items.Select(item => spans.TryGetValue(item.TraceId, out var list)
                ? item with { Match = TraceMatchFinder.Find(plan, list.SelectMany(s => SearchTerms.Fields(s.Name, s.StatusMessage, s.AttributesJson, s.EventsJson))) }
                : item).ToList();
        }

        return new PagedResult<TraceListItemDto>(items, countTotal ? total : items.Count, page, pageSize, capped);
    }

    public async Task<TraceFacetsDto> GetTraceFacetsAsync(TraceSearchQuery query, CancellationToken ct)
    {
        var plan = TraceSearchPlan.Build(query);
        var filter = plan.MatchesNothing ? null : await BuildTraceFilterAsync(query, plan, facets: true, ct).ConfigureAwait(false);
        if (filter is null)
        {
            return new TraceFacetsDto([], 0, 0, false);
        }

        var groups = await Traces.Aggregate()
            .Match(filter)
            .Sort(Builders<TraceDocument>.Sort.Descending(t => t.StartTime))
            .Limit(TraceFacets.SampleSize)
            .Group(new BsonDocument
            {
                { "_id", new BsonDocument { { "n", "$RootName" }, { "e", "$HasError" } } },
                { "c", new BsonDocument("$sum", 1) },
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var endpoints = new Dictionary<string, int>(StringComparer.Ordinal);
        int errors = 0, ok = 0;
        foreach (var group in groups)
        {
            var key = group["_id"].AsBsonDocument;
            var name = key.GetValue("n", BsonNull.Value) is { IsString: true } n ? n.AsString : "";
            var count = group["c"].ToInt32();
            endpoints[name] = endpoints.GetValueOrDefault(name) + count;
            if (key.GetValue("e", false).ToBoolean())
            {
                errors += count;
            }
            else
            {
                ok += count;
            }
        }

        return new TraceFacetsDto(
            endpoints.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(TraceFacets.MaxEndpoints).Select(e => new FacetValueDto(e.Key, e.Value)).ToList(),
            errors, ok, errors + ok >= TraceFacets.SampleSize);
    }

    public async Task<IReadOnlyList<FacetValueDto>> ListFieldValuesAsync(string key, string? prefix, DateTime since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var low = SearchTerms.Attribute(key.Trim(), prefix ?? "");
        var valueStart = SearchTerms.Attribute(key.Trim(), "").Length;
        var regex = new BsonRegularExpression("^" + Regex.Escape(low));

        var groups = await Traces.Aggregate()
            .Match(new BsonDocument { { "Terms", regex }, { "StartTime", new BsonDocument("$gte", since) } })
            .Limit(FieldValueScanLimit)
            .Project(new BsonDocument { { "_id", 0 }, { "Terms", 1 } })
            .Unwind("Terms")
            .Match(new BsonDocument("Terms", regex))
            .Group(new BsonDocument { { "_id", "$Terms" }, { "c", new BsonDocument("$sum", 1) } })
            .Sort(new BsonDocument("c", -1))
            .Limit(FieldValueCount)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return groups
            .Select(g => (Term: g["_id"].AsString, Count: g["c"].ToInt32()))
            .Where(g => g.Term.Length > valueStart)
            .Select(g => new FacetValueDto(g.Term[valueStart..], g.Count))
            .ToList();
    }

    /// <summary>The query as one filter on trace documents — or null when nothing can match (a
    /// service that has never reported). With <paramref name="facets"/>, the endpoint and status
    /// filters are left out.</summary>
    private async Task<FilterDefinition<TraceDocument>?> BuildTraceFilterAsync(TraceSearchQuery query, TraceSearchPlan plan, bool facets, CancellationToken ct)
    {
        var f = Builders<TraceDocument>.Filter;
        var filters = new List<FilterDefinition<TraceDocument>>();

        if (query.From is { } from)
        {
            filters.Add(f.Gte(t => t.StartTime, from));
        }

        if (query.To is { } to)
        {
            filters.Add(f.Lte(t => t.StartTime, to));
        }

        if (!facets && query.HasError is { } hasError)
        {
            filters.Add(f.Eq(t => t.HasError, hasError));
        }

        if (!facets && query.Endpoints is { Count: > 0 } endpoints)
        {
            filters.Add(f.In(t => t.RootName, endpoints));
        }

        var duration = new BsonDocument("$subtract", new BsonArray { "$EndTime", "$StartTime" });
        if (query.MinDurationMs is { } min)
        {
            filters.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { duration, min })));
        }

        if (query.MaxDurationMs is { } max)
        {
            filters.Add(new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { duration, max })));
        }

        if (plan.TraceId is { } traceId)
        {
            filters.Add(f.Eq(t => t.Id, traceId));
        }

        if (query.Services is { Count: > 0 } services)
        {
            var cursor = await Resources.DistinctAsync(r => r.Id, Builders<ResourceDocument>.Filter.In(r => r.ServiceName, services), cancellationToken: ct).ConfigureAwait(false);
            var resourceIds = await cursor.ToListAsync(ct).ConfigureAwait(false);
            if (resourceIds.Count == 0)
            {
                return null;
            }

            filters.Add(f.AnyIn(t => t.ResourceIds, resourceIds));
        }

        foreach (var group in plan.Required)
        {
            var lookups = group.Select(Lookup).ToList();
            filters.Add(lookups.Count == 1 ? lookups[0] : f.Or(lookups));
        }

        foreach (var lookup in plan.Excluded)
        {
            filters.Add(f.Not(Lookup(lookup)));
        }

        return filters.Count == 0 ? f.Empty : f.And(filters);

        FilterDefinition<TraceDocument> Lookup(TermLookup lookup) => lookup.Prefix
            // Anchored and case-sensitive, with the term regex-escaped: an index range scan, and
            // user text can't inject regex syntax.
            ? f.Regex("Terms", new BsonRegularExpression("^" + Regex.Escape(lookup.Term)))
            : f.AnyEq("Terms", lookup.Term);
    }

    private async Task<Dictionary<string, List<(string Name, string? StatusMessage, string? AttributesJson, string? EventsJson)>>> LoadSpanFieldsAsync(
        IReadOnlyList<string> traceIds, CancellationToken ct)
    {
        var docs = await Spans.Find(Builders<SpanDocument>.Filter.In(s => s.TraceId, traceIds))
            .Project(s => new { s.TraceId, s.Name, s.StatusMessage, s.AttributesJson, s.EventsJson, s.ParentSpanId, s.StartTime })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Root spans first, then by start time: the explanation prefers the request itself.
        return docs
            .OrderBy(d => d.ParentSpanId.Length == 0 ? 0 : 1).ThenBy(d => d.StartTime)
            .GroupBy(d => d.TraceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => (d.Name, d.StatusMessage, (string?)d.AttributesJson, (string?)d.EventsJson)).ToList(), StringComparer.Ordinal);
    }

    private sealed record BackfillCursor(DateTime? Before, string? BeforeId);

    /// <summary>Indexes the next batch of traces older than the backfill cursor (newest first),
    /// then moves the cursor past them; deletes the marker once none are left.</summary>
    public async Task<int> BackfillSearchIndexAsync(int maxTraces, CancellationToken ct)
    {
        var marker = await Settings.Find(Builders<SettingDocument>.Filter.Eq(d => d.Id, PowerTracesSchema.SearchBackfillSettingKey))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (marker is null)
        {
            return 0;
        }

        var cursor = Deserialize<BackfillCursor>(marker.Value) ?? new BackfillCursor(null, null);
        var before = cursor.Before ?? marker.UpdatedAt;
        var f = Builders<TraceDocument>.Filter;
        var filter = cursor.BeforeId is null
            ? f.Lte(t => t.StartTime, before)
            : f.Lt(t => t.StartTime, before) | (f.Eq(t => t.StartTime, before) & f.Lt(t => t.Id, cursor.BeforeId));

        var traces = await Traces.Find(filter)
            .Sort(Builders<TraceDocument>.Sort.Descending(t => t.StartTime).Descending(t => t.Id))
            .Limit(Math.Clamp(maxTraces, 1, 500))
            .Project(t => new { t.Id, t.StartTime })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (traces.Count == 0)
        {
            await Settings.DeleteOneAsync(Builders<SettingDocument>.Filter.Eq(d => d.Id, PowerTracesSchema.SearchBackfillSettingKey), ct).ConfigureAwait(false);
            return 0;
        }

        var spans = await LoadSpanFieldsAsync(traces.Select(t => t.Id).ToList(), ct).ConfigureAwait(false);
        var writes = new List<WriteModel<TraceDocument>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trace in traces)
        {
            if (!spans.TryGetValue(trace.Id, out var list))
            {
                continue;
            }

            writes.Add(new UpdateOneModel<TraceDocument>(
                f.Eq(t => t.Id, trace.Id),
                Builders<TraceDocument>.Update.AddToSetEach(t => t.Terms, SearchTerms.ForSpans(list).Take(MongoTraceSummaries.MaxTerms))));
            foreach (var span in list)
            {
                keys.UnionWith(SearchTerms.Fields(null, null, span.AttributesJson, span.EventsJson).Select(field => field.Key));
            }
        }

        if (writes.Count > 0)
        {
            await Traces.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        }

        await MongoAttributeKeys.RecordAsync(Database, options.ConnectionString, options.Schema, AttributeScope.Traces, keys, ct).ConfigureAwait(false);

        var last = traces[^1];
        await WriteSettingAsync(PowerTracesSchema.SearchBackfillSettingKey,
            JsonSerializer.Serialize(new BackfillCursor(Utc(last.StartTime), last.Id), SettingsJson), ct).ConfigureAwait(false);
        return traces.Count;
    }
}
