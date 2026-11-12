using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;
using PowerTraces.Storage.Sql;

namespace PowerTraces.Sqlite;

/// <summary>Writes batches of spans/logs to a SQLite file: makes sure the tables exist (once,
/// lazily), then — in one transaction per batch — resolves the batch's Resource row and inserts
/// the rows through one prepared command each, plus, for spans, an upsert per trace into the
/// summary table the trace list reads. SQLite allows one writer at a time; a writer that finds the
/// file busy waits (up to the command timeout) rather than failing. One instance is shared by
/// both exporters for a given options instance.</summary>
public sealed class SqliteStorageWriter(PowerTracesExporterOptions options) : IStorageWriter, IIgnoreRuleSource
{
    private readonly ConcurrentDictionary<string, string> _resourceIds = new(StringComparer.Ordinal);

    private string Table(string table) => SqliteDialect.Qualify(options.Schema, table);

    public Task EnsureSchemaAsync(CancellationToken ct) =>
        SqliteSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    public async Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await SqlIgnoreRules.ListAsync(SqliteDialect.Instance, options.ConnectionString, options.Schema, ct).ConfigureAwait(false);
    }

    public async Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await SqlIgnoreRules.DeleteTracesAsync(SqliteDialect.Instance, options.ConnectionString, options.Schema, traceIds, ct).ConfigureAwait(false);
    }

    public async Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await InTransactionAsync(resource, async (connection, transaction, resourceId) =>
        {
            await InsertSpansAsync(connection, transaction, rows, resourceId, ct).ConfigureAwait(false);
            await InsertSearchTermsAsync(connection, transaction, SearchTerms.FromBatch(rows), ct).ConfigureAwait(false);
            await UpsertTraceSummariesAsync(connection, transaction, TraceSummary.FromBatch(rows, resource), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(SqliteDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    public async Task WriteLogsAsync(IReadOnlyList<LogRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await InTransactionAsync(resource, (connection, transaction, resourceId) =>
            InsertLogsAsync(connection, transaction, rows, resourceId, ct), ct).ConfigureAwait(false);

        await SqlAttributeKeys.RecordAsync(SqliteDialect.Instance, options.ConnectionString, options.Schema, rows, ct).ConfigureAwait(false);
    }

    private async Task InTransactionAsync(ResourceInfo resource, Func<SqliteConnection, SqliteTransaction, string, Task> work, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // Immediate (not deferred): take the write lock up front, so a busy file is waited on
        // here rather than failing halfway through the batch.
        await using var transaction = connection.BeginTransaction(deferred: false);

        var resourceId = await GetOrCreateResourceAsync(connection, transaction, resource, ct).ConfigureAwait(false);
        await work(connection, transaction, resourceId).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        // Cached only once committed — a rolled-back row's id must not be reused.
        _resourceIds[resource.AttributesJson] = resourceId;
    }

    /// <summary>One Resources row per distinct attribute set; a process normally has one for its
    /// whole lifetime, so this is a lookup once per writer.</summary>
    private async Task<string> GetOrCreateResourceAsync(SqliteConnection connection, SqliteTransaction transaction, ResourceInfo resource, CancellationToken ct)
    {
        if (_resourceIds.TryGetValue(resource.AttributesJson, out var cached))
        {
            return cached;
        }

        var now = DateTime.UtcNow;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = $"""SELECT "Id" FROM {Table(PowerTracesSchema.ResourcesTable)} WHERE "AttributesJson" = @AttributesJson LIMIT 1""";
            find.Parameters.AddWithValue("@AttributesJson", resource.AttributesJson);
            if (await find.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existing)
            {
                await using var touch = connection.CreateCommand();
                touch.Transaction = transaction;
                touch.CommandText = $"""UPDATE {Table(PowerTracesSchema.ResourcesTable)} SET "LastSeenAt" = @Now WHERE "Id" = @Id""";
                touch.Parameters.AddWithValue("@Now", now);
                touch.Parameters.AddWithValue("@Id", existing);
                await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"""
            INSERT INTO {Table(PowerTracesSchema.ResourcesTable)} ("Id", "ServiceName", "AttributesJson", "SchemaUrl", "FirstSeenAt", "LastSeenAt")
            VALUES (@Id, @ServiceName, @AttributesJson, @SchemaUrl, @Now, @Now)
            """;
        insert.Parameters.AddWithValue("@Id", id);
        insert.Parameters.AddWithValue("@ServiceName", Db(resource.ServiceName));
        insert.Parameters.AddWithValue("@AttributesJson", resource.AttributesJson);
        insert.Parameters.AddWithValue("@SchemaUrl", Db(resource.SchemaUrl));
        insert.Parameters.AddWithValue("@Now", now);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return id;
    }

    private async Task InsertSpansAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SpanRow> rows, string resourceId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            INSERT INTO {Table(PowerTracesSchema.SpansTable)}
                ("Id", "TraceId", "SpanId", "ParentSpanId", "ResourceId", "Name", "Kind", "StartTime", "EndTime", "Status", "StatusMessage",
                 "HasError", "HttpStatus", "AttributesJson", "ScopeLibraryName", "ScopeLibraryVersion", "EventsJson", "LinksJson")
            VALUES (@Id, @TraceId, @SpanId, @ParentSpanId, @ResourceId, @Name, @Kind, @StartTime, @EndTime, @Status, @StatusMessage,
                 @HasError, @HttpStatus, @AttributesJson, @ScopeLibraryName, @ScopeLibraryVersion, @EventsJson, @LinksJson)
            """;
        var p = Parameters(cmd, "@Id", "@TraceId", "@SpanId", "@ParentSpanId", "@ResourceId", "@Name", "@Kind", "@StartTime", "@EndTime", "@Status",
            "@StatusMessage", "@HasError", "@HttpStatus", "@AttributesJson", "@ScopeLibraryName", "@ScopeLibraryVersion", "@EventsJson", "@LinksJson");

        foreach (var r in rows)
        {
            Set(p, r.Id.ToString(), r.TraceId, r.SpanId, r.ParentSpanId, resourceId, r.Name, r.Kind, r.StartTime, r.EndTime, r.Status,
                r.StatusMessage, r.HasError, r.HttpStatus, StoredJson.Pack(r.AttributesJson), r.ScopeLibraryName, r.ScopeLibraryVersion, StoredJson.Pack(r.EventsJson), r.LinksJson);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task InsertSearchTermsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<TraceTerms> terms, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            INSERT OR IGNORE INTO {Table(PowerTracesSchema.SearchTermsTable)} ("Term", "StartTime", "TraceId") VALUES (@Term, @StartTime, @TraceId)
            """;
        var p = Parameters(cmd, "@Term", "@StartTime", "@TraceId");

        foreach (var trace in terms)
        {
            foreach (var term in trace.Terms)
            {
                Set(p, term, trace.StartTime, trace.TraceId);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task InsertLogsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<LogRow> rows, string resourceId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            INSERT INTO {Table(PowerTracesSchema.LogsTable)}
                ("ResourceId", "Timestamp", "ObservedTimestamp", "TraceId", "SpanId", "SeverityNumber", "SeverityText", "Body",
                 "AttributesJson", "ScopeLibraryName", "ScopeLibraryVersion", "SchemaUrl")
            VALUES (@ResourceId, @Timestamp, @ObservedTimestamp, @TraceId, @SpanId, @SeverityNumber, @SeverityText, @Body,
                 @AttributesJson, @ScopeLibraryName, @ScopeLibraryVersion, @SchemaUrl)
            """;
        var p = Parameters(cmd, "@ResourceId", "@Timestamp", "@ObservedTimestamp", "@TraceId", "@SpanId", "@SeverityNumber", "@SeverityText", "@Body",
            "@AttributesJson", "@ScopeLibraryName", "@ScopeLibraryVersion", "@SchemaUrl");

        foreach (var r in rows)
        {
            Set(p, resourceId, r.Timestamp, r.ObservedTimestamp, r.TraceId, r.SpanId, r.SeverityNumber, r.SeverityText, r.Body,
                r.AttributesJson, r.ScopeLibraryName, r.ScopeLibraryVersion, r.SchemaUrl);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Merges each trace's contribution from this batch into its summary row. In
    /// <c>DO UPDATE</c>, bare column names are the row as stored and <c>excluded.</c> the incoming
    /// values; every SET expression sees the stored row, which the root-replacement condition
    /// needs.</summary>
    private async Task UpsertTraceSummariesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<TraceSummary> summaries, CancellationToken ct)
    {
        const string replaceRoot = """excluded."HasRoot" OR (NOT "HasRoot" AND excluded."StartTime" < "StartTime")""";

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            INSERT INTO {Table(PowerTracesSchema.TracesTable)}
                ("TraceId", "StartTime", "EndTime", "HasError", "SpanCount", "HasRoot", "RootName", "ServiceName", "RootAttributesJson")
            VALUES (@TraceId, @StartTime, @EndTime, @HasError, @SpanCount, @HasRoot, @RootName, @ServiceName, @RootAttributesJson)
            ON CONFLICT ("TraceId") DO UPDATE SET
                "RootName" = CASE WHEN {replaceRoot} THEN excluded."RootName" ELSE "RootName" END,
                "ServiceName" = CASE WHEN {replaceRoot} THEN excluded."ServiceName" ELSE "ServiceName" END,
                "RootAttributesJson" = CASE WHEN {replaceRoot} THEN excluded."RootAttributesJson" ELSE "RootAttributesJson" END,
                "HasRoot" = "HasRoot" OR excluded."HasRoot",
                "StartTime" = MIN("StartTime", excluded."StartTime"),
                "EndTime" = MAX("EndTime", excluded."EndTime"),
                "HasError" = "HasError" OR excluded."HasError",
                "SpanCount" = "SpanCount" + excluded."SpanCount"
            """;
        var p = Parameters(cmd, "@TraceId", "@StartTime", "@EndTime", "@HasError", "@SpanCount", "@HasRoot", "@RootName", "@ServiceName", "@RootAttributesJson");

        foreach (var s in summaries)
        {
            Set(p, s.TraceId, s.StartTime, s.EndTime, s.HasError, s.SpanCount, s.HasRoot, s.RootName, s.ServiceName, StoredJson.Pack(s.RootAttributesJson));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static SqliteParameter[] Parameters(SqliteCommand cmd, params string[] names) =>
        names.Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();

    private static void Set(SqliteParameter[] parameters, params object?[] values)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i].Value = Db(values[i]);
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
