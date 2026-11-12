using System.Data.Common;
using System.Text;
using System.Text.Json;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;

namespace PowerTraces.Storage.Sql;

/// <summary>Trace search over the <see cref="PowerTracesSchema.SearchTermsTable"/> index. Each
/// text word or field filter becomes one <c>TraceId IN (SELECT ... FROM SearchTerms WHERE Term
/// ...)</c> — an index range on (Term, StartTime) — so a search costs the number of traces holding
/// the term, not the number of spans in the time range.</summary>
public abstract partial class SqlDashboardStore : ISearchIndexBackfill
{
    private const int FieldValueScanLimit = 5_000;
    private const int FieldValueCount = 20;
    private const int TermsPerInsert = 300; // 3 parameters each — under every database's limit

    private string SearchTermsTable => dialect.Table(options.Schema, PowerTracesSchema.SearchTermsTable);

    /// <summary>The trace summary table, for providers that keep the search index on it.</summary>
    protected string TracesTable => Traces;

    public Task<PagedResult<TraceListItemDto>> SearchTracesAsync(TraceSearchQuery query, CancellationToken ct) =>
        SearchTracesCoreAsync(query, countTotal: true, ct);

    private async Task<PagedResult<TraceListItemDto>> SearchTracesCoreAsync(TraceSearchQuery query, bool countTotal, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var plan = TraceSearchPlan.Build(query);
        if (plan.MatchesNothing)
        {
            return new PagedResult<TraceListItemDto>([], 0, page, pageSize);
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        var where = new StringBuilder("1 = 1");
        if (!await AppendTraceSearchAsync(connection, cmd, where, query, plan, facets: false, ct).ConfigureAwait(false))
        {
            return new PagedResult<TraceListItemDto>([], 0, page, pageSize);
        }

        var fromWhere = $"FROM {Traces} t WHERE {where}";
        var (total, capped) = countTotal ? await CountAsync(cmd, fromWhere, ct).ConfigureAwait(false) : (0, false);

        cmd.CommandText = dialect.Sql($"""
            SELECT t."TraceId", t."RootName", t."ServiceName", t."StartTime", t."EndTime", t."HasError", t."SpanCount", t."RootAttributesJson"
            {fromWhere}
            ORDER BY t."StartTime" DESC, t."TraceId"
            {dialect.Paging}
            """);
        dialect.AddParameter(cmd, "@Skip", (page - 1) * pageSize);
        dialect.AddParameter(cmd, "@Take", pageSize);

        var items = new List<TraceListItemDto>(pageSize);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var start = SqlDialect.ReadUtc(reader, 3);
                var end = SqlDialect.ReadUtc(reader, 4);
                items.Add(new TraceListItemDto(
                    reader.GetString(0),
                    SqlDialect.ReadStringOrNull(reader, 1),
                    SqlDialect.ReadStringOrNull(reader, 2),
                    start,
                    (long)(end - start).TotalMilliseconds,
                    SqlDialect.ReadBool(reader, 5),
                    (int)SqlDialect.ReadInt64(reader.GetValue(6)),
                    JsonAttributes.Parse(SqlDialect.ReadStringOrNull(reader, 7))));
            }
        }

        if (items.Count > 0 && (plan.Words.Count > 0 || plan.Filters.Count > 0))
        {
            items = await AddMatchesAsync(connection, items, plan, ct).ConfigureAwait(false);
        }

        return new PagedResult<TraceListItemDto>(items, countTotal ? total : items.Count, page, pageSize, capped);
    }

    /// <summary>Endpoint and status counts over the newest <see cref="TraceFacets.SampleSize"/>
    /// matches of the query without its own endpoint and status filters.</summary>
    public async Task<TraceFacetsDto> GetTraceFacetsAsync(TraceSearchQuery query, CancellationToken ct)
    {
        var plan = TraceSearchPlan.Build(query);
        var empty = new TraceFacetsDto([], 0, 0, false);
        if (plan.MatchesNothing)
        {
            return empty;
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        var where = new StringBuilder("1 = 1");
        if (!await AppendTraceSearchAsync(connection, cmd, where, query, plan, facets: true, ct).ConfigureAwait(false))
        {
            return empty;
        }

        var sample = dialect.SelectLimited("""t."RootName", t."HasError" """, $"""FROM {Traces} t WHERE {where} ORDER BY t."StartTime" DESC""", TraceFacets.SampleSize);
        cmd.CommandText = dialect.Sql($"""
            SELECT x."RootName", x."HasError", COUNT(*) FROM ({sample}) x GROUP BY x."RootName", x."HasError"
            """);

        var endpoints = new Dictionary<string, int>(StringComparer.Ordinal);
        int errors = 0, ok = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = SqlDialect.ReadStringOrNull(reader, 0) ?? "";
                var count = (int)SqlDialect.ReadInt64(reader.GetValue(2));
                endpoints[name] = endpoints.GetValueOrDefault(name) + count;
                if (SqlDialect.ReadBool(reader, 1))
                {
                    errors += count;
                }
                else
                {
                    ok += count;
                }
            }
        }

        return new TraceFacetsDto(
            endpoints.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(TraceFacets.MaxEndpoints).Select(e => new FacetValueDto(e.Key, e.Value)).ToList(),
            errors, ok, errors + ok >= TraceFacets.SampleSize);
    }

    /// <summary>Reads the field's <c>a:key=value</c> terms from the index — a bounded range scan,
    /// so a very common field's counts are over its first <see cref="FieldValueScanLimit"/> rows.</summary>
    public virtual async Task<IReadOnlyList<FacetValueDto>> ListFieldValuesAsync(string key, string? prefix, DateTime since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var low = SearchTerms.Attribute(key.Trim(), prefix ?? "");
        var valueStart = SearchTerms.Attribute(key.Trim(), "").Length;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        var scan = dialect.SelectLimited("""st."Term" """, $"""FROM {SearchTermsTable} st WHERE st."Term" >= @Low AND st."Term" < @High AND st."StartTime" >= @Since""", FieldValueScanLimit);
        cmd.CommandText = dialect.Sql($"""SELECT x."Term", COUNT(*) FROM ({scan}) x GROUP BY x."Term" """);
        dialect.AddParameter(cmd, "@Low", low);
        dialect.AddParameter(cmd, "@High", SearchTerms.PrefixUpperBound(low));
        dialect.AddParameter(cmd, "@Since", since);

        var values = new List<FacetValueDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var term = reader.GetString(0);
            if (term.Length > valueStart)
            {
                values.Add(new FacetValueDto(term[valueStart..], (int)SqlDialect.ReadInt64(reader.GetValue(1))));
            }
        }

        return values.OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.Ordinal).Take(FieldValueCount).ToList();
    }

    /// <summary>Appends the query's predicates on the trace summary <c>t</c>. With
    /// <paramref name="facets"/>, the endpoint and status filters are left out. Returns false when
    /// nothing can match (a service that has never reported).</summary>
    private async Task<bool> AppendTraceSearchAsync(
        QueryConnection connection, DbCommand cmd, StringBuilder where, TraceSearchQuery query, TraceSearchPlan plan, bool facets, CancellationToken ct)
    {
        if (query.From is { } from)
        {
            where.Append(""" AND t."StartTime" >= @From""");
            dialect.AddParameter(cmd, "@From", from);
        }

        if (query.To is { } to)
        {
            where.Append(""" AND t."StartTime" <= @To""");
            dialect.AddParameter(cmd, "@To", to);
        }

        if (!facets && query.HasError is { } hasError)
        {
            where.Append(""" AND t."HasError" = @HasError""");
            dialect.AddParameter(cmd, "@HasError", hasError);
        }

        if (!facets && query.Endpoints is { Count: > 0 } endpoints)
        {
            var names = new List<string>();
            foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
            {
                names.Add($"@Endpoint{names.Count}");
                dialect.AddParameter(cmd, names[^1], endpoint);
            }

            where.Append($""" AND t."RootName" IN ({string.Join(", ", names)})""");
        }

        var duration = dialect.MillisecondsBetween("""t."StartTime" """, """t."EndTime" """);
        if (query.MinDurationMs is { } min)
        {
            where.Append($" AND {duration} >= @MinDuration");
            dialect.AddParameter(cmd, "@MinDuration", (double)min);
        }

        if (query.MaxDurationMs is { } max)
        {
            where.Append($" AND {duration} <= @MaxDuration");
            dialect.AddParameter(cmd, "@MaxDuration", (double)max);
        }

        if (plan.TraceId is { } traceId)
        {
            where.Append(""" AND t."TraceId" = @TraceId""");
            dialect.AddParameter(cmd, "@TraceId", traceId, ansi: true);
        }

        if (query.Services is { Count: > 0 } services)
        {
            var resourceIds = await AppendResourceIdsAsync(connection, cmd, services, ct).ConfigureAwait(false);
            if (resourceIds is null)
            {
                return false;
            }

            where.Append($""" AND EXISTS (SELECT 1 FROM {Spans} s WHERE s."TraceId" = t."TraceId" AND s."ResourceId" IN ({resourceIds}))""");
        }

        AppendTermPredicates(cmd, where, plan, boundedByFrom: query.From is not null);
        return true;
    }

    /// <summary>Appends the plan's index lookups on the trace summary <c>t</c>: by default one
    /// <c>IN</c> subquery per required group and one <c>NOT EXISTS</c> per exclusion, each a range on
    /// the <see cref="PowerTracesSchema.SearchTermsTable"/> index. A provider that keeps the index
    /// elsewhere overrides it.</summary>
    protected virtual void AppendTermPredicates(DbCommand cmd, StringBuilder where, TraceSearchPlan plan, bool boundedByFrom)
    {
        // Every index entry of a trace is at or after the trace's start, so the time range's
        // start also bounds the index range — the search reads only the window's entries.
        var since = boundedByFrom ? """ AND st."StartTime" >= @From""" : "";
        var n = 0;
        foreach (var group in plan.Required)
        {
            var lookups = new List<string>(group.Count);
            foreach (var lookup in group)
            {
                lookups.Add(TermPredicate(cmd, lookup, ref n));
            }

            where.Append($""" AND t."TraceId" IN (SELECT {dialect.SearchTraceIdText("st")} FROM {SearchTermsTable} st WHERE ({string.Join(" OR ", lookups)}){since})""");
        }

        // NOT EXISTS, not NOT IN: some databases plan a NOT IN over every trace holding the term
        // badly (null-aware anti-joins), while NOT EXISTS lets them probe the TraceId index per
        // candidate or hash the term's range, whichever is cheaper.
        foreach (var lookup in plan.Excluded)
        {
            where.Append($""" AND NOT EXISTS (SELECT 1 FROM {SearchTermsTable} st WHERE st."TraceId" = {dialect.SearchTraceIdOf("""t."TraceId" """)} AND {TermPredicate(cmd, lookup, ref n)})""");
        }
    }

    private string TermPredicate(DbCommand cmd, TermLookup lookup, ref int n)
    {
        var name = $"@Term{n++}";
        dialect.AddParameter(cmd, name, lookup.Term);
        if (!lookup.Prefix)
        {
            return $"""st."Term" = {name}""";
        }

        dialect.AddParameter(cmd, name + "End", SearchTerms.PrefixUpperBound(lookup.Term));
        return $"""(st."Term" >= {name} AND st."Term" < {name}End)""";
    }

    /// <summary>The resource ids of every listed service, bound on <paramref name="cmd"/> — or
    /// <see langword="null"/> when none of them has ever reported.</summary>
    private async Task<string?> AppendResourceIdsAsync(QueryConnection connection, DbCommand cmd, IReadOnlyList<string> services, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using (var lookup = connection.CreateCommand())
        {
            var names = new List<string>();
            foreach (var service in services.Distinct(StringComparer.Ordinal))
            {
                names.Add($"@Service{names.Count}");
                dialect.AddParameter(lookup, names[^1], service);
            }

            lookup.CommandText = dialect.Sql($"""SELECT "Id" FROM {Resources} WHERE "ServiceName" IN ({string.Join(", ", names)})""");
            await using var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(SqlDialect.ReadGuid(reader, 0));
            }
        }

        if (ids.Count == 0)
        {
            return null;
        }

        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = $"@Resource{i}";
            dialect.AddParameter(cmd, parameters[i], ids[i]);
        }

        return string.Join(", ", parameters);
    }

    /// <summary>Loads the page's spans and finds, per trace, the field that matched.</summary>
    private async Task<List<TraceListItemDto>> AddMatchesAsync(QueryConnection connection, List<TraceListItemDto> items, TraceSearchPlan plan, CancellationToken ct)
    {
        var spans = await LoadSpanFieldsAsync(connection, items.Select(i => i.TraceId).ToList(), ct).ConfigureAwait(false);
        return items.Select(item => spans.TryGetValue(item.TraceId, out var list)
            ? item with { Match = TraceMatchFinder.Find(plan, list.SelectMany(s => SearchTerms.Fields(s.Name, s.StatusMessage, s.AttributesJson, s.EventsJson))) }
            : item).ToList();
    }

    private async Task<Dictionary<string, List<(string Name, string? StatusMessage, string? AttributesJson, string? EventsJson)>>> LoadSpanFieldsAsync(
        QueryConnection connection, IReadOnlyList<string> traceIds, CancellationToken ct)
    {
        var result = new Dictionary<string, List<(string, string?, string?, string?)>>(StringComparer.Ordinal);
        await using var cmd = connection.CreateCommand();
        var names = new string[traceIds.Count];
        for (var i = 0; i < traceIds.Count; i++)
        {
            names[i] = $"@T{i}";
            dialect.AddParameter(cmd, names[i], traceIds[i], ansi: true);
        }

        // Root spans first, then by start time: the explanation prefers the request itself.
        cmd.CommandText = dialect.Sql($"""
            SELECT s."TraceId", s."Name", s."StatusMessage", s."AttributesJson", s."EventsJson"
            FROM {Spans} s WHERE s."TraceId" IN ({string.Join(", ", names)})
            ORDER BY s."TraceId", CASE WHEN s."ParentSpanId" = '' THEN 0 ELSE 1 END, s."StartTime"
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var traceId = reader.GetString(0);
            if (!result.TryGetValue(traceId, out var list))
            {
                result[traceId] = list = [];
            }

            list.Add((reader.GetString(1), SqlDialect.ReadStringOrNull(reader, 2), SqlDialect.ReadStringOrNull(reader, 3), SqlDialect.ReadStringOrNull(reader, 4)));
        }

        return result;
    }

    // ---------------------------------------------------------------- backfill

    private sealed record BackfillCursor(DateTime? Before, string? BeforeId);

    /// <summary>Indexes the next batch of traces older than the backfill cursor (newest first), then
    /// moves the cursor past them; deletes the marker once none are left. Traces that straddle the
    /// moment the index was created may be indexed twice — harmless, lookups are by membership.</summary>
    public async Task<int> BackfillSearchIndexAsync(int maxTraces, CancellationToken ct)
    {
        var (value, createdAt) = await ReadSettingAsync(PowerTracesSchema.SearchBackfillSettingKey, ct).ConfigureAwait(false);
        if (value is null)
        {
            return 0;
        }

        var cursor = Deserialize<BackfillCursor>(value) ?? new BackfillCursor(null, null);
        var before = cursor.Before ?? createdAt ?? DateTime.UtcNow;

        var traces = new List<(string TraceId, DateTime StartTime)>();
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.CreateCommand())
        {
            var where = cursor.BeforeId is null
                ? """t."StartTime" <= @Before"""
                : """(t."StartTime" < @Before OR (t."StartTime" = @Before AND t."TraceId" < @BeforeId))""";
            cmd.CommandText = dialect.Sql(dialect.SelectLimited(
                """t."TraceId", t."StartTime" """,
                $"""FROM {Traces} t WHERE {where} ORDER BY t."StartTime" DESC, t."TraceId" DESC""",
                Math.Clamp(maxTraces, 1, 500)));
            dialect.AddParameter(cmd, "@Before", before);
            if (cursor.BeforeId is not null)
            {
                dialect.AddParameter(cmd, "@BeforeId", cursor.BeforeId, ansi: true);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                traces.Add((reader.GetString(0), SqlDialect.ReadUtc(reader, 1)));
            }
        }

        if (traces.Count == 0)
        {
            await DeleteSettingAsync(PowerTracesSchema.SearchBackfillSettingKey, ct).ConfigureAwait(false);
            return 0;
        }

        var rows = new List<TraceTerms>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        {
            var spans = await LoadSpanFieldsAsync(connection, traces.Select(t => t.TraceId).ToList(), ct).ConfigureAwait(false);
            foreach (var (traceId, startTime) in traces)
            {
                if (!spans.TryGetValue(traceId, out var list))
                {
                    continue;
                }

                rows.Add(new TraceTerms(traceId, startTime, SearchTerms.ForSpans(list)));

                foreach (var span in list)
                {
                    keys.UnionWith(SearchTerms.Fields(null, null, span.AttributesJson, span.EventsJson).Select(f => f.Key));
                }
            }

            await WriteBackfillTermsAsync(connection, rows, ct).ConfigureAwait(false);
        }

        await SqlAttributeKeys.RecordAsync(dialect, options.ConnectionString, options.Schema, AttributeScope.Traces, keys, ct).ConfigureAwait(false);

        var last = traces[^1];
        await WriteSettingAsync(PowerTracesSchema.SearchBackfillSettingKey,
            JsonSerializer.Serialize(new BackfillCursor(last.StartTime, last.TraceId), SettingsJson), ct).ConfigureAwait(false);
        return traces.Count;
    }

    /// <summary>Adds backfilled traces' terms to the search index — by default rows of the
    /// <see cref="PowerTracesSchema.SearchTermsTable"/> table, in one transaction.</summary>
    protected virtual async Task WriteBackfillTermsAsync(QueryConnection connection, IReadOnlyList<TraceTerms> traces, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = traces.SelectMany(t => t.Terms.Select(term => (Term: term, t.StartTime, t.TraceId)));
        foreach (var chunk in rows.Chunk(TermsPerInsert))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var values = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                values[i] = $"(@Term{i}, @Start{i}, @Trace{i})";
                dialect.AddParameter(insert, $"@Term{i}", chunk[i].Term);
                dialect.AddParameter(insert, $"@Start{i}", chunk[i].StartTime);
                dialect.AddParameter(insert, $"@Trace{i}", dialect.SearchTraceIdValue(chunk[i].TraceId), ansi: true);
            }

            insert.CommandText = dialect.Sql($"""{dialect.InsertIgnoreInto} {SearchTermsTable} ("Term", "StartTime", "TraceId") VALUES {string.Join(", ", values)}""");
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task DeleteSettingAsync(string key, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""DELETE FROM {Settings} WHERE "Key" = @Key""");
        dialect.AddParameter(cmd, "@Key", key);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
