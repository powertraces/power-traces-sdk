using Npgsql;
using NpgsqlTypes;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;
using PowerTraces.Storage.Sql;

namespace PowerTraces.PostgreSQL;

/// <summary>Owns the connection lifecycle for writing a batch of spans/logs to PostgreSQL: makes
/// sure the schema exists (once, lazily, lock-guarded), resolves the batch's Resource row, then
/// writes — in one transaction per batch — the rows themselves via binary COPY (the fastest bulk
/// path PostgreSQL has) and the per-trace summary rows the trace list
/// reads via one array-parameter upsert. One instance is shared by both exporters for a given
/// options instance so schema init and the resource cache are shared too.</summary>
public sealed class PostgreSqlStorageWriter(PowerTracesExporterOptions options) : IStorageWriter, IIgnoreRuleSource
{
    private const int MaxDeadlockRetries = 3;

    // A trace's tsvector stops growing past this many terms (a tsvector must stay under 1 MB).
    private const int MaxSearchTerms = 20_000;

    private readonly PostgreSqlResourceWriter _resources = new(options.Schema);

    private string Table(string table) => PostgreSqlIdentifiers.Qualify(options.Schema, table);

    // PostgreSqlSchemaInitializer guards its own idempotency (shared with
    // PostgreSqlDashboardStore for the same database) — nothing to duplicate here.
    public Task EnsureSchemaAsync(CancellationToken ct) =>
        PostgreSqlSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    public async Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await SqlIgnoreRules.ListAsync(PostgreSqlDialect.Instance, options.ConnectionString, options.Schema, ct).ConfigureAwait(false);
    }

    public async Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await SqlIgnoreRules.DeleteTracesAsync(PostgreSqlDialect.Instance, options.ConnectionString, options.Schema, traceIds, ct).ConfigureAwait(false);
    }

    public async Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        var summaries = TraceSummary.FromBatch(rows, resource);
        var terms = SearchTerms.FromBatch(rows).ToDictionary(t => t.TraceId, t => t.Terms, StringComparer.Ordinal);
        await InTransactionAsync(resource, async (connection, resourceId) =>
        {
            await CopySpansAsync(connection, rows, resourceId, ct).ConfigureAwait(false);
            await UpsertTraceSummariesAsync(connection, summaries, terms, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(PostgreSqlDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    public async Task WriteLogsAsync(IReadOnlyList<LogRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await InTransactionAsync(resource, async (connection, resourceId) =>
        {
            await CopyLogsAsync(connection, rows, resourceId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(PostgreSqlDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one batch's writes in a transaction, retrying the whole batch when PostgreSQL
    /// aborts it as a deadlock victim (concurrent writers upserting the same trace summaries).</summary>
    private async Task InTransactionAsync(ResourceInfo resource, Func<NpgsqlConnection, Guid, Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(options.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                var resourceId = await _resources.GetOrCreateAsync(connection, transaction, resource, ct).ConfigureAwait(false);
                await work(connection, resourceId).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _resources.Remember(resource, resourceId);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < MaxDeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(20, 100) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CopySpansAsync(NpgsqlConnection connection, IReadOnlyList<SpanRow> rows, Guid resourceId, CancellationToken ct)
    {
        await using var copy = await connection.BeginBinaryImportAsync($"""
            COPY {Table(PowerTracesSchema.SpansTable)} ("Id", "TraceId", "SpanId", "ParentSpanId", "ResourceId", "Name", "Kind", "StartTime", "EndTime",
                "Status", "StatusMessage", "HasError", "HttpStatus", "AttributesJson", "ScopeLibraryName", "ScopeLibraryVersion", "EventsJson", "LinksJson")
            FROM STDIN (FORMAT BINARY)
            """, ct).ConfigureAwait(false);
        copy.Timeout = TimeSpan.FromSeconds(options.CommandTimeoutSeconds);

        foreach (var r in rows)
        {
            await copy.StartRowAsync(ct).ConfigureAwait(false);
            await copy.WriteAsync(r.Id, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.TraceId, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.SpanId, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.ParentSpanId, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(resourceId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.Name, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.Kind, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(AsUtc(r.StartTime), NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, AsUtc(r.EndTime), NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.Status, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.StatusMessage, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.HasError, NpgsqlDbType.Boolean, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.HttpStatus, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(StoredJson.Pack(r.AttributesJson), NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.ScopeLibraryName, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.ScopeLibraryVersion, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(StoredJson.Pack(r.EventsJson), NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.LinksJson, NpgsqlDbType.Text, ct).ConfigureAwait(false);
        }

        await copy.CompleteAsync(ct).ConfigureAwait(false);
    }

    private async Task CopyLogsAsync(NpgsqlConnection connection, IReadOnlyList<LogRow> rows, Guid resourceId, CancellationToken ct)
    {
        await using var copy = await connection.BeginBinaryImportAsync($"""
            COPY {Table(PowerTracesSchema.LogsTable)} ("ResourceId", "Timestamp", "ObservedTimestamp", "TraceId", "SpanId", "SeverityNumber",
                "SeverityText", "Body", "AttributesJson", "ScopeLibraryName", "ScopeLibraryVersion", "SchemaUrl")
            FROM STDIN (FORMAT BINARY)
            """, ct).ConfigureAwait(false);
        copy.Timeout = TimeSpan.FromSeconds(options.CommandTimeoutSeconds);

        foreach (var r in rows)
        {
            await copy.StartRowAsync(ct).ConfigureAwait(false);
            await copy.WriteAsync(resourceId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            await copy.WriteAsync(AsUtc(r.Timestamp), NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, AsUtc(r.ObservedTimestamp), NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.TraceId, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.SpanId, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.SeverityNumber, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.SeverityText, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.Body, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await copy.WriteAsync(r.AttributesJson, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.ScopeLibraryName, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.ScopeLibraryVersion, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(copy, r.SchemaUrl, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
        }

        await copy.CompleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Merges each trace's contribution from this batch into its summary row, as one
    /// statement over array parameters. In ON CONFLICT DO UPDATE, <c>t.</c> is the row as stored
    /// and <c>EXCLUDED.</c> the incoming values — every SET expression sees the pre-update row,
    /// which is what the root-replacement condition needs. The batch's search terms are merged into
    /// the trace's tsvector (a union: lexemes are distinct), up to <see cref="MaxSearchTerms"/>.</summary>
    private async Task UpsertTraceSummariesAsync(
        NpgsqlConnection connection, IReadOnlyList<TraceSummary> summaries, IReadOnlyDictionary<string, IReadOnlyCollection<string>> terms, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            INSERT INTO {Table(PowerTracesSchema.TracesTable)} AS t
                ("TraceId", "StartTime", "EndTime", "HasError", "SpanCount", "HasRoot", "RootName", "ServiceName", "RootAttributesJson", "SearchTerms")
            SELECT u.id, u.st, u.et, u.err, u.cnt, u.root, u.name, u.svc, u.attrs, array_to_tsvector(string_to_array(u.terms, E'\x1F'))
            FROM unnest(@TraceIds, @StartTimes, @EndTimes, @HasErrors, @SpanCounts, @HasRoots, @RootNames, @ServiceNames, @RootAttributes, @Terms)
                AS u(id, st, et, err, cnt, root, name, svc, attrs, terms)
            ON CONFLICT ("TraceId") DO UPDATE SET
                "SearchTerms" = CASE WHEN length(t."SearchTerms") < {MaxSearchTerms} THEN t."SearchTerms" || EXCLUDED."SearchTerms" ELSE t."SearchTerms" END,
                "RootName" = CASE WHEN EXCLUDED."HasRoot" OR (NOT t."HasRoot" AND EXCLUDED."StartTime" < t."StartTime") THEN EXCLUDED."RootName" ELSE t."RootName" END,
                "ServiceName" = CASE WHEN EXCLUDED."HasRoot" OR (NOT t."HasRoot" AND EXCLUDED."StartTime" < t."StartTime") THEN EXCLUDED."ServiceName" ELSE t."ServiceName" END,
                "RootAttributesJson" = CASE WHEN EXCLUDED."HasRoot" OR (NOT t."HasRoot" AND EXCLUDED."StartTime" < t."StartTime") THEN EXCLUDED."RootAttributesJson" ELSE t."RootAttributesJson" END,
                "HasRoot" = t."HasRoot" OR EXCLUDED."HasRoot",
                "StartTime" = LEAST(t."StartTime", EXCLUDED."StartTime"),
                "EndTime" = GREATEST(t."EndTime", EXCLUDED."EndTime"),
                "HasError" = t."HasError" OR EXCLUDED."HasError",
                "SpanCount" = t."SpanCount" + EXCLUDED."SpanCount";
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@TraceIds", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = summaries.Select(s => s.TraceId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@StartTimes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = summaries.Select(s => AsUtc(s.StartTime)).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@EndTimes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = summaries.Select(s => AsUtc(s.EndTime)).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@HasErrors", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = summaries.Select(s => s.HasError).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@SpanCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = summaries.Select(s => s.SpanCount).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@HasRoots", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = summaries.Select(s => s.HasRoot).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@RootNames", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = summaries.Select(s => s.RootName).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@ServiceNames", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = summaries.Select(s => s.ServiceName).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@RootAttributes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = summaries.Select(s => StoredJson.Pack(s.RootAttributesJson)).ToArray() });
        // One string per trace (terms never contain control characters), since a parameter can't
        // be an array of arrays of different lengths.
        cmd.Parameters.Add(new NpgsqlParameter("@Terms", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = summaries.Select(s => string.Join('\u001F', terms.TryGetValue(s.TraceId, out var t) ? t : [])).ToArray(),
        });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken ct) where T : class =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, type, ct);

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken ct) where T : struct =>
        value is { } v ? copy.WriteAsync(v, type, ct) : copy.WriteNullAsync(ct);

    // Npgsql (6+) rejects DateTime with Kind=Unspecified/Local for a `timestamptz` column — the
    // rows the exporters build come from OTel APIs that are already UTC (Activity.StartTimeUtc,
    // LogRecord.Timestamp), but stamping the Kind explicitly here is cheap insurance against that
    // ever changing upstream.
    private static DateTime AsUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? dt) => dt is { } value ? AsUtc(value) : null;
}
