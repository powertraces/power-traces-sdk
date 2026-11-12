using Npgsql;
using OpenTelemetry;
using PowerTraces;
using PowerTraces.Storage;

namespace PowerTraces.PostgreSQL;

/// <summary>Creates the library's schema/tables in the target PostgreSQL database if they don't
/// already exist. Pure ADO.NET (Npgsql) — no EF Core, no migrations project. Safe to call every
/// time the host application starts: a transaction-scoped advisory lock
/// (<c>pg_advisory_xact_lock</c>) guards the DDL so two instances starting at once don't race each
/// other; the lock auto-releases on commit/rollback, no explicit unlock needed.</summary>
public static class PostgreSqlSchemaInitializer
{
    // Shared by every PostgreSqlStorageWriter/PostgreSqlDashboardStore instance in the process, so
    // a writer and a dashboard store pointed at the same database only actually run this once
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
        // If the host has Npgsql instrumentation enabled, every one of these calls would otherwise
        // itself become a span — including pg_advisory_xact_lock, whose statement text is just
        // "SELECT pg_advisory_xact_lock(hashtext(@Schema)::bigint);" with no reference to our
        // actual schema name (that's only ever a parameter VALUE, which instrumentation
        // deliberately doesn't capture), so the exporter's own tag-based self-exclusion can't
        // catch it. Suppressing instrumentation for this whole method is simpler and more complete
        // than trying to pattern-match every possible internal statement.
        using var _ = SuppressInstrumentationScope.Begin();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await AcquireLockAsync(connection, transaction, schema, ct).ConfigureAwait(false);

        // Databases created by an earlier version have spans but no trace-summary/attribute-index
        // tables; those get backfilled once, below, right after the tables are created.
        var backfill = !await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.TracesTable, ct).ConfigureAwait(false)
            && await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.SpansTable, ct).ConfigureAwait(false);

        // Traces written before the search index existed get indexed in the background by the
        // dashboard (see SqlDashboardStore.BackfillSearchIndexAsync); this marks that work as due.
        var searchBackfill = await TableExistsAsync(connection, transaction, schema, PowerTracesSchema.SpansTable, ct).ConfigureAwait(false)
            && !await SearchColumnExistsAsync(connection, transaction, schema, ct).ConfigureAwait(false);

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
            mark.CommandText = $"""
                INSERT INTO {PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.SettingsTable)} ("Key", "Value", "UpdatedAt")
                VALUES (@Key, @Value, now()) ON CONFLICT ("Key") DO NOTHING;
                """;
            mark.Parameters.AddWithValue("@Key", PowerTracesSchema.SearchBackfillSettingKey);
            mark.Parameters.AddWithValue("@Value", "{}");
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @Table;";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;
    }

    private static async Task<bool> SearchColumnExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = @Schema AND table_name = @Table AND column_name = @Column;";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", PowerTracesSchema.TracesTable);
        cmd.Parameters.AddWithValue("@Column", SearchColumn);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;
    }

    /// <summary>The trace summary column holding the trace's search terms.</summary>
    public const string SearchColumn = "SearchTerms";

    /// <summary>Populates the trace summaries from rows an earlier version wrote. Runs once, in the schema-creation transaction (so no concurrent writer can slip rows
    /// in between), with no timeout — it's proportional to the existing data.</summary>
    private static async Task BackfillAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken ct)
    {
        var spans = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.SpansTable);
        var resources = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            INSERT INTO {PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable)}
                ("TraceId", "StartTime", "EndTime", "HasError", "SpanCount", "HasRoot", "RootName", "ServiceName", "RootAttributesJson")
            SELECT g."TraceId", g."StartTime", g."EndTime", g."HasError", g."SpanCount",
                   rep."ParentSpanId" = '', rep."Name", r."ServiceName", rep."AttributesJson"
            FROM (
                SELECT "TraceId", MIN("StartTime") AS "StartTime", MAX(COALESCE("EndTime", "StartTime")) AS "EndTime",
                       BOOL_OR("HasError") AS "HasError", COUNT(*) AS "SpanCount"
                FROM {spans} GROUP BY "TraceId"
            ) g
            CROSS JOIN LATERAL (
                SELECT s."Name", s."AttributesJson", s."ResourceId", s."ParentSpanId"
                FROM {spans} s WHERE s."TraceId" = g."TraceId"
                ORDER BY (s."ParentSpanId" = '') DESC, s."StartTime", s."SpanId"
                LIMIT 1
            ) rep
            LEFT JOIN {resources} r ON r."Id" = rep."ResourceId";

            -- Fresh planner statistics for the just-filled table, so the first queries against it
            -- pick its index.
            ANALYZE {PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable)};
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken ct)
    {
        // Transaction-scoped advisory lock keyed off the schema name: hashtext() folds the name
        // to an int4, widened to the bigint pg_advisory_xact_lock expects. Unlike SQL Server's
        // sp_getapplock this blocks (no timeout) rather than failing fast, which is fine here —
        // schema init is a one-time, sub-second operation.
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@Schema)::bigint);";
        cmd.Parameters.AddWithValue("@Schema", $"PowerTraces_SchemaInit_{schema}");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildDdl(string schema)
    {
        var resources = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable);
        var spans = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.SpansTable);
        var logs = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.LogsTable);
        var ignoreRules = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.IgnoreRulesTable);
        var traces = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable);
        var attributeKeys = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.AttributeKeysTable);
        var legacySpanAttributes = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.LegacySpanAttributesTable);
        var legacyLogAttributes = PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.LegacyLogAttributesTable);
        var schemaIdent = PostgreSqlIdentifiers.Quote(schema);

        return $$"""
            CREATE SCHEMA IF NOT EXISTS {{schemaIdent}};

            CREATE TABLE IF NOT EXISTS {{resources}} (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ServiceName" varchar(200) NULL,
                "AttributesJson" text NOT NULL DEFAULT '{}',
                "AttributesHash" text GENERATED ALWAYS AS (md5("AttributesJson")) STORED,
                "SchemaUrl" varchar(500) NULL,
                "FirstSeenAt" timestamptz NOT NULL,
                "LastSeenAt" timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Resources_AttributesHash" ON {{resources}} ("AttributesHash");
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Resources_ServiceName" ON {{resources}} ("ServiceName");

            CREATE TABLE IF NOT EXISTS {{spans}} (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TraceId" varchar(32) NOT NULL,
                "SpanId" varchar(16) NOT NULL,
                "ParentSpanId" varchar(16) NOT NULL DEFAULT '',
                "ResourceId" uuid NULL,
                "Name" varchar(400) NOT NULL DEFAULT '',
                "Kind" varchar(30) NULL,
                "StartTime" timestamptz NOT NULL,
                "EndTime" timestamptz NULL,
                "Status" varchar(30) NULL,
                "StatusMessage" varchar(1000) NULL,
                "HasError" boolean NOT NULL DEFAULT false,
                "HttpStatus" varchar(10) NULL,
                "AttributesJson" text NOT NULL DEFAULT '{}',
                "ScopeLibraryName" varchar(200) NULL,
                "ScopeLibraryVersion" varchar(50) NULL,
                "EventsJson" text NOT NULL DEFAULT '[]',
                "LinksJson" text NOT NULL DEFAULT '[]',
                CONSTRAINT "FK_{{schema}}_Spans_Resource" FOREIGN KEY ("ResourceId")
                    REFERENCES {{resources}} ("Id") ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Spans_StartTime" ON {{spans}} ("StartTime");
            -- Serves both loading a trace's spans and the trace list's per-trace EXISTS probes
            -- (span name search, service filter) as index-only scans.
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Spans_TraceId_Cover" ON {{spans}} ("TraceId") INCLUDE ("Name", "ResourceId");

            CREATE TABLE IF NOT EXISTS {{logs}} (
                "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "ResourceId" uuid NULL,
                "Timestamp" timestamptz NOT NULL,
                "ObservedTimestamp" timestamptz NULL,
                "TraceId" varchar(32) NULL,
                "SpanId" varchar(16) NULL,
                "SeverityNumber" integer NOT NULL DEFAULT 0,
                "SeverityText" varchar(20) NULL,
                "Body" text NULL,
                "AttributesJson" text NOT NULL DEFAULT '{}',
                "ScopeLibraryName" varchar(200) NULL,
                "ScopeLibraryVersion" varchar(50) NULL,
                "SchemaUrl" varchar(500) NULL,
                CONSTRAINT "FK_{{schema}}_Logs_Resource" FOREIGN KEY ("ResourceId")
                    REFERENCES {{resources}} ("Id") ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Logs_Timestamp" ON {{logs}} ("Timestamp");
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Logs_TraceId" ON {{logs}} ("TraceId");
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Logs_SeverityNumber" ON {{logs}} ("SeverityNumber");
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Logs_ResourceId_Timestamp" ON {{logs}} ("ResourceId", "Timestamp");

            CREATE TABLE IF NOT EXISTS {{ignoreRules}} (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Kind" varchar(10) NOT NULL,
                "Name" varchar(400) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_{{schema}}_IgnoreRules_Kind_Name" UNIQUE ("Kind", "Name")
            );

            CREATE TABLE IF NOT EXISTS {{PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.SettingsTable)}} (
                "Key" varchar(100) NOT NULL PRIMARY KEY,
                "Value" text NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS {{PostgreSqlIdentifiers.Qualify(schema, PowerTracesSchema.PinnedTracesTable)}} (
                "TraceId" varchar(32) NOT NULL PRIMARY KEY,
                "RootName" varchar(400) NULL,
                "ServiceName" varchar(200) NULL,
                "StartTime" timestamptz NULL,
                "HasError" boolean NOT NULL,
                "PinnedAt" timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS {{traces}} (
                "TraceId" varchar(32) NOT NULL PRIMARY KEY,
                "StartTime" timestamptz NOT NULL,
                "EndTime" timestamptz NOT NULL,
                "HasError" boolean NOT NULL,
                "SpanCount" integer NOT NULL,
                "HasRoot" boolean NOT NULL,
                "RootName" varchar(400) NULL,
                "ServiceName" varchar(200) NULL,
                "RootAttributesJson" text NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Traces_StartTime" ON {{traces}} ("StartTime" DESC, "TraceId") INCLUDE ("HasError");
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Traces_RootName" ON {{traces}} ("RootName", "StartTime" DESC);

            -- The trace search index: each trace's search terms (see SearchTerms) as the lexemes of a
            -- tsvector, under a GIN index — one compressed posting list per term, so the index
            -- costs a few bytes per (term, trace) and still answers prefix lookups ('decl':*).
            ALTER TABLE {{traces}} ADD COLUMN IF NOT EXISTS "{{SearchColumn}}" tsvector NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS "IX_{{schema}}_Traces_SearchTerms" ON {{traces}} USING GIN ("{{SearchColumn}}");

            CREATE TABLE IF NOT EXISTS {{attributeKeys}} (
                "Key" varchar({{AttributeText.MaxKeyLength}}) NOT NULL,
                "Source" varchar(10) NOT NULL,
                "FirstSeenAt" timestamptz NOT NULL,
                PRIMARY KEY ("Key", "Source")
            );

            -- Earlier versions indexed every attribute value in two tables: their keys seed
            -- AttributeKeys, then they're dropped (filters read the attribute JSON now).
            DO $migrate$
            BEGIN
                IF to_regclass('{{legacySpanAttributes}}') IS NOT NULL THEN
                    INSERT INTO {{attributeKeys}} ("Key", "Source", "FirstSeenAt")
                    SELECT DISTINCT "Key", 'traces', now() FROM {{legacySpanAttributes}} ON CONFLICT DO NOTHING;
                    DROP TABLE {{legacySpanAttributes}};
                END IF;
                IF to_regclass('{{legacyLogAttributes}}') IS NOT NULL THEN
                    INSERT INTO {{attributeKeys}} ("Key", "Source", "FirstSeenAt")
                    SELECT DISTINCT "Key", 'logs', now() FROM {{legacyLogAttributes}} ON CONFLICT DO NOTHING;
                    DROP TABLE {{legacyLogAttributes}};
                END IF;
            END
            $migrate$;
            """;
    }
}

/// <summary>Identifier-quoting helpers shared by the PostgreSQL write and read sides. Unlike SQL
/// Server, PostgreSQL lowercases unquoted identifiers — since this library's schema uses PascalCase
/// column/table names (matched verbatim against the quoted DDL above), every reference to them in
/// DDL or DML must be double-quoted. Public so <c>PowerTraces.Dashboard</c>'s PostgreSQL read side can
/// reuse it rather than duplicating the same escaping.</summary>
public static class PostgreSqlIdentifiers
{
    public static string Quote(string identifier) => $"\"{identifier}\"";

    public static string Qualify(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";
}
