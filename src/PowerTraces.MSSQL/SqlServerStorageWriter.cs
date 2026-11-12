using System.Data;
using Microsoft.Data.SqlClient;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MSSQL;

/// <summary>Owns the connection lifecycle for writing a batch of spans/logs to SQL Server: makes
/// sure the schema exists (once, lazily, lock-guarded), resolves the batch's Resource row, then
/// writes — in one transaction per batch — the rows themselves (via SqlBulkCopy) and the per-trace
/// summary rows the trace list reads (MERGE upsert). One instance is shared by both exporters for a given options instance so
/// schema init and the resource cache are shared too.</summary>
public sealed class SqlServerStorageWriter(PowerTracesExporterOptions options) : IStorageWriter, IIgnoreRuleSource
{
    // SQL Server caps a single command at 2100 parameters.
    private const int TracesPerStatement = 200; // 9 parameters per trace summary
    private const int MaxDeadlockRetries = 3;

    private readonly SqlServerResourceWriter _resources = new(options.Schema);

    private string Table(string table) => PowerTracesSchema.Qualify(options.Schema, table);

    // SqlServerSchemaInitializer guards its own idempotency (shared with SqlServerDashboardStore
    // for the same database) — nothing to duplicate here.
    public Task EnsureSchemaAsync(CancellationToken ct) =>
        SqlServerSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    public async Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await SqlIgnoreRules.ListAsync(SqlServerDialect.Instance, options.ConnectionString, options.Schema, ct).ConfigureAwait(false);
    }

    public async Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await SqlIgnoreRules.DeleteTracesAsync(SqlServerDialect.Instance, options.ConnectionString, options.Schema, traceIds, ct).ConfigureAwait(false);
    }

    public async Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        var summaries = TraceSummary.FromBatch(rows, resource);
        var terms = SearchTerms.FromBatch(rows);
        await InTransactionAsync(resource, async (connection, transaction, resourceId) =>
        {
            await BulkCopyAsync(connection, transaction, PowerTracesSchema.SpansTable, BuildSpansTable(rows, resourceId), ct).ConfigureAwait(false);
            await BulkCopyAsync(connection, transaction, PowerTracesSchema.SearchTermsTable, BuildSearchTermsTable(terms), ct).ConfigureAwait(false);
            await UpsertTraceSummariesAsync(connection, transaction, summaries, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(SqlServerDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
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
            await BulkCopyAsync(connection, transaction, PowerTracesSchema.LogsTable, BuildLogsTable(rows, resourceId), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(SqlServerDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one batch's writes in a transaction, retrying the whole batch when SQL Server
    /// picks it as a deadlock victim — concurrent exporters (several app instances, or the trace
    /// and log exporters of one) upserting the same trace summaries can deadlock, and a retry of a
    /// fresh transaction is the standard, safe response.</summary>
    private async Task InTransactionAsync(ResourceInfo resource, Func<SqlConnection, SqlTransaction, Guid, Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = SqlServerConnections.Create(options.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                var resourceId = await _resources.GetOrCreateAsync(connection, transaction, resource, ct).ConfigureAwait(false);
                await work(connection, transaction, resourceId).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _resources.Remember(resource, resourceId);
                return;
            }
            catch (SqlException ex) when (ex.Number == 1205 && attempt < MaxDeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(20, 100) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task BulkCopyAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable data, CancellationToken ct)
    {
        if (data.Rows.Count == 0)
        {
            return;
        }

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = Table(table),
            BulkCopyTimeout = options.CommandTimeoutSeconds,
            BatchSize = 0,
        };

        foreach (DataColumn column in data.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(data, ct).ConfigureAwait(false);
    }

    private static DataTable BuildSpansTable(IReadOnlyList<SpanRow> rows, Guid resourceId)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("TraceId", typeof(string));
        table.Columns.Add("SpanId", typeof(string));
        table.Columns.Add("ParentSpanId", typeof(string));
        table.Columns.Add("ResourceId", typeof(Guid));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Kind", typeof(string));
        table.Columns.Add("StartTime", typeof(DateTime));
        table.Columns.Add("EndTime", typeof(DateTime));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("StatusMessage", typeof(string));
        table.Columns.Add("HasError", typeof(bool));
        table.Columns.Add("HttpStatus", typeof(string));
        table.Columns.Add("AttributesJson", typeof(string));
        table.Columns.Add("ScopeLibraryName", typeof(string));
        table.Columns.Add("ScopeLibraryVersion", typeof(string));
        table.Columns.Add("EventsJson", typeof(string));
        table.Columns.Add("LinksJson", typeof(string));

        foreach (var r in rows)
        {
            table.Rows.Add(
                r.Id, r.TraceId, r.SpanId, r.ParentSpanId, resourceId, r.Name, Db(r.Kind), r.StartTime, Db(r.EndTime),
                Db(r.Status), Db(r.StatusMessage), r.HasError, Db(r.HttpStatus), StoredJson.Pack(r.AttributesJson),
                Db(r.ScopeLibraryName), Db(r.ScopeLibraryVersion), StoredJson.Pack(r.EventsJson), r.LinksJson);
        }

        return table;
    }

    private static DataTable BuildSearchTermsTable(IReadOnlyList<TraceTerms> terms)
    {
        var table = new DataTable();
        table.Columns.Add("Term", typeof(string));
        table.Columns.Add("StartTime", typeof(DateTime));
        table.Columns.Add("TraceId", typeof(byte[]));

        foreach (var trace in terms)
        {
            var traceId = Convert.FromHexString(trace.TraceId);
            foreach (var term in trace.Terms)
            {
                table.Rows.Add(term, trace.StartTime, traceId);
            }
        }

        return table;
    }

    private static DataTable BuildLogsTable(IReadOnlyList<LogRow> rows, Guid resourceId)
    {
        var table = new DataTable();
        table.Columns.Add("ResourceId", typeof(Guid));
        table.Columns.Add("Timestamp", typeof(DateTime));
        table.Columns.Add("ObservedTimestamp", typeof(DateTime));
        table.Columns.Add("TraceId", typeof(string));
        table.Columns.Add("SpanId", typeof(string));
        table.Columns.Add("SeverityNumber", typeof(int));
        table.Columns.Add("SeverityText", typeof(string));
        table.Columns.Add("Body", typeof(string));
        table.Columns.Add("AttributesJson", typeof(string));
        table.Columns.Add("ScopeLibraryName", typeof(string));
        table.Columns.Add("ScopeLibraryVersion", typeof(string));
        table.Columns.Add("SchemaUrl", typeof(string));

        foreach (var r in rows)
        {
            table.Rows.Add(
                resourceId, r.Timestamp, Db(r.ObservedTimestamp), Db(r.TraceId), Db(r.SpanId), r.SeverityNumber, Db(r.SeverityText),
                Db(r.Body), r.AttributesJson, Db(r.ScopeLibraryName), Db(r.ScopeLibraryVersion), Db(r.SchemaUrl));
        }

        return table;
    }

    /// <summary>Merges each trace's contribution from this batch into its summary row. HOLDLOCK
    /// makes the "does the row exist yet?" check and the insert atomic, so two writers seeing the
    /// same new trace can't both insert it. In the UPDATE branch every <c>t.</c> column is the row's
    /// value before the update, which is what the root-replacement condition needs.</summary>
    private async Task UpsertTraceSummariesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<TraceSummary> summaries, CancellationToken ct)
    {
        for (var offset = 0; offset < summaries.Count; offset += TracesPerStatement)
        {
            var count = Math.Min(TracesPerStatement, summaries.Count - offset);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.CommandTimeoutSeconds;

            var values = new string[count];
            for (var i = 0; i < count; i++)
            {
                var s = summaries[offset + i];
                values[i] = $"(@TraceId{i}, @StartTime{i}, @EndTime{i}, @HasError{i}, @SpanCount{i}, @HasRoot{i}, @RootName{i}, @ServiceName{i}, @RootAttributesJson{i})";
                cmd.Parameters.Add(new SqlParameter($"@TraceId{i}", SqlDbType.VarChar, 32) { Value = s.TraceId });
                cmd.Parameters.Add(new SqlParameter($"@StartTime{i}", SqlDbType.DateTime2) { Value = s.StartTime });
                cmd.Parameters.Add(new SqlParameter($"@EndTime{i}", SqlDbType.DateTime2) { Value = s.EndTime });
                cmd.Parameters.Add(new SqlParameter($"@HasError{i}", SqlDbType.Bit) { Value = s.HasError });
                cmd.Parameters.Add(new SqlParameter($"@SpanCount{i}", SqlDbType.Int) { Value = s.SpanCount });
                cmd.Parameters.Add(new SqlParameter($"@HasRoot{i}", SqlDbType.Bit) { Value = s.HasRoot });
                cmd.Parameters.Add(new SqlParameter($"@RootName{i}", SqlDbType.NVarChar, 400) { Value = s.RootName });
                cmd.Parameters.Add(new SqlParameter($"@ServiceName{i}", SqlDbType.NVarChar, 200) { Value = Db(s.ServiceName) });
                cmd.Parameters.Add(new SqlParameter($"@RootAttributesJson{i}", SqlDbType.NVarChar, -1) { Value = StoredJson.Pack(s.RootAttributesJson) });
            }

            cmd.CommandText = $"""
                MERGE INTO {Table(PowerTracesSchema.TracesTable)} WITH (HOLDLOCK) AS t
                USING (VALUES {string.Join(",", values)})
                    AS s (TraceId, StartTime, EndTime, HasError, SpanCount, HasRoot, RootName, ServiceName, RootAttributesJson)
                ON t.TraceId = s.TraceId
                WHEN MATCHED THEN UPDATE SET
                    RootName = CASE WHEN s.HasRoot = 1 OR (t.HasRoot = 0 AND s.StartTime < t.StartTime) THEN s.RootName ELSE t.RootName END,
                    ServiceName = CASE WHEN s.HasRoot = 1 OR (t.HasRoot = 0 AND s.StartTime < t.StartTime) THEN s.ServiceName ELSE t.ServiceName END,
                    RootAttributesJson = CASE WHEN s.HasRoot = 1 OR (t.HasRoot = 0 AND s.StartTime < t.StartTime) THEN s.RootAttributesJson ELSE t.RootAttributesJson END,
                    HasRoot = CASE WHEN s.HasRoot = 1 THEN 1 ELSE t.HasRoot END,
                    StartTime = CASE WHEN s.StartTime < t.StartTime THEN s.StartTime ELSE t.StartTime END,
                    EndTime = CASE WHEN s.EndTime > t.EndTime THEN s.EndTime ELSE t.EndTime END,
                    HasError = CASE WHEN s.HasError = 1 THEN 1 ELSE t.HasError END,
                    SpanCount = t.SpanCount + s.SpanCount
                WHEN NOT MATCHED THEN
                    INSERT (TraceId, StartTime, EndTime, HasError, SpanCount, HasRoot, RootName, ServiceName, RootAttributesJson)
                    VALUES (s.TraceId, s.StartTime, s.EndTime, s.HasError, s.SpanCount, s.HasRoot, s.RootName, s.ServiceName, s.RootAttributesJson);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
