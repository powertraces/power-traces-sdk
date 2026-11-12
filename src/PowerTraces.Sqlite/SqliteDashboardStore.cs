using PowerTraces.Dashboard;
using PowerTraces.Storage.Sql;

namespace PowerTraces.Sqlite;

/// <summary>Reads the dashboard's data from the SQLite file — the shared
/// <see cref="SqlDashboardStore"/> queries with <see cref="SqliteDialect"/>. Table creation is
/// delegated to <see cref="SqliteSchemaInitializer"/>, the same DDL the writer uses.</summary>
public sealed class SqliteDashboardStore(PowerTracesDashboardOptions options)
    : SqlDashboardStore(SqliteDialect.Instance, options)
{
    public override Task EnsureSchemaAsync(CancellationToken ct) =>
        SqliteSchemaInitializer.EnsureCreatedAsync(Options.ConnectionString, Options.Schema, ct);
}
