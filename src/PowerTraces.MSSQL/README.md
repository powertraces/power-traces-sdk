# PowerTraces.MSSQL

SQL Server provider for [PowerTraces](https://www.nuget.org/packages/PowerTraces) — exports
[OpenTelemetry](https://opentelemetry.io/) traces and logs straight to SQL Server via a connection
string. No collector, no EF Core, no migrations project: tables are created automatically the
first time you write.

## Install

```
dotnet add package PowerTraces.MSSQL
```

## Export traces and logs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using PowerTraces.MSSQL;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddPowerTracesMsSqlExporter(connectionString))
    .WithLogging(l => l
        .AddPowerTracesMsSqlExporter(connectionString));
```

Optional settings (schema name, command timeout, path exclusions, export-error callback):

```csharp
tracing.AddPowerTracesMsSqlExporter(connectionString, o =>
{
    o.Schema = "powertraces";              // default; tables live under this SQL schema
    o.CommandTimeoutSeconds = 30;
    o.ExcludePathPrefixes.Add("/otel"); // don't trace the dashboard's own requests
    o.OnExportError = ex => logger.LogWarning(ex, "PowerTraces export failed");
});
```

## View what you collected

Add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard) and register this
provider's dashboard store alongside it:

```csharp
builder.Services.AddPowerTracesMsSqlDashboard(connectionString);
// ...
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.SqlServer);
```

## Connections

Unless your connection string sets its own `Application Name`, PowerTraces connects as
`PowerTraces`. SqlClient pools connections per exact connection string, so this keeps the exporter
and dashboard in their own pool instead of sharing your application's, and makes their sessions
easy to find:

```sql
SELECT program_name, COUNT(*) AS sessions
FROM sys.dm_exec_sessions
WHERE is_user_process = 1
GROUP BY program_name;
```

The exporters use at most one connection each at a time; the dashboard is capped by
`MaxConcurrentQueries` (see the PowerTraces.Dashboard README).

## What gets created

Tables under the `powertraces` SQL schema (configurable): `Resources`, `Spans`, `Logs`, `Traces`,
`AttributeKeys`, `IgnoreRules`, `Settings` and `PinnedTraces` — plus the indexes the dashboard's
queries need. Creation is idempotent and lock-guarded (`sp_getapplock`), so it's safe for multiple
instances to start at once. Spans and logs are written with `SqlBulkCopy`.

The trace list reads `Traces` (one summary row per trace, upserted as spans arrive) rather than
grouping `Spans` on every request, so it stays in the milliseconds as the tables grow; list totals
stop counting at 10,000 (shown as "10,000+"). Attribute values live only in each row's attribute
JSON, which attribute filters read — there's no per-attribute index, so a filter scans the rows
the other filters (time range, service, errors) leave. `AttributeKeys` lists every attribute key
seen, for the dashboard's attribute picker. Attribute filters use `JSON_VALUE` with a parameterized path, which needs
SQL Server 2017+ (database compatibility level 130+).

**Upgrading a database written by an earlier version:** missing tables are created (and the trace
summaries backfilled from existing spans) once, the first time the new version starts. The
`SpanAttributes`/`LogAttributes` index tables earlier versions wrote are dropped after their keys
are copied into `AttributeKeys`.

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
