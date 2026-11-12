# PowerTraces.MySQL

MySQL / MariaDB provider for [PowerTraces](https://www.nuget.org/packages/PowerTraces) — exports
[OpenTelemetry](https://opentelemetry.io/) traces and logs straight to MySQL via a connection
string (using [MySqlConnector](https://mysqlconnector.net/)). No collector, no EF Core, no
migrations project: tables are created automatically the first time you write.

## Install

```
dotnet add package PowerTraces.MySQL
```

## Export traces and logs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using PowerTraces.MySQL;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddPowerTracesMySqlExporter(connectionString))
    .WithLogging(l => l
        .AddPowerTracesMySqlExporter(connectionString));
```

Optional settings (schema/database name, command timeout, path exclusions, export-error callback):

```csharp
tracing.AddPowerTracesMySqlExporter(connectionString, o =>
{
    o.Schema = "powertraces";              // default; tables live in this MySQL database/schema
    o.CommandTimeoutSeconds = 30;
    o.ExcludePathPrefixes.Add("/otel"); // don't trace the dashboard's own requests
    o.OnExportError = ex => logger.LogWarning(ex, "PowerTraces export failed");
});
```

## View what you collected

Add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard) and register this
provider's dashboard store alongside it:

```csharp
builder.Services.AddPowerTracesMySqlDashboard(connectionString);
// ...
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.MySql);
```

## What gets created

Tables in the `powertraces` database (configurable): `Resources`, `Spans`, `Logs`, `Traces`,
`AttributeKeys`, `IgnoreRules`, `Settings` and `PinnedTraces` — plus the indexes the dashboard's
queries need. Creation is idempotent and guarded by `GET_LOCK`, so it's safe for multiple instances
to start at once.

The trace list reads `Traces` (one summary row per trace, upserted as spans arrive) rather than
grouping `Spans` on every request, so it stays in the milliseconds as the tables grow; list totals
stop counting at 10,000 (shown as "10,000+"). Attribute values live only in each row's attribute
JSON, which attribute filters read — there's no per-attribute index, so a filter scans the rows
the other filters (time range, service, errors) leave. `AttributeKeys` lists every attribute key
seen, for the dashboard's attribute picker.

**Upgrading a database written by an earlier version:** missing tables are created (and the trace
summaries backfilled from existing spans) once, the first time the new version starts. The
`SpanAttributes`/`LogAttributes` index tables earlier versions wrote are dropped after their keys
are copied into `AttributeKeys`.

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
