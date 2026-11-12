# PowerTraces.PostgreSQL

PostgreSQL provider for [PowerTraces](https://www.nuget.org/packages/PowerTraces) — exports
[OpenTelemetry](https://opentelemetry.io/) traces and logs straight to PostgreSQL via a connection
string (using [Npgsql](https://www.npgsql.org/)). No collector, no EF Core, no migrations project:
tables are created automatically the first time you write.

## Install

```
dotnet add package PowerTraces.PostgreSQL
```

## Export traces and logs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using PowerTraces.PostgreSQL;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddPowerTracesPostgreSqlExporter(connectionString))
    .WithLogging(l => l
        .AddPowerTracesPostgreSqlExporter(connectionString));
```

Optional settings (schema name, command timeout, path exclusions, export-error callback):

```csharp
tracing.AddPowerTracesPostgreSqlExporter(connectionString, o =>
{
    o.Schema = "powertraces";              // default; tables live under this Postgres schema
    o.CommandTimeoutSeconds = 30;
    o.ExcludePathPrefixes.Add("/otel"); // don't trace the dashboard's own requests
    o.OnExportError = ex => logger.LogWarning(ex, "PowerTraces export failed");
});
```

## View what you collected

Add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard) and register this
provider's dashboard store alongside it:

```csharp
builder.Services.AddPowerTracesPostgreSqlDashboard(connectionString);
// ...
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.PostgreSql);
```

## What gets created

Tables under the `powertraces` Postgres schema (configurable): `Resources`, `Spans`, `Logs`,
`Traces`, `AttributeKeys`, `IgnoreRules`, `Settings` and `PinnedTraces` — plus the indexes the
dashboard's queries need. Creation is idempotent and lock-guarded (`pg_advisory_xact_lock`), so it's
safe for multiple instances to start at once. Rows are written with binary `COPY`.

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
