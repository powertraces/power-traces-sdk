using System.Data.Common;
using System.Text;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage.Sql;

/// <summary>The dashboard's read side for every SQL provider — one implementation, with the
/// per-database differences confined to <see cref="SqlDialect"/>.
///
/// Every list query is shaped so the database can answer it from an index instead of scanning
/// or parsing JSON: the trace list pages through the <see cref="PowerTracesSchema.TracesTable"/>
/// summary table (one row per trace, maintained on write) by start time; trace search reads the
/// <see cref="PowerTracesSchema.SearchTermsTable"/> index (see SqlDashboardStore.Search.cs); log
/// attribute filters read the rows' attribute JSON (<see cref="SqlDialect.JsonAttributeText"/>),
/// a scan of the rows the other filters leave; only the predicates actually in use are emitted (no
/// <c>@p IS NULL OR ...</c> catch-alls, which defeat index seeks); and totals stop counting at
/// <see cref="PagedResult{T}.CountLimit"/>.</summary>
public abstract partial class SqlDashboardStore(SqlDialect dialect, PowerTracesDashboardOptions options) : IDashboardStore, IIgnoreRuleStore
{
    private const int SummaryListSize = 20;

    private static readonly TimeSpan AttributeKeyTtl = TimeSpan.FromSeconds(30);

    private readonly AttributeKeyCache _traceAttributeKeyCache = new(AttributeKeyTtl);
    private readonly AttributeKeyCache _logAttributeKeyCache = new(AttributeKeyTtl);
    private readonly SemaphoreSlim _queryGate = new(options.MaxConcurrentQueries, options.MaxConcurrentQueries);

    protected SqlDialect Dialect => dialect;

    protected PowerTracesDashboardOptions Options => options;

    private string Spans => dialect.Table(options.Schema, PowerTracesSchema.SpansTable);
    private string Logs => dialect.Table(options.Schema, PowerTracesSchema.LogsTable);
    private string Resources => dialect.Table(options.Schema, PowerTracesSchema.ResourcesTable);
    private string Traces => dialect.Table(options.Schema, PowerTracesSchema.TracesTable);
    private string AttributeKeys => dialect.Table(options.Schema, PowerTracesSchema.AttributeKeysTable);

    public abstract Task EnsureSchemaAsync(CancellationToken ct);

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
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""
            SELECT s."SpanId", s."ParentSpanId", s."Name", r."ServiceName", s."Kind", s."StartTime", s."EndTime",
                   s."Status", s."StatusMessage", s."HasError", s."HttpStatus", s."AttributesJson", s."EventsJson", s."LinksJson"
            FROM {Spans} s LEFT JOIN {Resources} r ON r."Id" = s."ResourceId"
            WHERE s."TraceId" = @TraceId
            """);
        dialect.AddParameter(cmd, "@TraceId", traceId.Trim().ToLowerInvariant(), ansi: true);

        var spans = new List<SpanDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var start = SqlDialect.ReadUtc(reader, 5);
                var end = SqlDialect.ReadUtcOrNull(reader, 6);
                spans.Add(new SpanDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    SqlDialect.ReadStringOrNull(reader, 3),
                    SqlDialect.ReadStringOrNull(reader, 4),
                    start,
                    end,
                    (long)((end ?? start) - start).TotalMilliseconds,
                    SqlDialect.ReadStringOrNull(reader, 7),
                    SqlDialect.ReadStringOrNull(reader, 8),
                    SqlDialect.ReadBool(reader, 9),
                    SqlDialect.ReadStringOrNull(reader, 10),
                    JsonAttributes.Parse(SqlDialect.ReadStringOrNull(reader, 11)),
                    JsonAttributes.ParseList<SpanEventDto>(SqlDialect.ReadStringOrNull(reader, 12)),
                    JsonAttributes.ParseList<SpanLinkDto>(SqlDialect.ReadStringOrNull(reader, 13))));
            }
        }

        return TraceDetails.Build(traceId, spans);
    }

    // ---------------------------------------------------------------- logs

    /// <summary>The original log list query, kept for callers of the interface: it runs as a
    /// <see cref="SearchLogsAsync"/> — <paramref name="search"/> as one text condition,
    /// <paramref name="severity"/> as its level, each attribute filter as an exact (or, with
    /// <c>Contains</c>, substring) field filter.</summary>
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

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var where = new StringBuilder("1 = 1");
        if (!await AppendLogSearchAsync(connection, cmd, where, query, withSeverities: true, withServices: true, ct).ConfigureAwait(false))
        {
            return new PagedResult<LogListItemDto>([], 0, page, pageSize);
        }

        var fromWhere = $"FROM {Logs} l WHERE {where}";
        var (total, capped) = countTotal ? await CountAsync(cmd, fromWhere, ct).ConfigureAwait(false) : (0, false);

        // Page on the Logs table alone, then join Resources for just that page's service names.
        cmd.CommandText = dialect.Sql($"""
            SELECT p."Id", p."Timestamp", p."SeverityNumber", p."SeverityText", p."Body", r."ServiceName", p."TraceId", p."SpanId", p."AttributesJson"
            FROM (
                SELECT l."Id", l."Timestamp", l."SeverityNumber", l."SeverityText", l."Body", l."ResourceId", l."TraceId", l."SpanId", l."AttributesJson"
                {fromWhere}
                ORDER BY l."Timestamp" DESC, l."Id" DESC
                {dialect.Paging}
            ) p
            LEFT JOIN {Resources} r ON r."Id" = p."ResourceId"
            ORDER BY p."Timestamp" DESC, p."Id" DESC
            """);
        dialect.AddParameter(cmd, "@Skip", (page - 1) * pageSize);
        dialect.AddParameter(cmd, "@Take", pageSize);

        var items = new List<LogListItemDto>(pageSize);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new LogListItemDto(
                    SqlDialect.ReadInt64(reader.GetValue(0)),
                    SqlDialect.ReadUtc(reader, 1),
                    (int)SqlDialect.ReadInt64(reader.GetValue(2)),
                    SqlDialect.ReadStringOrNull(reader, 3),
                    SqlDialect.ReadStringOrNull(reader, 4),
                    SqlDialect.ReadStringOrNull(reader, 5),
                    SqlDialect.ReadStringOrNull(reader, 6),
                    SqlDialect.ReadStringOrNull(reader, 7),
                    JsonAttributes.Parse(SqlDialect.ReadStringOrNull(reader, 8))));
            }
        }

        return new PagedResult<LogListItemDto>(items, countTotal ? total : items.Count, page, pageSize, capped);
    }

    /// <summary>Severity and service counts over the newest <see cref="LogFacets.SampleSize"/>
    /// matches — each with the query's own severity (or service) selection left out.</summary>
    public async Task<LogFacetsDto> GetLogFacetsAsync(LogSearchQuery query, CancellationToken ct)
    {
        var sampled = false;
        var levels = new Dictionary<int, int>();
        var services = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = connection.CreateCommand())
        {
            var where = new StringBuilder("1 = 1");
            if (await AppendLogSearchAsync(connection, cmd, where, query, withSeverities: false, withServices: true, ct).ConfigureAwait(false))
            {
                var sample = dialect.SelectLimited("""l."SeverityNumber" """, $"""FROM {Logs} l WHERE {where} ORDER BY l."Timestamp" DESC""", LogFacets.SampleSize);
                cmd.CommandText = dialect.Sql($"""SELECT x."SeverityNumber", COUNT(*) FROM ({sample}) x GROUP BY x."SeverityNumber" """);
                var seen = 0;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var level = LogSeverity.Level((int)SqlDialect.ReadInt64(reader.GetValue(0)));
                    var count = (int)SqlDialect.ReadInt64(reader.GetValue(1));
                    levels[level] = levels.GetValueOrDefault(level) + count;
                    seen += count;
                }

                sampled |= seen >= LogFacets.SampleSize;
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            var where = new StringBuilder("1 = 1");
            if (await AppendLogSearchAsync(connection, cmd, where, query, withSeverities: true, withServices: false, ct).ConfigureAwait(false))
            {
                var sample = dialect.SelectLimited("""l."ResourceId" """, $"""FROM {Logs} l WHERE {where} ORDER BY l."Timestamp" DESC""", LogFacets.SampleSize);
                cmd.CommandText = dialect.Sql($"""
                    SELECT r."ServiceName", COUNT(*) FROM ({sample}) x LEFT JOIN {Resources} r ON r."Id" = x."ResourceId" GROUP BY r."ServiceName"
                    """);
                var seen = 0;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = SqlDialect.ReadStringOrNull(reader, 0) ?? "";
                    var count = (int)SqlDialect.ReadInt64(reader.GetValue(1));
                    services[name] = services.GetValueOrDefault(name) + count;
                    seen += count;
                }

                sampled |= seen >= LogFacets.SampleSize;
            }
        }

        return new LogFacetsDto(
            levels.OrderBy(l => l.Key).Select(l => new SeverityCountDto(l.Key, l.Value)).ToList(),
            services.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(LogFacets.MaxServices).Select(s => new FacetValueDto(s.Key, s.Value)).ToList(),
            sampled);
    }

    /// <summary>Appends the query's predicates on the log row <c>l</c>, optionally without its
    /// severity or service part (for facets). Returns false when nothing can match (only services
    /// that have never reported).</summary>
    private async Task<bool> AppendLogSearchAsync(
        QueryConnection connection, DbCommand cmd, StringBuilder where, LogSearchQuery query, bool withSeverities, bool withServices, CancellationToken ct)
    {
        if (query.From is { } f)
        {
            where.Append(""" AND l."Timestamp" >= @From""");
            dialect.AddParameter(cmd, "@From", f);
        }

        if (query.To is { } t)
        {
            where.Append(""" AND l."Timestamp" <= @To""");
            dialect.AddParameter(cmd, "@To", t);
        }

        if (withSeverities && LogSeverity.Levels(query.Severities) is { } levels)
        {
            var ranges = new List<string>(levels.Count);
            for (var i = 0; i < levels.Count; i++)
            {
                var (low, high) = LogSeverity.Range(levels[i]);
                ranges.Add($"""(l."SeverityNumber" >= @SevLow{i} AND l."SeverityNumber" <= @SevHigh{i})""");
                dialect.AddParameter(cmd, $"@SevLow{i}", low);
                dialect.AddParameter(cmd, $"@SevHigh{i}", high);
            }

            where.Append(" AND (").Append(string.Join(" OR ", ranges)).Append(')');
        }

        if (withServices && query.Services is { Count: > 0 } serviceNames)
        {
            var resourceIds = await AppendResourceIdsAsync(connection, cmd, serviceNames, ct).ConfigureAwait(false);
            if (resourceIds is null)
            {
                return false;
            }

            where.Append($""" AND l."ResourceId" IN ({resourceIds})""");
        }

        var n = 0;
        foreach (var term in LogTextTerm.Parse(query.Text))
        {
            var param = $"@Text{n}";
            var bodyMatch = dialect.ContainsPredicate("""l."Body" """, param);
            dialect.AddParameter(cmd, param, dialect.EscapeLike(term.Text));
            if (term.Exclude)
            {
                where.Append($""" AND (l."Body" IS NULL OR NOT ({bodyMatch}))""");
            }
            else if (term.IsTraceId)
            {
                where.Append($""" AND (l."TraceId" = @TextId{n} OR {bodyMatch})""");
                dialect.AddParameter(cmd, $"@TextId{n}", term.Text.ToLowerInvariant(), ansi: true);
            }
            else
            {
                where.Append(" AND ").Append(bodyMatch);
            }

            n++;
        }

        AppendFieldFilters(cmd, where, query.Filters, json => json("""l."AttributesJson" """));
        return true;
    }

    public async Task<LogDetailDto?> GetLogAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""
            SELECT l."Id", l."Timestamp", l."ObservedTimestamp", l."SeverityNumber", l."SeverityText", l."Body",
                   r."ServiceName", l."TraceId", l."SpanId", l."ScopeLibraryName", l."ScopeLibraryVersion", l."SchemaUrl", l."AttributesJson"
            FROM {Logs} l LEFT JOIN {Resources} r ON r."Id" = l."ResourceId"
            WHERE l."Id" = @Id
            """);
        dialect.AddParameter(cmd, "@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LogDetailDto(
            SqlDialect.ReadInt64(reader.GetValue(0)),
            SqlDialect.ReadUtc(reader, 1),
            SqlDialect.ReadUtcOrNull(reader, 2),
            (int)SqlDialect.ReadInt64(reader.GetValue(3)),
            SqlDialect.ReadStringOrNull(reader, 4),
            SqlDialect.ReadStringOrNull(reader, 5),
            SqlDialect.ReadStringOrNull(reader, 6),
            SqlDialect.ReadStringOrNull(reader, 7),
            SqlDialect.ReadStringOrNull(reader, 8),
            SqlDialect.ReadStringOrNull(reader, 9),
            SqlDialect.ReadStringOrNull(reader, 10),
            SqlDialect.ReadStringOrNull(reader, 11),
            JsonAttributes.Parse(SqlDialect.ReadStringOrNull(reader, 12)));
    }

    // ---------------------------------------------------------------- summary / lookups

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);

        long logsLast24h;
        long tracesLast24h;
        long errorTracesLast24h;
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = dialect.Sql($"""SELECT COUNT(*) FROM {Logs} WHERE "Timestamp" >= @Since""");
            dialect.AddParameter(cmd, "@Since", since);
            logsLast24h = SqlDialect.ReadInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            cmd.CommandText = dialect.Sql($"""
                SELECT COUNT(*), SUM(CASE WHEN "HasError" = @True THEN 1 ELSE 0 END)
                FROM {Traces} WHERE "StartTime" >= @Since
                """);
            dialect.AddParameter(cmd, "@True", true);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            tracesLast24h = SqlDialect.ReadInt64(reader.GetValue(0));
            errorTracesLast24h = SqlDialect.ReadInt64(reader.GetValue(1));
        }

        var errorRate = tracesLast24h == 0 ? 0 : Math.Round(100.0 * errorTracesLast24h / tracesLast24h, 1);

        var recentLogs = await SearchLogsCoreAsync(new LogSearchQuery { PageSize = SummaryListSize }, countTotal: false, ct).ConfigureAwait(false);
        var recentTraces = await SearchTracesCoreAsync(new TraceSearchQuery { PageSize = SummaryListSize }, countTotal: false, ct).ConfigureAwait(false);

        return new DashboardSummaryDto(
            (int)logsLast24h, (int)tracesLast24h, (int)errorTracesLast24h, errorRate,
            recentLogs.Items, recentTraces.Items);
    }

    /// <summary>Three grouped scans over the window, each through a time index: trace summaries
    /// per bucket, logs per bucket, and trace summaries per root operation.</summary>
    public async Task<DashboardOverviewDto> GetOverviewAsync(DateTime from, DateTime to, int bucketCount, CancellationToken ct)
    {
        var bucketSeconds = DashboardOverview.BucketSeconds(from, to, bucketCount);
        var duration = dialect.MillisecondsBetween("\"StartTime\"", "\"EndTime\"");

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        dialect.AddParameter(cmd, "@From", from);
        dialect.AddParameter(cmd, "@To", to);
        dialect.AddParameter(cmd, "@BucketSeconds", bucketSeconds);
        dialect.AddParameter(cmd, "@True", true);

        var traceBucket = dialect.BucketIndex("\"StartTime\"");
        cmd.CommandText = dialect.Sql($"""
            SELECT {traceBucket}, COUNT(*), SUM(CASE WHEN "HasError" = @True THEN 1 ELSE 0 END), AVG({duration})
            FROM {Traces} WHERE "StartTime" >= @From AND "StartTime" < @To
            GROUP BY {traceBucket}
            """);
        var traceRows = new List<(long, int, int, double)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                traceRows.Add((SqlDialect.ReadInt64(reader.GetValue(0)), (int)SqlDialect.ReadInt64(reader.GetValue(1)),
                    (int)SqlDialect.ReadInt64(reader.GetValue(2)), reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3))));
            }
        }

        var logBucket = dialect.BucketIndex("\"Timestamp\"");
        cmd.CommandText = dialect.Sql($"""
            SELECT {logBucket}, COUNT(*),
                   SUM(CASE WHEN "SeverityNumber" >= 13 AND "SeverityNumber" < 17 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN "SeverityNumber" >= 17 THEN 1 ELSE 0 END)
            FROM {Logs} WHERE "Timestamp" >= @From AND "Timestamp" < @To
            GROUP BY {logBucket}
            """);
        var logRows = new List<(long, int, int, int)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                logRows.Add((SqlDialect.ReadInt64(reader.GetValue(0)), (int)SqlDialect.ReadInt64(reader.GetValue(1)),
                    (int)SqlDialect.ReadInt64(reader.GetValue(2)), (int)SqlDialect.ReadInt64(reader.GetValue(3))));
            }
        }

        cmd.CommandText = dialect.Sql(dialect.SelectLimited(
            $"""
            "RootName", "ServiceName", COUNT(*), SUM(CASE WHEN "HasError" = @True THEN 1 ELSE 0 END), AVG({duration}), MAX({duration})
            """,
            $"""
            FROM {Traces} WHERE "StartTime" >= @From AND "StartTime" < @To
            GROUP BY "RootName", "ServiceName"
            ORDER BY COUNT(*) DESC
            """,
            DashboardOverview.TopOperationCount));
        var operations = new List<OperationStatsDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                operations.Add(new OperationStatsDto(
                    SqlDialect.ReadStringOrNull(reader, 0) ?? "(unnamed)",
                    SqlDialect.ReadStringOrNull(reader, 1),
                    (int)SqlDialect.ReadInt64(reader.GetValue(2)),
                    (int)SqlDialect.ReadInt64(reader.GetValue(3)),
                    Math.Round(reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)), 1),
                    reader.IsDBNull(5) ? 0 : (long)Convert.ToDouble(reader.GetValue(5))));
            }
        }

        return DashboardOverview.Assemble(from, to, bucketSeconds, traceRows, logRows, operations);
    }

    public async Task<IReadOnlyList<string>> ListServiceNamesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""
            SELECT DISTINCT "ServiceName" FROM {Resources} WHERE "ServiceName" IS NOT NULL ORDER BY "ServiceName"
            """);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Every attribute key recorded for the scope (see <see cref="SqlAttributeKeys"/>),
    /// cached briefly.</summary>
    public Task<IReadOnlyList<string>> ListAttributeKeysAsync(AttributeScope scope, CancellationToken ct)
    {
        var cache = scope == AttributeScope.Traces ? _traceAttributeKeyCache : _logAttributeKeyCache;
        return cache.GetOrRefreshAsync(async refreshCt =>
        {
            await using var connection = await OpenAsync(refreshCt).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = dialect.Sql($"""SELECT "Key" FROM {AttributeKeys} WHERE "Source" = @Source ORDER BY "Key" """);
            dialect.AddParameter(cmd, "@Source", SqlAttributeKeys.SourceOf(scope));

            var keys = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(refreshCt).ConfigureAwait(false);
            while (await reader.ReadAsync(refreshCt).ConfigureAwait(false))
            {
                keys.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)keys;
        }, ct);
    }

    // ---------------------------------------------------------------- ignore rules

    public Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct) =>
        SqlIgnoreRules.ListAsync(dialect, options.ConnectionString, options.Schema, ct);

    public async Task AddIgnoreRuleAsync(IgnoreRuleKind kind, string name, CancellationToken ct)
    {
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.CreateCommand())
        {
            var rules = dialect.Table(options.Schema, PowerTracesSchema.IgnoreRulesTable);
            cmd.CommandText = dialect.Sql($"""
                INSERT INTO {rules} ("Id", "Kind", "Name", "CreatedAt")
                SELECT @Id, @Kind, @Name, @CreatedAt{dialect.FromDual}
                WHERE NOT EXISTS (SELECT 1 FROM {rules} WHERE "Kind" = @Kind AND "Name" = @Name)
                """);
            dialect.AddParameter(cmd, "@Id", Guid.NewGuid());
            dialect.AddParameter(cmd, "@Kind", kind.ToString().ToLowerInvariant());
            dialect.AddParameter(cmd, "@Name", name);
            dialect.AddParameter(cmd, "@CreatedAt", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        SqlIgnoreRules.Invalidate(dialect, options.ConnectionString, options.Schema);
    }

    public async Task DeleteIgnoreRuleAsync(Guid id, CancellationToken ct)
    {
        await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = dialect.Sql($"""DELETE FROM {dialect.Table(options.Schema, PowerTracesSchema.IgnoreRulesTable)} WHERE "Id" = @Id""");
            dialect.AddParameter(cmd, "@Id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        SqlIgnoreRules.Invalidate(dialect, options.ConnectionString, options.Schema);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Takes one of the store's <see cref="PowerTracesDashboardOptions.MaxConcurrentQueries"/>
    /// slots (waiting, cancellably and without a connection, if all are busy), then opens a
    /// connection; disposing the result closes the connection and frees the slot.</summary>
    protected async Task<QueryConnection> OpenAsync(CancellationToken ct)
    {
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        var connection = dialect.CreateConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return new QueryConnection(connection, _queryGate);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _queryGate.Release();
            throw;
        }
    }

    /// <summary>An open connection holding one of the store's query slots.</summary>
    protected sealed class QueryConnection(DbConnection connection, SemaphoreSlim gate) : IAsyncDisposable
    {
        public DbCommand CreateCommand() => connection.CreateCommand();

        public ValueTask<DbTransaction> BeginTransactionAsync(CancellationToken ct) => connection.BeginTransactionAsync(ct);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Counts matches up to <see cref="PagedResult{T}.CountLimit"/> (one past it, to know
    /// whether the limit was hit) using the parameters already bound on <paramref name="cmd"/>.</summary>
    private async Task<(int Total, bool Capped)> CountAsync(DbCommand cmd, string fromWhere, CancellationToken ct)
    {
        var limit = PagedResult<object>.CountLimit;
        cmd.CommandText = dialect.Sql($"SELECT COUNT(*) FROM ({dialect.SelectLimited("1 AS x", fromWhere, limit + 1)}) c");
        var count = SqlDialect.ReadInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return count > limit ? (limit, true) : ((int)count, false);
    }

    /// <summary>One predicate per filter on the log's attribute JSON — equality on the attribute's
    /// text, "absent or different" for not-equals, or a case-insensitive substring match for
    /// has-word. <paramref name="wrap"/> places the predicate (given a function of the JSON
    /// column) in its query.</summary>
    private void AppendFieldFilters(
        DbCommand cmd, StringBuilder where, IReadOnlyList<FieldFilter>? filters, Func<Func<string, string>, string> wrap)
    {
        if (filters is null)
        {
            return;
        }

        var i = 0;
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Key) || filter.Value is null)
            {
                continue;
            }

            var keyParam = $"@AttrKey{i}";
            var valueParam = $"@AttrValue{i}";
            where.Append(" AND ").Append(wrap(column =>
            {
                var text = dialect.JsonAttributeText(column, keyParam);
                return filter.Match switch
                {
                    FieldMatch.HasWord => dialect.JsonContainsPredicate(text, valueParam),
                    FieldMatch.NotEquals => $"({text} IS NULL OR {text} <> {valueParam})",
                    _ => $"{text} = {valueParam}",
                };
            }));
            dialect.AddParameter(cmd, keyParam, dialect.JsonAttributeKey(filter.Key.Trim()));
            dialect.AddParameter(cmd, valueParam, filter.Match == FieldMatch.HasWord ? dialect.EscapeLike(filter.Value) : filter.Value);
            i++;
        }
    }
}
