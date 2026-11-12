# PowerTraces.Dashboard

An embedded traces/logs viewer UI + query API for [PowerTraces](https://www.nuget.org/packages/PowerTraces)
— mount it in your ASP.NET Core app with one line. The whole SPA is embedded in the package, so
there's nothing extra to publish or build (Node/Yarn aren't required at runtime).

This package doesn't know about any specific database — it resolves an `IDashboardStore` from DI,
keyed by which provider you ask for. Register the provider(s) you actually use:

| Package | `PowerTracesDatabaseProvider` |
|---|---|
| [`PowerTraces.MSSQL`](https://www.nuget.org/packages/PowerTraces.MSSQL) | `SqlServer` |
| [`PowerTraces.PostgreSQL`](https://www.nuget.org/packages/PowerTraces.PostgreSQL) | `PostgreSql` |
| [`PowerTraces.MongoDb`](https://www.nuget.org/packages/PowerTraces.MongoDb) | `MongoDb` |
| [`PowerTraces.MySQL`](https://www.nuget.org/packages/PowerTraces.MySQL) | `MySql` |
| [`PowerTraces.Sqlite`](https://www.nuget.org/packages/PowerTraces.Sqlite) | `Sqlite` |

## Install

```
dotnet add package PowerTraces.Dashboard
dotnet add package PowerTraces.MSSQL   # or whichever provider you're already exporting to
```

## Mount it

```csharp
using PowerTraces;
using PowerTraces.Dashboard;
using PowerTraces.MSSQL;

// 1. Register a store for the database you're actually using.
builder.Services.AddPowerTracesMsSqlDashboard(connectionString);

var app = builder.Build();

// 2. Mount the UI + API at a path of your choosing.
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.SqlServer);
```

Open `/otel`:

- **Overview** — requests, error rate, latency and log volume over the last hour/6h/24h/7d, the
  busiest operations with their error rates, recent failures and the latest logs.
- **Traces** and **Logs** — search, time range, service, severity/error and attribute filters, and
  extra attribute columns.
- **Trace detail** — the span tree on a time axis (foldable, searchable, critical path
  highlighted) beside the selected span's details, a hotspots view (where it failed, time by
  service, slowest spans), and **Keep forever** to exempt the trace from retention.
- **Ignore rules** — stop exporting noisy traces or spans without a redeploy.
- **Retention** — how long to keep traces and logs, whether failures are kept regardless, the
  pinned traces, and the last cleanup's results.

The UI follows the OS light/dark setting, or can be pinned to either from the sidebar.

## Retention cleanup

Cleanup follows the policy saved on the Retention page (off until enabled) and runs in the app that
mounts the dashboard: at startup, then hourly. When several apps mount a dashboard for the same
database, one is enough — turn it off in the others:

```csharp
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.SqlServer,
    configureUi: o => o.RetentionSweepInterval = null);
```

## Basic Auth (optional)

```csharp
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.SqlServer,
    username: builder.Configuration["Dashboard:Username"],
    password: builder.Configuration["Dashboard:Password"]);
```

Leave both `null` (the default) to leave the dashboard unauthenticated — e.g. because your app
already puts it behind its own auth or network boundary.

## Database connections

The dashboard uses at most `MaxConcurrentQueries` connections at a time (default 8); further
requests wait without holding one, so it can't drain the pool your application uses. Requests the
browser abandons — a superseded search, leaving the page — are cancelled on the server and free
their connection immediately.

```csharp
builder.Services.AddPowerTracesMsSqlDashboard(connectionString, o => o.MaxConcurrentQueries = 4);
```

## Multiple databases at once

Call `MapPowerTracesDashboard` more than once — each with its own prefix and provider — to browse
several databases' telemetry from one app:

```csharp
builder.Services.AddPowerTracesMsSqlDashboard(sqlConnectionString);
builder.Services.AddPowerTracesMongoDbDashboard(mongoConnectionString);

app.MapPowerTracesDashboard("/otel/prod", PowerTracesDatabaseProvider.SqlServer);
app.MapPowerTracesDashboard("/otel/staging", PowerTracesDatabaseProvider.MongoDb);
```

## Excluding the dashboard's own traffic

Add the dashboard's own mount prefix to the exporter's `ExcludePathPrefixes` so browsing it doesn't
fill the trace list with its own requests:

```csharp
tracing.AddPowerTracesMsSqlExporter(connectionString, o => o.ExcludePathPrefixes.Add("/otel"));
```

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
