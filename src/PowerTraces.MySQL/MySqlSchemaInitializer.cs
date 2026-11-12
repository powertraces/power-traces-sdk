using MySqlConnector;
using OpenTelemetry;
using PowerTraces;
using PowerTraces.Storage;

namespace PowerTraces.MySQL;

/// <summary>Creates the library's schema/tables in the target MySQL/MariaDB server if they don't
/// already exist. Pure ADO.NET (MySqlConnector) — no EF Core, no migrations project.
///
/// MySQL has no schema-as-namespace concept the way SQL Server/PostgreSQL do — a MySQL "database"
/// is the closest equivalent, so <see cref="PowerTracesExporterOptions.Schema"/> is used here as the
/// database name the tables live in: <c>CREATE DATABASE IF NOT EXISTS `schema`</c>, then every
/// table is qualified as <c>`schema`.`Spans`</c> etc. (rather than treating it as a table-name
/// prefix inside the connection string's own database). This keeps the option's meaning consistent
/// with how it's already documented on <see cref="PowerTracesExporterOptions.Schema"/> ("a SQL schema
/// for SQL Server/PostgreSQL/MySQL") and with the SQL Server/PostgreSQL providers' own
/// schema-per-namespace behavior.
///
/// Safe to call every time the host application starts: MySQL's <c>GET_LOCK</c>/<c>RELEASE_LOCK</c>
/// guard the DDL so two instances starting at once don't race each other. Unlike SQL Server's
/// sp_getapplock (transaction-scoped) or PostgreSQL's pg_advisory_xact_lock (also transaction-scoped,
/// auto-released on commit), MySQL's named locks are session-scoped and do NOT auto-release on
/// commit/rollback — the lock is explicitly released in a finally block on the same connection that
/// acquired it (named locks are tied to the connection/session that took them).</summary>
public static class MySqlSchemaInitializer
{
    // Shared by every MySqlStorageWriter/MySqlDashboardStore instance in the process, so a writer
    // and a dashboard store pointed at the same database only actually run this once between them,
    // instead of each independently guarding its own duplicate "have I done this already?" check.
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
        // If the host has MySqlConnector instrumentation enabled, every one of these calls would
        // otherwise itself become a span — including GET_LOCK/RELEASE_LOCK, whose statement text
        // is just "SELECT GET_LOCK(@LockName, 30);" with no reference to our actual lock name
        // (that's only ever a parameter VALUE, which instrumentation deliberately doesn't
        // capture), so the exporter's own tag-based self-exclusion can't catch it. Suppressing
        // instrumentation for this whole method is simpler and more complete than trying to
        // pattern-match every possible internal statement.
        using var _ = SuppressInstrumentationScope.Begin();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var lockName = $"PowerTraces_SchemaInit_{schema}";
        await AcquireLockAsync(connection, lockName, ct).ConfigureAwait(false);
        try
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandTimeout = 60;
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS {MySqlIdentifiers.Quote(schema)};";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await EnsureTableAsync(connection, schema, PowerTracesSchema.ResourcesTable, ResourcesDdl(schema), ct).ConfigureAwait(false);
            var spansExisted = !await EnsureTableAsync(connection, schema, PowerTracesSchema.SpansTable, SpansDdl(schema), ct).ConfigureAwait(false);
            await EnsureTableAsync(connection, schema, PowerTracesSchema.LogsTable, LogsDdl(schema), ct).ConfigureAwait(false);
            await EnsureTableAsync(connection, schema, PowerTracesSchema.IgnoreRulesTable, IgnoreRulesDdl(schema), ct).ConfigureAwait(false);
            await EnsureTableAsync(connection, schema, PowerTracesSchema.SettingsTable, SettingsDdl(schema), ct).ConfigureAwait(false);
            await EnsureTableAsync(connection, schema, PowerTracesSchema.PinnedTracesTable, PinnedTracesDdl(schema), ct).ConfigureAwait(false);
            var tracesCreated = await EnsureTableAsync(connection, schema, PowerTracesSchema.TracesTable, TracesDdl(schema), ct).ConfigureAwait(false);
            if (await EnsureTableAsync(connection, schema, PowerTracesSchema.AttributeKeysTable, AttributeKeysDdl(schema), ct).ConfigureAwait(false))
            {
                await MigrateLegacyAttributeTablesAsync(connection, schema, ct).ConfigureAwait(false);
            }

            // Traces written before the search index existed get indexed in the background by the
            // dashboard (see SqlDashboardStore.BackfillSearchIndexAsync); this marks that work as due.
            if (await EnsureSearchTextAsync(connection, schema, ct).ConfigureAwait(false) && spansExisted)
            {
                await using var mark = connection.CreateCommand();
                mark.CommandText = $"""
                    INSERT IGNORE INTO {MySqlIdentifiers.Qualify(schema, PowerTracesSchema.SettingsTable)} (`Key`, `Value`, `UpdatedAt`)
                    VALUES (@Key, @Value, UTC_TIMESTAMP(6));
                    """;
                mark.Parameters.AddWithValue("@Key", PowerTracesSchema.SearchBackfillSettingKey);
                mark.Parameters.AddWithValue("@Value", "{}");
                await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await EnsureIndexAsync(connection, schema, PowerTracesSchema.TracesTable, $"IX_{schema}_Traces_RootName", "(`RootName`, `StartTime`)", ct).ConfigureAwait(false);

            // Serves both loading a trace's spans and the trace list's per-trace EXISTS probes
            // (span name search, service filter) from the index alone.
            await EnsureIndexAsync(connection, schema, PowerTracesSchema.SpansTable, $"IX_{schema}_Spans_TraceId_Cover", "(`TraceId`, `ResourceId`, `Name`)", ct).ConfigureAwait(false);
            await EnsureIndexAsync(connection, schema, PowerTracesSchema.LogsTable, $"IX_{schema}_Logs_ResourceId_Timestamp", "(`ResourceId`, `Timestamp`)", ct).ConfigureAwait(false);

            // Databases created by an earlier version have spans but no trace-summary/attribute-
            // index tables; those get backfilled once, right after the tables are created. The
            // schema-init lock is still held, so no other process's writer has started yet.
            if (tracesCreated && spansExisted)
            {
                await BackfillAsync(connection, schema, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await ReleaseLockAsync(connection, lockName, ct).ConfigureAwait(false);
        }
    }

    private static async Task AcquireLockAsync(MySqlConnection connection, string lockName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GET_LOCK(@LockName, 30);";
        cmd.Parameters.AddWithValue("@LockName", lockName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        // GET_LOCK returns 1 on success, 0 on timeout, NULL on error (e.g. killed connection).
        if (result is null || result is DBNull || Convert.ToInt64(result) != 1)
        {
            throw new InvalidOperationException(
                $"Could not acquire the PowerTraces schema-init lock '{lockName}' " +
                "(another process may be holding it, or a deadlock was detected).");
        }
    }

    private static async Task ReleaseLockAsync(MySqlConnection connection, string lockName, CancellationToken ct)
    {
        // Best-effort: the connection is about to be disposed either way (which would also drop
        // the session-scoped lock), so don't let a release failure mask the real outcome.
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RELEASE_LOCK(@LockName);";
            cmd.Parameters.AddWithValue("@LockName", lockName);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignored — see comment above.
        }
    }

    /// <summary>Creates <paramref name="table"/> (with its indexes inline) if it doesn't already
    /// exist in <paramref name="schema"/>'s database. MySQL's <c>CREATE TABLE IF NOT EXISTS</c>
    /// handles the table itself, but standalone <c>CREATE INDEX</c> has no equivalent
    /// <c>IF NOT EXISTS</c> guard portable across MySQL/MariaDB — so indexes are declared inline in
    /// the <c>CREATE TABLE</c> statement instead (checked for existence up front the same way, via
    /// information_schema, rather than relying on IF NOT EXISTS at all).</summary>
    /// <returns>Whether the table was created by this call.</returns>
    private static async Task<bool> EnsureTableAsync(MySqlConnection connection, string schema, string table, string ddl, CancellationToken ct)
    {
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @Table;";
            checkCmd.Parameters.AddWithValue("@Schema", schema);
            checkCmd.Parameters.AddWithValue("@Table", table);
            var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count > 0)
            {
                return false;
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Adds an index to a table that may predate it — portable across MySQL and MariaDB,
    /// neither of which share a <c>CREATE INDEX IF NOT EXISTS</c>.</summary>
    private static async Task EnsureIndexAsync(MySqlConnection connection, string schema, string table, string index, string columns, CancellationToken ct)
    {
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = @Schema AND table_name = @Table AND index_name = @Index;";
            checkCmd.Parameters.AddWithValue("@Schema", schema);
            checkCmd.Parameters.AddWithValue("@Table", table);
            checkCmd.Parameters.AddWithValue("@Index", index);
            if (Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0)
            {
                return;
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"CREATE INDEX {MySqlIdentifiers.Quote(index)} ON {MySqlIdentifiers.Qualify(schema, table)} {columns};";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Populates the trace summaries from rows an earlier version wrote, with no timeout
    /// (it's proportional to the existing data).</summary>
    private static async Task BackfillAsync(MySqlConnection connection, string schema, CancellationToken ct)
    {
        var spans = MySqlIdentifiers.Qualify(schema, PowerTracesSchema.SpansTable);

        await ExecuteAsync($"""
            INSERT IGNORE INTO {MySqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable)}
                (`TraceId`, `StartTime`, `EndTime`, `HasError`, `SpanCount`, `HasRoot`, `RootName`, `ServiceName`, `RootAttributesJson`)
            SELECT g.`TraceId`, g.`StartTime`, g.`EndTime`, g.`HasError`, g.`SpanCount`,
                   rep.`ParentSpanId` = '', rep.`Name`, r.`ServiceName`, rep.`AttributesJson`
            FROM (
                SELECT `TraceId`, MIN(`StartTime`) AS `StartTime`, MAX(COALESCE(`EndTime`, `StartTime`)) AS `EndTime`,
                       MAX(`HasError`) AS `HasError`, COUNT(*) AS `SpanCount`
                FROM {spans} GROUP BY `TraceId`
            ) g
            JOIN (
                SELECT `TraceId`, `Name`, `AttributesJson`, `ResourceId`, `ParentSpanId`,
                       ROW_NUMBER() OVER (PARTITION BY `TraceId` ORDER BY `ParentSpanId` = '' DESC, `StartTime`, `SpanId`) AS rn
                FROM {spans}
            ) rep ON rep.`TraceId` = g.`TraceId` AND rep.rn = 1
            LEFT JOIN {MySqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable)} r ON r.`Id` = rep.`ResourceId`;
            """).ConfigureAwait(false);

        async Task ExecuteAsync(string sql)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // Every string column is VARCHAR/TEXT, never CHAR — a fixed-length CHAR blank-pads short
    // values (e.g. an empty ParentSpanId on a root span), which would silently break root-span
    // detection (`ParentSpanId = ''`) the same way it did on SQL Server before that was caught.
    //
    // TEXT columns intentionally have no DEFAULT: MySQL/MariaDB support for defaulting TEXT/BLOB
    // columns is version-dependent (MySQL 8.0.13+ only, via a parenthesized expression), and the
    // writer always supplies these fields on every insert anyway, so a default adds risk for no
    // benefit here.
    private static string ResourcesDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable)}} (
            `Id` VARCHAR(36) NOT NULL PRIMARY KEY,
            `ServiceName` VARCHAR(200) NULL,
            `AttributesJson` TEXT NOT NULL,
            `AttributesHash` VARCHAR(32) GENERATED ALWAYS AS (MD5(`AttributesJson`)) STORED,
            `SchemaUrl` VARCHAR(500) NULL,
            `FirstSeenAt` DATETIME(6) NOT NULL,
            `LastSeenAt` DATETIME(6) NOT NULL,
            INDEX `IX_{{schema}}_Resources_AttributesHash` (`AttributesHash`),
            INDEX `IX_{{schema}}_Resources_ServiceName` (`ServiceName`)
        ) ENGINE=InnoDB;
        """;

    private static string SpansDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.SpansTable)}} (
            `Id` VARCHAR(36) NOT NULL PRIMARY KEY,
            `TraceId` VARCHAR(32) NOT NULL,
            `SpanId` VARCHAR(16) NOT NULL,
            `ParentSpanId` VARCHAR(16) NOT NULL DEFAULT '',
            `ResourceId` VARCHAR(36) NULL,
            `Name` VARCHAR(400) NOT NULL DEFAULT '',
            `Kind` VARCHAR(30) NULL,
            `StartTime` DATETIME(6) NOT NULL,
            `EndTime` DATETIME(6) NULL,
            `Status` VARCHAR(30) NULL,
            `StatusMessage` VARCHAR(1000) NULL,
            `HasError` BOOLEAN NOT NULL DEFAULT FALSE,
            `HttpStatus` VARCHAR(10) NULL,
            `AttributesJson` TEXT NOT NULL,
            `ScopeLibraryName` VARCHAR(200) NULL,
            `ScopeLibraryVersion` VARCHAR(50) NULL,
            `EventsJson` TEXT NOT NULL,
            `LinksJson` TEXT NOT NULL,
            INDEX `IX_{{schema}}_Spans_StartTime` (`StartTime`),
            CONSTRAINT `FK_{{schema}}_Spans_Resource` FOREIGN KEY (`ResourceId`)
                REFERENCES {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable)}} (`Id`) ON DELETE SET NULL
        ) ENGINE=InnoDB;
        """;

    private static string LogsDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.LogsTable)}} (
            `Id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            `ResourceId` VARCHAR(36) NULL,
            `Timestamp` DATETIME(6) NOT NULL,
            `ObservedTimestamp` DATETIME(6) NULL,
            `TraceId` VARCHAR(32) NULL,
            `SpanId` VARCHAR(16) NULL,
            `SeverityNumber` INT NOT NULL DEFAULT 0,
            `SeverityText` VARCHAR(20) NULL,
            `Body` TEXT NULL,
            `AttributesJson` TEXT NOT NULL,
            `ScopeLibraryName` VARCHAR(200) NULL,
            `ScopeLibraryVersion` VARCHAR(50) NULL,
            `SchemaUrl` VARCHAR(500) NULL,
            INDEX `IX_{{schema}}_Logs_Timestamp` (`Timestamp`),
            INDEX `IX_{{schema}}_Logs_TraceId` (`TraceId`),
            INDEX `IX_{{schema}}_Logs_SeverityNumber` (`SeverityNumber`),
            CONSTRAINT `FK_{{schema}}_Logs_Resource` FOREIGN KEY (`ResourceId`)
                REFERENCES {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable)}} (`Id`) ON DELETE SET NULL
        ) ENGINE=InnoDB;
        """;

    private static string IgnoreRulesDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.IgnoreRulesTable)}} (
            `Id` VARCHAR(36) NOT NULL PRIMARY KEY,
            `Kind` VARCHAR(10) NOT NULL,
            `Name` VARCHAR(400) NOT NULL,
            `CreatedAt` DATETIME(6) NOT NULL,
            UNIQUE INDEX `UQ_{{schema}}_IgnoreRules_Kind_Name` (`Kind`, `Name`)
        ) ENGINE=InnoDB;
        """;

    private static string SettingsDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.SettingsTable)}} (
            `Key` VARCHAR(100) NOT NULL PRIMARY KEY,
            `Value` LONGTEXT NOT NULL,
            `UpdatedAt` DATETIME(6) NOT NULL
        ) ENGINE=InnoDB;
        """;

    private static string PinnedTracesDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.PinnedTracesTable)}} (
            `TraceId` VARCHAR(32) NOT NULL PRIMARY KEY,
            `RootName` VARCHAR(400) NULL,
            `ServiceName` VARCHAR(200) NULL,
            `StartTime` DATETIME(6) NULL,
            `HasError` BOOLEAN NOT NULL,
            `PinnedAt` DATETIME(6) NOT NULL
        ) ENGINE=InnoDB;
        """;

    private static string TracesDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable)}} (
            `TraceId` VARCHAR(32) NOT NULL PRIMARY KEY,
            `StartTime` DATETIME(6) NOT NULL,
            `EndTime` DATETIME(6) NOT NULL,
            `HasError` BOOLEAN NOT NULL,
            `SpanCount` INT NOT NULL,
            `HasRoot` BOOLEAN NOT NULL,
            `RootName` VARCHAR(400) NULL,
            `ServiceName` VARCHAR(200) NULL,
            `RootAttributesJson` TEXT NULL,
            INDEX `IX_{{schema}}_Traces_StartTime` (`StartTime`, `TraceId`, `HasError`)
        ) ENGINE=InnoDB;
        """;

    private static string AttributeKeysDdl(string schema) => $$"""
        CREATE TABLE {{MySqlIdentifiers.Qualify(schema, PowerTracesSchema.AttributeKeysTable)}} (
            `Key` VARCHAR({{AttributeText.MaxKeyLength}}) NOT NULL,
            `Source` VARCHAR(10) NOT NULL,
            `FirstSeenAt` DATETIME(6) NOT NULL,
            PRIMARY KEY (`Key`, `Source`)
        ) ENGINE=InnoDB;
        """;

    /// <summary>Adds the trace search index (see <see cref="MySqlSearchText"/>) to the trace summary
    /// table if it isn't there: the text column, then its FULLTEXT index — built with stopwords off
    /// (a session setting read when the index is created), since every token is a search term.</summary>
    /// <returns>Whether the column was added by this call.</returns>
    private static async Task<bool> EnsureSearchTextAsync(MySqlConnection connection, string schema, CancellationToken ct)
    {
        var traces = MySqlIdentifiers.Qualify(schema, PowerTracesSchema.TracesTable);
        var index = $"IX_{schema}_Traces_SearchText";

        long columns;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = @Schema AND table_name = @Table AND column_name = @Column;";
            check.Parameters.AddWithValue("@Schema", schema);
            check.Parameters.AddWithValue("@Table", PowerTracesSchema.TracesTable);
            check.Parameters.AddWithValue("@Column", MySqlSearchText.Column);
            columns = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        if (columns == 0)
        {
            await using var add = connection.CreateCommand();
            add.CommandTimeout = 0;
            add.CommandText = $"ALTER TABLE {traces} ADD COLUMN `{MySqlSearchText.Column}` MEDIUMTEXT NULL;";
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = @Schema AND table_name = @Table AND index_name = @Index;";
            check.Parameters.AddWithValue("@Schema", schema);
            check.Parameters.AddWithValue("@Table", PowerTracesSchema.TracesTable);
            check.Parameters.AddWithValue("@Index", index);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
            {
                await using var create = connection.CreateCommand();
                create.CommandTimeout = 0;
                create.CommandText = $"""
                    SET SESSION innodb_ft_enable_stopword = 0;
                    ALTER TABLE {traces} ADD FULLTEXT INDEX `{index}` (`{MySqlSearchText.Column}`);
                    """;
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        return columns == 0;
    }

    /// <summary>Earlier versions indexed every attribute value in two tables. Their keys seed
    /// <see cref="PowerTracesSchema.AttributeKeysTable"/>, then the tables are dropped — attribute
    /// filters read the attribute JSON now.</summary>
    private static async Task MigrateLegacyAttributeTablesAsync(MySqlConnection connection, string schema, CancellationToken ct)
    {
        var keys = MySqlIdentifiers.Qualify(schema, PowerTracesSchema.AttributeKeysTable);
        foreach (var (table, source) in new[] { (PowerTracesSchema.LegacySpanAttributesTable, "traces"), (PowerTracesSchema.LegacyLogAttributesTable, "logs") })
        {
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @Table;";
                check.Parameters.AddWithValue("@Schema", schema);
                check.Parameters.AddWithValue("@Table", table);
                if (Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                {
                    continue;
                }
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"""
                INSERT IGNORE INTO {keys} (`Key`, `Source`, `FirstSeenAt`)
                SELECT DISTINCT `Key`, '{source}', UTC_TIMESTAMP(6) FROM {MySqlIdentifiers.Qualify(schema, table)};
                DROP TABLE {MySqlIdentifiers.Qualify(schema, table)};
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Identifier-quoting helpers shared by the MySQL write and read sides. MySQL quotes
/// identifiers with backticks (not double quotes like PostgreSQL or brackets like SQL Server); to
/// avoid ever depending on the server's <c>lower_case_table_names</c> setting for case-sensitivity
/// of unquoted identifiers, every reference to this library's PascalCase columns/tables is
/// backtick-quoted, matching the PostgreSQL package's "quote everything" discipline. Public so
/// <c>PowerTraces.Dashboard</c>'s MySQL read side can reuse it rather than
/// duplicating the same escaping.</summary>
public static class MySqlIdentifiers
{
    public static string Quote(string identifier) => $"`{identifier}`";

    public static string Qualify(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";
}
