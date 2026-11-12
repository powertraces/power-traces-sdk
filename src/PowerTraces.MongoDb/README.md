# PowerTraces.MongoDb

MongoDB provider for [PowerTraces](https://www.nuget.org/packages/PowerTraces) — exports
[OpenTelemetry](https://opentelemetry.io/) traces and logs straight to MongoDB via a connection
string (using the official `MongoDB.Driver`). No collector, no EF Core: collections and indexes
are created automatically the first time you write.

## Install

```
dotnet add package PowerTraces.MongoDb
```

## Export traces and logs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using PowerTraces.MongoDb;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddPowerTracesMongoDbExporter(connectionString))
    .WithLogging(l => l
        .AddPowerTracesMongoDbExporter(connectionString));
```

Optional settings (collection prefix, command timeout, path exclusions, export-error callback):

```csharp
tracing.AddPowerTracesMongoDbExporter(connectionString, o =>
{
    o.Schema = "powertraces";              // default; collection-name prefix (powertraces_Spans, etc.)
    o.CommandTimeoutSeconds = 30;
    o.ExcludePathPrefixes.Add("/otel"); // don't trace the dashboard's own requests
    o.OnExportError = ex => logger.LogWarning(ex, "PowerTraces export failed");
});
```

## View what you collected

Add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard) and register this
provider's dashboard store alongside it:

```csharp
builder.Services.AddPowerTracesMongoDbDashboard(connectionString);
// ...
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.MongoDb);
```

## What gets created

Collections prefixed with `powertraces_` (configurable): `Resources`, `Spans`, `Logs`, `Traces`,
`IgnoreRules`, `AttributeKeys` (every attribute key seen, for the dashboard's attribute picker),
`Settings`, `PinnedTraces` and `Counters` (backs sequential log ids, so log records look identical across every
provider). Search uses escaped regex filters; resource deduplication is a single atomic upsert.

Each `Traces` document is upserted as spans arrive and carries the trace's summary plus the distinct
span names, resource ids and attribute key/value pairs of all its spans, so every trace-list filter
is one indexed query on one collection; log documents carry an indexed attribute array the same way.
Data written by an earlier version is migrated once, on first start (recorded in `Counters`).

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
