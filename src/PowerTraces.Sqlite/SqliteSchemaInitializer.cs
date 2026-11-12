using Microsoft.Data.Sqlite;
using PowerTraces.Storage;

namespace PowerTraces.Sqlite;

/// <summary>Creates the library's tables in the SQLite file (and the file's folder) if they don't
/// exist yet, and switches the file to WAL journaling so the dashboard can read while the exporter
/// writes. Safe to call every time the host starts: every statement is <c>IF NOT EXISTS</c>, run in
/// an immediate transaction so two processes starting at once take turns.</summary>
public static class SqliteSchemaInitializer
{
    // Shared by every SqliteStorageWriter/SqliteDashboardStore in the process, so a writer and a
    // dashboard store pointed at the same file only run this once between them.
    private static readonly SchemaInitGate Gate = new();

    public static Task EnsureCreatedAsync(string connectionString, string schema, CancellationToken ct = default)
    {
        PowerTracesSchema.ValidateSchemaName(schema);
        return Gate.RunOnceAsync(connectionString, schema, ct2 => EnsureCreatedCoreAsync(connectionString, schema, ct2), ct);
    }

    private static async Task EnsureCreatedCoreAsync(string connectionString, string schema, CancellationToken ct)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrEmpty(dataSource) && dataSource != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(dataSource)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var wal = connection.CreateCommand())
        {
            // Persistent for the file: readers don't block the writer, nor it them.
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Traces written before the search index existed get indexed in the background by the
        // dashboard (see SqlDashboardStore.BackfillSearchIndexAsync); this marks that work as due.
        var searchBackfill = !await TableExistsAsync(PowerTracesSchema.SearchTermsTable).ConfigureAwait(false)
            && await TableExistsAsync(PowerTracesSchema.SpansTable).ConfigureAwait(false);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = BuildDdl(schema);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (searchBackfill)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = $"""
                INSERT OR IGNORE INTO {SqliteDialect.Qualify(schema, PowerTracesSchema.SettingsTable)} ("Key", "Value", "UpdatedAt")
                VALUES (@Key, @Value, @Now)
                """;
            mark.Parameters.AddWithValue("@Key", PowerTracesSchema.SearchBackfillSettingKey);
            mark.Parameters.AddWithValue("@Value", "{}");
            mark.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        async Task<bool> TableExistsAsync(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @Name";
            cmd.Parameters.AddWithValue("@Name", $"{schema}_{table}");
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        }
    }

    private static string BuildDdl(string schema)
    {
        string T(string table) => SqliteDialect.Qualify(schema, table);
        string I(string name) => $"\"IX_{schema}_{name}\"";

        return $"""
            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.ResourcesTable)} (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ServiceName" TEXT NULL,
                "AttributesJson" TEXT NOT NULL DEFAULT '{"{}"}',
                "SchemaUrl" TEXT NULL,
                "FirstSeenAt" TEXT NOT NULL,
                "LastSeenAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {I("Resources_AttributesJson")} ON {T(PowerTracesSchema.ResourcesTable)} ("AttributesJson");
            CREATE INDEX IF NOT EXISTS {I("Resources_ServiceName")} ON {T(PowerTracesSchema.ResourcesTable)} ("ServiceName");

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.SpansTable)} (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TraceId" TEXT NOT NULL,
                "SpanId" TEXT NOT NULL,
                "ParentSpanId" TEXT NOT NULL DEFAULT '',
                "ResourceId" TEXT NULL,
                "Name" TEXT NOT NULL DEFAULT '',
                "Kind" TEXT NULL,
                "StartTime" TEXT NOT NULL,
                "EndTime" TEXT NULL,
                "Status" TEXT NULL,
                "StatusMessage" TEXT NULL,
                "HasError" INTEGER NOT NULL DEFAULT 0,
                "HttpStatus" TEXT NULL,
                "AttributesJson" TEXT NOT NULL DEFAULT '{"{}"}',
                "ScopeLibraryName" TEXT NULL,
                "ScopeLibraryVersion" TEXT NULL,
                "EventsJson" TEXT NOT NULL DEFAULT '[]',
                "LinksJson" TEXT NOT NULL DEFAULT '[]'
            );
            CREATE INDEX IF NOT EXISTS {I("Spans_StartTime")} ON {T(PowerTracesSchema.SpansTable)} ("StartTime");
            CREATE INDEX IF NOT EXISTS {I("Spans_TraceId")} ON {T(PowerTracesSchema.SpansTable)} ("TraceId", "ResourceId", "Name");

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.LogsTable)} (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "ResourceId" TEXT NULL,
                "Timestamp" TEXT NOT NULL,
                "ObservedTimestamp" TEXT NULL,
                "TraceId" TEXT NULL,
                "SpanId" TEXT NULL,
                "SeverityNumber" INTEGER NOT NULL DEFAULT 0,
                "SeverityText" TEXT NULL,
                "Body" TEXT NULL,
                "AttributesJson" TEXT NOT NULL DEFAULT '{"{}"}',
                "ScopeLibraryName" TEXT NULL,
                "ScopeLibraryVersion" TEXT NULL,
                "SchemaUrl" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS {I("Logs_Timestamp")} ON {T(PowerTracesSchema.LogsTable)} ("Timestamp");
            CREATE INDEX IF NOT EXISTS {I("Logs_TraceId")} ON {T(PowerTracesSchema.LogsTable)} ("TraceId");
            CREATE INDEX IF NOT EXISTS {I("Logs_SeverityNumber")} ON {T(PowerTracesSchema.LogsTable)} ("SeverityNumber");
            CREATE INDEX IF NOT EXISTS {I("Logs_ResourceId_Timestamp")} ON {T(PowerTracesSchema.LogsTable)} ("ResourceId", "Timestamp");

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.IgnoreRulesTable)} (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                UNIQUE ("Kind", "Name")
            );

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.TracesTable)} (
                "TraceId" TEXT NOT NULL PRIMARY KEY,
                "StartTime" TEXT NOT NULL,
                "EndTime" TEXT NOT NULL,
                "HasError" INTEGER NOT NULL,
                "SpanCount" INTEGER NOT NULL,
                "HasRoot" INTEGER NOT NULL,
                "RootName" TEXT NULL,
                "ServiceName" TEXT NULL,
                "RootAttributesJson" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS {I("Traces_StartTime")} ON {T(PowerTracesSchema.TracesTable)} ("StartTime" DESC, "TraceId", "HasError");
            CREATE INDEX IF NOT EXISTS {I("Traces_RootName")} ON {T(PowerTracesSchema.TracesTable)} ("RootName", "StartTime" DESC);

            -- The trace search index (see SearchTerms). TEXT compares in binary order, so a prefix
            -- search is an index range scan; WITHOUT ROWID stores each row once, in lookup order
            -- (writers INSERT OR IGNORE).
            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.SearchTermsTable)} (
                "Term" TEXT NOT NULL,
                "StartTime" TEXT NOT NULL,
                "TraceId" TEXT NOT NULL,
                PRIMARY KEY ("Term", "StartTime", "TraceId")
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS {I("SearchTerms_TraceId")} ON {T(PowerTracesSchema.SearchTermsTable)} ("TraceId");

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.AttributeKeysTable)} (
                "Key" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "FirstSeenAt" TEXT NOT NULL,
                PRIMARY KEY ("Key", "Source")
            );

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.SettingsTable)} (
                "Key" TEXT NOT NULL PRIMARY KEY,
                "Value" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS {T(PowerTracesSchema.PinnedTracesTable)} (
                "TraceId" TEXT NOT NULL PRIMARY KEY,
                "RootName" TEXT NULL,
                "ServiceName" TEXT NULL,
                "StartTime" TEXT NULL,
                "HasError" INTEGER NOT NULL,
                "PinnedAt" TEXT NOT NULL
            );
            """;
    }
}
