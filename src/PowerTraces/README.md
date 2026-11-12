# PowerTraces

The base package for exporting [OpenTelemetry](https://opentelemetry.io/) traces and logs straight
to a database via a connection string — no collector, no EF Core. Tables/collections are created
automatically on first use.

This package is database-agnostic on its own — it defines the shapes (`IStorageWriter`,
`IDashboardStore`) and the shared exporter plumbing. To actually write anywhere, add **one** of the
provider packages:

| Package | Database |
|---|---|
| [`PowerTraces.MSSQL`](https://www.nuget.org/packages/PowerTraces.MSSQL) | SQL Server |
| [`PowerTraces.PostgreSQL`](https://www.nuget.org/packages/PowerTraces.PostgreSQL) | PostgreSQL |
| [`PowerTraces.MongoDb`](https://www.nuget.org/packages/PowerTraces.MongoDb) | MongoDB |
| [`PowerTraces.MySQL`](https://www.nuget.org/packages/PowerTraces.MySQL) | MySQL / MariaDB |
| [`PowerTraces.Sqlite`](https://www.nuget.org/packages/PowerTraces.Sqlite) | SQLite (a local file, no database server) |

And to see what's collected, add [`PowerTraces.Dashboard`](https://www.nuget.org/packages/PowerTraces.Dashboard)
— an embedded traces/logs viewer UI you mount with one line.

## Quick start

```csharp
// after: dotnet add package PowerTraces.MSSQL
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddPowerTracesMsSqlExporter(connectionString))
    .WithLogging(l => l.AddPowerTracesMsSqlExporter(connectionString));
```

That's it — spans and logs start landing in SQL Server under the `powertraces` schema, tables created
automatically.

## Extending to another database

Implement `IStorageWriter` (write side) and, if you also want dashboard support, `IDashboardStore`
(read side) — both defined in this package. Then either call the generic entry point directly:

```csharp
tracing.AddPowerTracesExporter(new MyStorageWriter(options), options);
```

or package it up the same way the built-in providers do: a small `AddPowerTracesMyDbExporter(...)`
extension method, plus `AddPowerTracesMyDbDashboard(...)` registering your `IDashboardStore` under a
DI key so `PowerTraces.Dashboard` can resolve it.

Optional pieces the built-in providers all implement:

- `IIgnoreRuleSource` on the writer (and `IIgnoreRuleStore` on the dashboard store) — the exporter
  then applies ignore rules created from the dashboard. Read them through `IgnoreRuleCache` so an
  export doesn't cost a query.
- `IStorageWriter.DeleteTracesAsync` — purges spans already written for a trace whose root turns
  out to be ignored.
- `IDashboardStore.GetOverviewAsync` — the Overview page's charts.

For another **SQL** database, most of this is already written: derive from `SqlDashboardStore` with
your own `SqlDialect` (quoting, paging, LIKE escaping, time bucketing) and reuse `SqlIgnoreRules`;
only the writer and the DDL are yours to write. `TraceSummary.FromBatch` gives you the per-trace
summary rows the shared queries read, and `SqlAttributeKeys.RecordAsync` keeps the attribute-key
list up to date. Your dialect's `JsonAttributeText` is how attribute filters read the JSON.

## License

Free for any use, including commercial use, under the PowerTraces Free License (LICENSE.txt in this package): you may use it and ship it unmodified inside your applications, but not modify the code or publish changed copies.
