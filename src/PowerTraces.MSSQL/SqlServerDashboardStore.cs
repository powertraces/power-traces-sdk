using PowerTraces.Dashboard;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MSSQL;

/// <summary>Reads the dashboard's data straight from SQL Server — the shared
/// <see cref="SqlDashboardStore"/> queries with <see cref="SqlServerDialect"/>. Schema creation is
/// delegated to <see cref="SqlServerSchemaInitializer"/>, the same DDL the writer uses.</summary>
public sealed class SqlServerDashboardStore(PowerTracesDashboardOptions options)
    : SqlDashboardStore(SqlServerDialect.Instance, options)
{
    // Called on every /api request (cheap once warm) — SqlServerSchemaInitializer guards its own
    // idempotency, shared with SqlServerStorageWriter for the same database.
    public override Task EnsureSchemaAsync(CancellationToken ct) =>
        SqlServerSchemaInitializer.EnsureCreatedAsync(Options.ConnectionString, Options.Schema, ct);
}
