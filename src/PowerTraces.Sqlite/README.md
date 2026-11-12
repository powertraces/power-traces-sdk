# PowerTraces.Sqlite

SQLite provider for [PowerTraces](https://www.nuget.org/packages/PowerTraces) — exports
[OpenTelemetry](https://opentelemetry.io/) traces and logs to a local SQLite file. No database
server, no collector, no migrations: the file and its tables are created the first time you write.
Good for local development, single-server apps and small deployments.

## Install

```
dotnet add package PowerTraces.Sqlite
```

## Export traces and logs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using PowerTraces.Sqlite;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddPowerTracesSqliteExporter("telemetry/traces.db"))
    .WithLogging(l => l
        .AddPowerTracesSqliteExporter("telemetry/traces.db"));
```

Pass a file path (default `powertraces.db`) or a full connection string
(`"Data Source=telemetry/traces.db;Default Timeout=30"`). Optional settings:

```csharp
tracing.AddPowerTracesSqliteExporter("telemetry/traces.db", o =>
{
    o.Schema = "powertraces";             // default; prefixes table names (SQLite has no schemas)
    o.CommandTimeoutSeconds = 30;         // also how long a write waits for a busy file
    o.ExcludePathPrefixes.Add("/otel");   // don't trace the dashboard's own requests
    o.OnExportError = ex => logger.LogWarning(ex, "PowerTraces export failed");
});
```

## View what you collected

Add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard) and register this
provider's dashboard store alongside it:

```csharp
builder.Services.AddPowerTracesSqliteDashboard("telemetry/traces.db");
// ...
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.Sqlite);
```

Old telemetry is deleted by the dashboard's **Retention** policy (off until you enable it), the
same as every other provider.

## What gets created

Tables prefixed with `powertraces_` (configurable): `Resources`, `Spans`, `Logs`, `Traces`,
`AttributeKeys`, `IgnoreRules`, `Settings` and `PinnedTraces`, plus the indexes the dashboard's
queries need. The file is switched to WAL journaling, so the dashboard reads while the exporter
writes. Timestamps are stored as ISO-8601 text.

The trace list reads `Traces` (one summary row per trace, upserted as spans arrive). Attribute
filters read each row's attribute JSON with `json_extract`, scanning the rows the other filters
leave; `AttributeKeys` lists every key seen, for the dashboard's attribute picker.

## Trade-offs

- SQLite allows one writer at a time. Batches from several app instances sharing one file take
  turns (each waits up to `CommandTimeoutSeconds`). Keep the file on a local disk, not a network
  share.
- Case-insensitive matching (search, *contains* filters) folds ASCII letters only.

If you outgrow it, switch to [`PowerTraces.PostgreSQL`](https://www.nuget.org/packages/PowerTraces.PostgreSQL),
[`PowerTraces.MSSQL`](https://www.nuget.org/packages/PowerTraces.MSSQL),
[`PowerTraces.MySQL`](https://www.nuget.org/packages/PowerTraces.MySQL) or
[`PowerTraces.MongoDb`](https://www.nuget.org/packages/PowerTraces.MongoDb) — same contract, a
real database server.

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
