namespace PowerTraces.Dashboard;

/// <summary>Which storage backend the dashboard reads from — the key each provider package's
/// <c>AddPowerTracesXxxDashboard(...)</c> registers its <see cref="Query.IDashboardStore"/> under, and
/// that <c>MapPowerTracesDashboard(prefix, provider)</c> resolves it by. SQL Server, PostgreSQL,
/// MongoDB, MySQL and SQLite ship built in; support another backend by
/// implementing <see cref="Query.IDashboardStore"/> yourself and registering it under your own DI
/// key with <c>services.AddKeyedSingleton&lt;IDashboardStore&gt;(myKey, ...)</c>.</summary>
public enum PowerTracesDatabaseProvider
{
    SqlServer,
    PostgreSql,
    MongoDb,
    MySql,
    Sqlite,
}
