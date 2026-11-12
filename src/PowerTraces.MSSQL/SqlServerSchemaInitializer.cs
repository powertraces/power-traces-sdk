using System.Data;
using Microsoft.Data.SqlClient;
using OpenTelemetry;
using PowerTraces;
using PowerTraces.Search;
using PowerTraces.Storage;

namespace PowerTraces.MSSQL;

/// <summary>Creates the library's schema/tables in the target SQL Server database if they don't
/// already exist. Pure ADO.NET — no EF Core, no migrations project. Safe to call every time the
/// host application starts: an sp_getapplock guards the DDL so two instances starting at once
/// don't race each other.</summary>
public static class SqlServerSchemaInitializer
{
    // Shared by every SqlServerStorageWriter/SqlServerDashboardStore instance in the process, so a
    // writer and a dashboard store pointed at the same database only actually run this once
    // between them, instead of each independently guarding its own duplicate "have I done this
    // already?" check.
    private static readonly SchemaInitGate Gate = new();

    public static Task EnsureCreatedAsync(string connectionString, CancellationToken ct = default)
        => EnsureCreatedAsync(connectionString, PowerTracesSchema.DefaultSchemaName, ct);

    public static Task EnsureCreatedAsync(string connectionString, string schema, CancellationToken ct = default)
    {
        PowerTracesSchema.ValidateSchemaName(schema);
        return Gate.RunOnceAsync(connectionString, schema, ct2 => EnsureCreatedCoreAsync(connectionString, schema, ct2), ct);
    }

    private static async Task EnsureCreatedCoreAsync(string connectionString, string schema, CancellationToken ct)
    {
        // If the host has SqlClient instrumentation enabled, every one of these calls would
        // otherwise itself become a span — including the sp_getapplock RPC, whose db.statement is
        // just the literal text "sp_getapplock" with no reference to our schema/table names (the
        // lock resource name only ever appears as a parameter VALUE, which instrumentation
        // deliberately doesn't capture), so the exporter's own tag-based self-exclusion can't
        // catch it. Suppressing instrumentation for this whole method is simpler and more complete
        // than trying to pattern-match every possible internal statement.
        using var _ = SuppressInstrumentationScope.Begin();

        await using var connection = SqlServerConnections.Create(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await AcquireLockAsync(connection, transaction, schema, ct).ConfigureAwait(false);

        // Databases created by an earlier version have spans but no trace-summary/attribute-index
        // tables; those get backfilled once, below, right after the tables are created.
        var backfill = !await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.TracesTable, ct).ConfigureAwait(false)
            && await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.SpansTable, ct).ConfigureAwait(false);

        // Traces written before the search index existed get indexed in the background by the
        // dashboard (see SqlDashboardStore.BackfillSearchIndexAsync); this marks that work as due.
        var searchBackfill = !await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.SearchTermsTable, ct).ConfigureAwait(false)
            && await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.SpansTable, ct).ConfigureAwait(false);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            // No timeout: on a database written by an earlier version this also builds new
            // indexes over the existing rows, which can take minutes on a large table — a timeout
            // would fail schema init on every attempt.
            cmd.CommandTimeout = 0;
            cmd.CommandText = BuildDdl(schema);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (backfill)
        {
            await BackfillAsync(connection, transaction, schema, ct).ConfigureAwait(false);
        }

        if (searchBackfill)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            var settings = PowerTracesSchema.Qualify(schema, PowerTracesSchema.SettingsTable);
            mark.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM {settings} WHERE [Key] = @Key)
                    INSERT INTO {settings} ([Key], [Value], UpdatedAt) VALUES (@Key, @Value, SYSUTCDATETIME());
                """;
            mark.Parameters.AddWithValue("@Key", PowerTracesSchema.SearchBackfillSettingKey);
            mark.Parameters.AddWithValue("@Value", "{}");
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlTransaction transaction, string schema, string table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @Table AND schema_id = SCHEMA_ID(@Schema);";
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Schema", schema);
        return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;
    }

    /// <summary>Populates the trace summaries from rows an earlier version wrote. Runs once, in the
    /// schema-creation transaction (so no concurrent writer can slip rows in between), with no
    /// timeout — it's proportional to the existing data.</summary>
    private static async Task BackfillAsync(SqlConnection connection, SqlTransaction transaction, string schema, CancellationToken ct)
    {
        var spans = PowerTracesSchema.Qualify(schema, PowerTracesSchema.SpansTable);
        var resources = PowerTracesSchema.Qualify(schema, PowerTracesSchema.ResourcesTable);

        await ExecuteAsync($"""
            INSERT INTO {PowerTracesSchema.Qualify(schema, PowerTracesSchema.TracesTable)}
                (TraceId, StartTime, EndTime, HasError, SpanCount, HasRoot, RootName, ServiceName, RootAttributesJson)
            SELECT g.TraceId, g.StartTime, g.EndTime, CAST(g.HasError AS BIT), g.SpanCount,
                   CAST(CASE WHEN rep.ParentSpanId = '' THEN 1 ELSE 0 END AS BIT), rep.Name, r.ServiceName, rep.AttributesJson
            FROM (
                SELECT TraceId, MIN(StartTime) AS StartTime, MAX(COALESCE(EndTime, StartTime)) AS EndTime,
                       MAX(CAST(HasError AS INT)) AS HasError, COUNT(*) AS SpanCount
                FROM {spans} GROUP BY TraceId
            ) g
            CROSS APPLY (
                SELECT TOP (1) s.Name, s.AttributesJson, s.ResourceId, s.ParentSpanId
                FROM {spans} s WHERE s.TraceId = g.TraceId
                ORDER BY CASE WHEN s.ParentSpanId = '' THEN 0 ELSE 1 END, s.StartTime, s.SpanId
            ) rep
            LEFT JOIN {resources} r ON r.Id = rep.ResourceId;
            """).ConfigureAwait(false);

        async Task ExecuteAsync(string sql)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = 0;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task AcquireLockAsync(SqlConnection connection, SqlTransaction transaction, string schema, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "sp_getapplock";
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@Resource", $"PowerTraces_SchemaInit_{schema}");
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Transaction");
        cmd.Parameters.AddWithValue("@LockTimeout", 30_000);
        var returnValue = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if ((int)returnValue.Value! < 0)
        {
            throw new InvalidOperationException(
                $"Could not acquire the PowerTraces schema-init lock for schema '{schema}' (another process may be holding it).");
        }
    }

    private static string BuildDdl(string schema)
    {
        var resources = PowerTracesSchema.Qualify(schema, PowerTracesSchema.ResourcesTable);
        var spans = PowerTracesSchema.Qualify(schema, PowerTracesSchema.SpansTable);
        var logs = PowerTracesSchema.Qualify(schema, PowerTracesSchema.LogsTable);
        var traces = PowerTracesSchema.Qualify(schema, PowerTracesSchema.TracesTable);
        var attributeKeys = PowerTracesSchema.Qualify(schema, PowerTracesSchema.AttributeKeysTable);
        var searchTerms = PowerTracesSchema.Qualify(schema, PowerTracesSchema.SearchTermsTable);
        var legacySpanAttributes = PowerTracesSchema.Qualify(schema, PowerTracesSchema.LegacySpanAttributesTable);
        var legacyLogAttributes = PowerTracesSchema.Qualify(schema, PowerTracesSchema.LegacyLogAttributesTable);

        return $$"""
        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{{schema}}')
            EXEC('CREATE SCHEMA [{{schema}}]');

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.ResourcesTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{resources}} (
                Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                ServiceName NVARCHAR(200) NULL,
                AttributesJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_{{schema}}_Resources_AttributesJson DEFAULT ('{}'),
                AttributesHash AS CHECKSUM(AttributesJson) PERSISTED,
                SchemaUrl NVARCHAR(500) NULL,
                FirstSeenAt DATETIME2 NOT NULL,
                LastSeenAt DATETIME2 NOT NULL
            );
            CREATE INDEX IX_{{schema}}_Resources_AttributesHash ON {{resources}} (AttributesHash);
            CREATE INDEX IX_{{schema}}_Resources_ServiceName ON {{resources}} (ServiceName);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.SpansTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{spans}} (
                Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                TraceId VARCHAR(32) NOT NULL,
                SpanId VARCHAR(16) NOT NULL,
                ParentSpanId VARCHAR(16) NOT NULL DEFAULT (''),
                ResourceId UNIQUEIDENTIFIER NULL,
                Name NVARCHAR(400) NOT NULL DEFAULT (''),
                Kind NVARCHAR(30) NULL,
                StartTime DATETIME2 NOT NULL,
                EndTime DATETIME2 NULL,
                Status NVARCHAR(30) NULL,
                StatusMessage NVARCHAR(1000) NULL,
                HasError BIT NOT NULL DEFAULT (0),
                HttpStatus NVARCHAR(10) NULL,
                AttributesJson NVARCHAR(MAX) NOT NULL DEFAULT ('{}'),
                ScopeLibraryName NVARCHAR(200) NULL,
                ScopeLibraryVersion NVARCHAR(50) NULL,
                EventsJson NVARCHAR(MAX) NOT NULL DEFAULT ('[]'),
                LinksJson NVARCHAR(MAX) NOT NULL DEFAULT ('[]'),
                CONSTRAINT FK_{{schema}}_Spans_Resource FOREIGN KEY (ResourceId)
                    REFERENCES {{resources}} (Id) ON DELETE SET NULL
            );
            CREATE INDEX IX_{{schema}}_Spans_StartTime ON {{spans}} (StartTime);
        END

        -- Serves both loading a trace's spans and the trace list's per-trace EXISTS probes (span
        -- name search, service filter) without touching the wide base rows.
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{{schema}}_Spans_TraceId_Cover' AND object_id = OBJECT_ID('{{spans}}'))
            CREATE INDEX IX_{{schema}}_Spans_TraceId_Cover ON {{spans}} (TraceId) INCLUDE (Name, ResourceId);

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.LogsTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{logs}} (
                Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ResourceId UNIQUEIDENTIFIER NULL,
                Timestamp DATETIME2 NOT NULL,
                ObservedTimestamp DATETIME2 NULL,
                TraceId VARCHAR(32) NULL,
                SpanId VARCHAR(16) NULL,
                SeverityNumber INT NOT NULL DEFAULT (0),
                SeverityText NVARCHAR(20) NULL,
                Body NVARCHAR(MAX) NULL,
                AttributesJson NVARCHAR(MAX) NOT NULL DEFAULT ('{}'),
                ScopeLibraryName NVARCHAR(200) NULL,
                ScopeLibraryVersion NVARCHAR(50) NULL,
                SchemaUrl NVARCHAR(500) NULL,
                CONSTRAINT FK_{{schema}}_Logs_Resource FOREIGN KEY (ResourceId)
                    REFERENCES {{resources}} (Id) ON DELETE SET NULL
            );
            CREATE INDEX IX_{{schema}}_Logs_Timestamp ON {{logs}} (Timestamp);
            CREATE INDEX IX_{{schema}}_Logs_TraceId ON {{logs}} (TraceId);
            CREATE INDEX IX_{{schema}}_Logs_SeverityNumber ON {{logs}} (SeverityNumber);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{{schema}}_Logs_ResourceId_Timestamp' AND object_id = OBJECT_ID('{{logs}}'))
            CREATE INDEX IX_{{schema}}_Logs_ResourceId_Timestamp ON {{logs}} (ResourceId, Timestamp);

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.IgnoreRulesTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{PowerTracesSchema.Qualify(schema, PowerTracesSchema.IgnoreRulesTable)}} (
                Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                Kind NVARCHAR(10) NOT NULL,
                Name NVARCHAR(400) NOT NULL,
                CreatedAt DATETIME2 NOT NULL,
                CONSTRAINT UQ_{{schema}}_IgnoreRules_Kind_Name UNIQUE (Kind, Name)
            );
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.SettingsTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{PowerTracesSchema.Qualify(schema, PowerTracesSchema.SettingsTable)}} (
                [Key] NVARCHAR(100) NOT NULL CONSTRAINT PK_{{schema}}_Settings PRIMARY KEY,
                [Value] NVARCHAR(MAX) NOT NULL,
                UpdatedAt DATETIME2 NOT NULL
            );
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.PinnedTracesTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{PowerTracesSchema.Qualify(schema, PowerTracesSchema.PinnedTracesTable)}} (
                TraceId VARCHAR(32) NOT NULL CONSTRAINT PK_{{schema}}_PinnedTraces PRIMARY KEY,
                RootName NVARCHAR(400) NULL,
                ServiceName NVARCHAR(200) NULL,
                StartTime DATETIME2 NULL,
                HasError BIT NOT NULL,
                PinnedAt DATETIME2 NOT NULL
            );
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.TracesTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{traces}} (
                TraceId VARCHAR(32) NOT NULL CONSTRAINT PK_{{schema}}_Traces PRIMARY KEY,
                StartTime DATETIME2 NOT NULL,
                EndTime DATETIME2 NOT NULL,
                HasError BIT NOT NULL,
                SpanCount INT NOT NULL,
                HasRoot BIT NOT NULL,
                RootName NVARCHAR(400) NULL,
                ServiceName NVARCHAR(200) NULL,
                RootAttributesJson NVARCHAR(MAX) NULL
            );
            CREATE INDEX IX_{{schema}}_Traces_StartTime ON {{traces}} (StartTime DESC, TraceId) INCLUDE (HasError);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.AttributeKeysTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{attributeKeys}} (
                [Key] NVARCHAR({{AttributeText.MaxKeyLength}}) NOT NULL,
                Source VARCHAR(10) NOT NULL,
                FirstSeenAt DATETIME2 NOT NULL,
                CONSTRAINT PK_{{schema}}_AttributeKeys PRIMARY KEY ([Key], Source)
            );
        END

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{{schema}}_Traces_RootName' AND object_id = OBJECT_ID('{{traces}}'))
            CREATE INDEX IX_{{schema}}_Traces_RootName ON {{traces}} (RootName, StartTime DESC);

        -- The trace search index (see SearchTerms). BIN2 collation: terms compare by code point, so
        -- a prefix search is an index range scan. Clustered on the lookup key, so each row is stored
        -- once and a search reads consecutive rows. Trace ids are BINARY(16), and there is no index
        -- on them: a deleted trace's terms are left for retention to remove (they never match, as
        -- searches join to the live traces). Page compression (every edition since 2016 SP1)
        -- shares the long common prefixes of sorted terms.
        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{{PowerTracesSchema.SearchTermsTable}}' AND schema_id = SCHEMA_ID('{{schema}}'))
        BEGIN
            CREATE TABLE {{searchTerms}} (
                Term NVARCHAR({{SearchTerms.MaxTermLength + 56}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
                StartTime DATETIME2 NOT NULL,
                TraceId BINARY(16) NOT NULL
            );
            CREATE CLUSTERED INDEX IX_{{schema}}_SearchTerms_Term ON {{searchTerms}} (Term, StartTime, TraceId) WITH (DATA_COMPRESSION = PAGE);
        END

        -- Earlier versions indexed every attribute value in two tables: their keys seed
        -- AttributeKeys, then they're dropped (filters read the attribute JSON now).
        IF OBJECT_ID('{{legacySpanAttributes}}') IS NOT NULL
        BEGIN
            INSERT INTO {{attributeKeys}} ([Key], Source, FirstSeenAt)
            SELECT DISTINCT a.[Key], 'traces', SYSUTCDATETIME() FROM {{legacySpanAttributes}} a
            WHERE NOT EXISTS (SELECT 1 FROM {{attributeKeys}} k WHERE k.[Key] = a.[Key] AND k.Source = 'traces');
            DROP TABLE {{legacySpanAttributes}};
        END

        IF OBJECT_ID('{{legacyLogAttributes}}') IS NOT NULL
        BEGIN
            INSERT INTO {{attributeKeys}} ([Key], Source, FirstSeenAt)
            SELECT DISTINCT a.[Key], 'logs', SYSUTCDATETIME() FROM {{legacyLogAttributes}} a
            WHERE NOT EXISTS (SELECT 1 FROM {{attributeKeys}} k WHERE k.[Key] = a.[Key] AND k.Source = 'logs');
            DROP TABLE {{legacyLogAttributes}};
        END
        """;
    }
}
