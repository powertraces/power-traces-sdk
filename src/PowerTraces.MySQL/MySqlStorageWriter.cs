using MySqlConnector;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MySQL;

/// <summary>Owns the connection lifecycle for writing a batch of spans/logs to MySQL/MariaDB: makes
/// sure the schema exists (once, lazily, lock-guarded), resolves the batch's Resource row, then
/// writes — in one transaction per batch — the rows themselves and the per-trace summary rows the trace list reads (INSERT ... ON DUPLICATE KEY UPDATE). Rows go in
/// as multi-row INSERTs (MySqlBulkCopy would need the server and connection string to allow LOCAL
/// INFILE). One instance is shared by both exporters for a given options instance so schema init
/// and the resource cache are shared too.</summary>
public sealed class MySqlStorageWriter(PowerTracesExporterOptions options) : IStorageWriter, IIgnoreRuleSource
{
    // Keep well under MySQL's default max_allowed_packet and a sane number of bound parameters
    // per statement.
    private const int SpansPerInsert = 100;
    private const int LogsPerInsert = 200;
    private const int TracesPerInsert = 200;
    private const int MaxDeadlockRetries = 3;

    private readonly MySqlResourceWriter _resources = new(options.Schema);

    private string Table(string table) => MySqlIdentifiers.Qualify(options.Schema, table);

    // MySqlSchemaInitializer guards its own idempotency (shared with MySqlDashboardStore for the
    // same database) — nothing to duplicate here.
    public Task EnsureSchemaAsync(CancellationToken ct) =>
        MySqlSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    public async Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await SqlIgnoreRules.ListAsync(MySqlDialect.Instance, options.ConnectionString, options.Schema, ct).ConfigureAwait(false);
    }

    public async Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await SqlIgnoreRules.DeleteTracesAsync(MySqlDialect.Instance, options.ConnectionString, options.Schema, traceIds, ct).ConfigureAwait(false);
    }

    public async Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        var summaries = TraceSummary.FromBatch(rows, resource);
        var terms = SearchTerms.FromBatch(rows).ToDictionary(t => t.TraceId, t => MySqlSearchText.Join(t.Terms), StringComparer.Ordinal);
        await InTransactionAsync(resource, async (connection, transaction, resourceId) =>
        {
            await InsertSpansAsync(connection, transaction, rows, resourceId, ct).ConfigureAwait(false);
            await UpsertTraceSummariesAsync(connection, transaction, summaries, terms, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(MySqlDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    public async Task WriteLogsAsync(IReadOnlyList<LogRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await InTransactionAsync(resource, async (connection, transaction, resourceId) =>
        {
            await InsertLogsAsync(connection, transaction, rows, resourceId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(MySqlDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one batch's writes in a transaction, retrying the whole batch when InnoDB
    /// picks it as a deadlock victim (concurrent writers upserting the same trace summaries).</summary>
    private async Task InTransactionAsync(ResourceInfo resource, Func<MySqlConnection, MySqlTransaction, Guid, Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new MySqlConnection(options.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                var resourceId = await _resources.GetOrCreateAsync(connection, transaction, resource, ct).ConfigureAwait(false);
                await work(connection, transaction, resourceId).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _resources.Remember(resource, resourceId);
                return;
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < MaxDeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(20, 100) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task InsertSpansAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<SpanRow> rows, Guid resourceId, CancellationToken ct)
    {
        foreach (var chunk in rows.Chunk(SpansPerInsert))
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ResourceId", resourceId.ToString());

            var values = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                var r = chunk[i];
                values[i] = $"(@Id{i}, @TraceId{i}, @SpanId{i}, @ParentSpanId{i}, @ResourceId, @Name{i}, @Kind{i}, @StartTime{i}, @EndTime{i}, @Status{i}, @StatusMessage{i}, @HasError{i}, @HttpStatus{i}, @AttributesJson{i}, @ScopeLibraryName{i}, @ScopeLibraryVersion{i}, @EventsJson{i}, @LinksJson{i})";

                cmd.Parameters.AddWithValue($"@Id{i}", r.Id.ToString());
                cmd.Parameters.AddWithValue($"@TraceId{i}", r.TraceId);
                cmd.Parameters.AddWithValue($"@SpanId{i}", r.SpanId);
                cmd.Parameters.AddWithValue($"@ParentSpanId{i}", r.ParentSpanId);
                cmd.Parameters.AddWithValue($"@Name{i}", r.Name);
                cmd.Parameters.AddWithValue($"@Kind{i}", Db(r.Kind));
                cmd.Parameters.AddWithValue($"@StartTime{i}", r.StartTime);
                cmd.Parameters.AddWithValue($"@EndTime{i}", Db(r.EndTime));
                cmd.Parameters.AddWithValue($"@Status{i}", Db(r.Status));
                cmd.Parameters.AddWithValue($"@StatusMessage{i}", Db(r.StatusMessage));
                cmd.Parameters.AddWithValue($"@HasError{i}", r.HasError);
                cmd.Parameters.AddWithValue($"@HttpStatus{i}", Db(r.HttpStatus));
                cmd.Parameters.AddWithValue($"@AttributesJson{i}", StoredJson.Pack(r.AttributesJson));
                cmd.Parameters.AddWithValue($"@ScopeLibraryName{i}", Db(r.ScopeLibraryName));
                cmd.Parameters.AddWithValue($"@ScopeLibraryVersion{i}", Db(r.ScopeLibraryVersion));
                cmd.Parameters.AddWithValue($"@EventsJson{i}", StoredJson.Pack(r.EventsJson));
                cmd.Parameters.AddWithValue($"@LinksJson{i}", r.LinksJson);
            }

            cmd.CommandText = $"""
                INSERT INTO {Table(PowerTracesSchema.SpansTable)}
                    (`Id`, `TraceId`, `SpanId`, `ParentSpanId`, `ResourceId`, `Name`, `Kind`, `StartTime`, `EndTime`, `Status`, `StatusMessage`, `HasError`, `HttpStatus`, `AttributesJson`, `ScopeLibraryName`, `ScopeLibraryVersion`, `EventsJson`, `LinksJson`)
                VALUES {string.Join(",", values)};
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Inserts the log rows as multi-row INSERTs.</summary>
    private async Task InsertLogsAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<LogRow> rows, Guid resourceId, CancellationToken ct)
    {
        for (var offset = 0; offset < rows.Count; offset += LogsPerInsert)
        {
            var count = Math.Min(LogsPerInsert, rows.Count - offset);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ResourceId", resourceId.ToString());

            var values = new string[count];
            for (var i = 0; i < count; i++)
            {
                var r = rows[offset + i];
                values[i] = $"(@ResourceId, @Timestamp{i}, @ObservedTimestamp{i}, @TraceId{i}, @SpanId{i}, @SeverityNumber{i}, @SeverityText{i}, @Body{i}, @AttributesJson{i}, @ScopeLibraryName{i}, @ScopeLibraryVersion{i}, @SchemaUrl{i})";

                cmd.Parameters.AddWithValue($"@Timestamp{i}", r.Timestamp);
                cmd.Parameters.AddWithValue($"@ObservedTimestamp{i}", Db(r.ObservedTimestamp));
                cmd.Parameters.AddWithValue($"@TraceId{i}", Db(r.TraceId));
                cmd.Parameters.AddWithValue($"@SpanId{i}", Db(r.SpanId));
                cmd.Parameters.AddWithValue($"@SeverityNumber{i}", r.SeverityNumber);
                cmd.Parameters.AddWithValue($"@SeverityText{i}", Db(r.SeverityText));
                cmd.Parameters.AddWithValue($"@Body{i}", Db(r.Body));
                cmd.Parameters.AddWithValue($"@AttributesJson{i}", r.AttributesJson);
                cmd.Parameters.AddWithValue($"@ScopeLibraryName{i}", Db(r.ScopeLibraryName));
                cmd.Parameters.AddWithValue($"@ScopeLibraryVersion{i}", Db(r.ScopeLibraryVersion));
                cmd.Parameters.AddWithValue($"@SchemaUrl{i}", Db(r.SchemaUrl));
            }

            cmd.CommandText = $"""
                INSERT INTO {Table(PowerTracesSchema.LogsTable)}
                    (`ResourceId`, `Timestamp`, `ObservedTimestamp`, `TraceId`, `SpanId`, `SeverityNumber`, `SeverityText`, `Body`, `AttributesJson`, `ScopeLibraryName`, `ScopeLibraryVersion`, `SchemaUrl`)
                VALUES {string.Join(",", values)};
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Merges each trace's contribution from this batch into its summary row. MySQL
    /// evaluates ON DUPLICATE KEY UPDATE assignments left to right, each seeing the columns already
    /// assigned before it — so the root fields (which compare against the stored HasRoot and
    /// StartTime) are assigned before HasRoot and StartTime themselves change. The batch's search
    /// tokens are appended to the trace's search text (see <see cref="MySqlSearchText"/>).</summary>
    private async Task UpsertTraceSummariesAsync(
        MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<TraceSummary> summaries, IReadOnlyDictionary<string, string> searchText, CancellationToken ct)
    {
        const string replaceRoot = "VALUES(`HasRoot`) OR (NOT `HasRoot` AND VALUES(`StartTime`) < `StartTime`)";

        foreach (var chunk in summaries.Chunk(TracesPerInsert))
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.CommandTimeoutSeconds;

            var values = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                var s = chunk[i];
                values[i] = $"(@TraceId{i}, @StartTime{i}, @EndTime{i}, @HasError{i}, @SpanCount{i}, @HasRoot{i}, @RootName{i}, @ServiceName{i}, @RootAttributesJson{i}, @SearchText{i})";
                cmd.Parameters.AddWithValue($"@SearchText{i}", searchText.GetValueOrDefault(s.TraceId, ""));
                cmd.Parameters.AddWithValue($"@TraceId{i}", s.TraceId);
                cmd.Parameters.AddWithValue($"@StartTime{i}", s.StartTime);
                cmd.Parameters.AddWithValue($"@EndTime{i}", s.EndTime);
                cmd.Parameters.AddWithValue($"@HasError{i}", s.HasError);
                cmd.Parameters.AddWithValue($"@SpanCount{i}", s.SpanCount);
                cmd.Parameters.AddWithValue($"@HasRoot{i}", s.HasRoot);
                cmd.Parameters.AddWithValue($"@RootName{i}", s.RootName);
                cmd.Parameters.AddWithValue($"@ServiceName{i}", Db(s.ServiceName));
                cmd.Parameters.AddWithValue($"@RootAttributesJson{i}", StoredJson.Pack(s.RootAttributesJson));
            }

            cmd.CommandText = $"""
                INSERT INTO {Table(PowerTracesSchema.TracesTable)}
                    (`TraceId`, `StartTime`, `EndTime`, `HasError`, `SpanCount`, `HasRoot`, `RootName`, `ServiceName`, `RootAttributesJson`, `{MySqlSearchText.Column}`)
                VALUES {string.Join(",", values)}
                ON DUPLICATE KEY UPDATE
                    `{MySqlSearchText.Column}` = IF(CHAR_LENGTH(`{MySqlSearchText.Column}`) < {MySqlSearchText.MaxLength}, CONCAT_WS(' ', `{MySqlSearchText.Column}`, VALUES(`{MySqlSearchText.Column}`)), `{MySqlSearchText.Column}`),
                    `RootName` = IF({replaceRoot}, VALUES(`RootName`), `RootName`),
                    `ServiceName` = IF({replaceRoot}, VALUES(`ServiceName`), `ServiceName`),
                    `RootAttributesJson` = IF({replaceRoot}, VALUES(`RootAttributesJson`), `RootAttributesJson`),
                    `HasRoot` = `HasRoot` OR VALUES(`HasRoot`),
                    `StartTime` = LEAST(`StartTime`, VALUES(`StartTime`)),
                    `EndTime` = GREATEST(`EndTime`, VALUES(`EndTime`)),
                    `HasError` = `HasError` OR VALUES(`HasError`),
                    `SpanCount` = `SpanCount` + VALUES(`SpanCount`);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
