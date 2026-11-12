# PowerTraces

A .NET library that exports [OpenTelemetry](https://opentelemetry.io/) traces and logs straight to
a database via a connection string, plus an embedded UI to browse them — no collector process, no
EF Core, no migrations. Point it at SQL Server, PostgreSQL, MongoDB, or MySQL and it creates its
own tables/collections on first use.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddPowerTracesMsSqlExporter(connectionString))
    .WithLogging(l => l.AddPowerTracesMsSqlExporter(connectionString));

builder.Services.AddPowerTracesMsSqlDashboard(connectionString);
app.MapPowerTracesDashboard("/otel", PowerTracesDatabaseProvider.SqlServer);
```

That's the whole setup. Open `/otel` for an overview of traffic, errors and latency, a waterfall
trace view (critical-path highlighting, grouped/humanized attributes, sensitive-value masking), a
filterable log stream, ignore rules and retention — in a light or dark theme.

## Packages

| Package | What it is |
|---|---|
| [`PowerTraces`](src/PowerTraces) | Base package — `IStorageWriter`/`IDashboardStore` abstractions, shared exporter plumbing. Everything else depends on this. |
| [`PowerTraces.MSSQL`](src/PowerTraces.MSSQL) | SQL Server provider |
| [`PowerTraces.PostgreSQL`](src/PowerTraces.PostgreSQL) | PostgreSQL provider |
| [`PowerTraces.MongoDb`](src/PowerTraces.MongoDb) | MongoDB provider |
| [`PowerTraces.MySQL`](src/PowerTraces.MySQL) | MySQL / MariaDB provider |
| [`PowerTraces.Sqlite`](src/PowerTraces.Sqlite) | SQLite provider — a local file, no database server |
| [`PowerTraces.Dashboard`](src/PowerTraces.Dashboard) | Embedded traces/logs viewer UI + query API |

You need the base package (pulled in transitively), **one** provider package for whatever database
you use, and the dashboard package if you want the UI. Each package has its own README with
install instructions and a quick-start snippet.

Don't see your database? Implement `IStorageWriter` (and `IDashboardStore` for dashboard support)
yourself — both are public interfaces in the base package, and the four built-in providers are
ordinary implementations of the same contract, not special-cased internally.

## Architecture

```
PowerTraces (abstractions: IStorageWriter, IDashboardStore, options, DTOs)
  ├── PowerTraces.MSSQL       (SqlServerStorageWriter + SqlServerDashboardStore)
  ├── PowerTraces.PostgreSQL  (PostgreSqlStorageWriter + PostgreSqlDashboardStore)
  ├── PowerTraces.MongoDb     (MongoStorageWriter + MongoDashboardStore)
  ├── PowerTraces.MySQL       (MySqlStorageWriter + MySqlDashboardStore)
  ├── PowerTraces.Sqlite      (SqliteStorageWriter + SqliteDashboardStore — a local file)
  └── PowerTraces.Dashboard   (depends only on the base package — resolves whichever
                             IDashboardStore a provider package registered via keyed DI)
```

Each provider package owns both the write side (used by the OpenTelemetry exporter) and the read
side (used by the dashboard) for its database. `PowerTraces.Dashboard` never references a specific
provider package — it resolves an `IDashboardStore` from dependency injection, keyed by a
`PowerTracesDatabaseProvider` enum value, which is how you can mount dashboards for more than one
database in the same app.

## Ignoring noisy telemetry

Health checks, polling endpoints and chatty spans can be switched off from the dashboard (a trace's
or span's **Ignore** button, or the **Ignore rules** page) — no redeploy. Every provider stores the
rules in its own database, and the exporter applies them before writing:

- a **trace rule** drops every span of a trace whose root span has that name — including child spans
  that finished (and were exported) before the root did, which are purged once the root arrives;
- a **span rule** drops spans with that name and keeps the rest of their trace.

Rules take effect on the next export in the process that created them, and within 30 seconds in
other processes writing to the same database. Telemetry already stored is kept.
`ExcludePathPrefixes` works the same way for a request path: excluding the root excludes its whole
trace.

## Exceptions with source snapshots

Record an exception with its stack and the code around each line of your app it passed through:

```csharp
try { ... }
catch (Exception ex)
{
    Activity.Current.RecordExceptionWithSource(ex, snapshotLines: 5, maxFrames: 3);
    throw;
}
```

This writes the standard OpenTelemetry `exception` event (`exception.type`, `exception.message`,
`exception.stacktrace`, and the span marked as failed), plus `exception.details`: the exception and
its inner exceptions, each with its stack frames. Up to `maxFrames` frames of your own code per
exception also get the `snapshotLines` lines of source before and after the line that ran. The
trace page shows the frames with the code: the line that threw is marked, framework frames are
folded away, and inner exceptions are listed as "Caused by".

Source is read from the sources embedded in your PDB, or from the `.cs` file if it exists on disk.
To get snapshots in production, where source files usually aren't deployed, embed the sources:

```xml
<PropertyGroup>
  <EmbedAllSources>true</EmbedAllSources>
</PropertyGroup>
```

Without either, the frames are still recorded, just without code.

To add snapshots to exceptions that other code records (ASP.NET Core or HttpClient
instrumentation, OpenTelemetry's own `RecordException`), turn on `CaptureSourceSnapshots`. The
exporter then reads the frames from each `exception.stacktrace` and attaches the code in the same
way:

```csharp
tracing.AddPowerTracesMsSqlExporter(connectionString, o =>
{
    o.CaptureSourceSnapshots = true; // off by default
    o.SourceSnapshotLines = 5;
    o.SourceSnapshotFrames = 3;
});
```

> **Note:** snapshots store your source code in the telemetry database, so anyone who can read
> that database or open the dashboard can see it. Both ways are opt-in for that reason. The work
> runs only when an exception is recorded, never for spans that don't fail.

## Attribute key casing

Attribute keys are stored exactly as given, mixed case included, and so are string values: a
request body captured as its raw JSON text keeps every key as the client sent it. Prefer that when
you need the body exactly as received.

Values that are .NET objects (a bound request model set as a tag, or logged as `{@Body}`) have
no original JSON left to keep, so they are serialized. By default this uses ASP.NET Core's web
settings (camelCase property names, `[JsonPropertyName]` respected, dictionary keys untouched). To
store them exactly as your API writes them, pass your app's own options:

```csharp
tracing.AddPowerTracesMsSqlExporter(connectionString, o => o.AttributeJsonOptions = myApiJsonOptions);
```

## Retention

Every provider can delete old telemetry automatically. The policy is set on the dashboard's
**Retention** page and stored in the provider's own database, so all apps sharing that database
follow it:

- how many days to keep **traces** and **logs** (separately);
- whether to keep **failed traces** and **error logs** regardless of age;
- **pinned traces** — a trace's **Keep forever** button exempts it, and the logs that belong to it,
  from retention until it's unpinned.

Retention is off until someone enables it. The app that mounts the dashboard runs the cleanup at startup and then hourly
(`RetentionSweepInterval` in `MapPowerTracesDashboard`'s `configureUi`; `null` turns it off in
that app), and **Run cleanup now** runs one on demand. Expired rows are found through the time
indexes and deleted in batches of 500, so cleanup never holds a long lock on tables the exporter is
writing to.

## Performance

The trace list reads a per-trace summary maintained on write instead of grouping every span per
request, only the filters in use reach the query, and totals stop counting at 10,000. On SQL Server
with 1.2M spans, the trace list went from 2.6 s to 7 ms.

### Trace search

The Traces page has one search box. It finds traces by any word, value or field. JSON request
and response bodies are searched field by field, e.g. `http.request.body.customer.email = sara@acme.io`.

- `declined` — a word anywhere in the trace (span names, attributes, events, bodies); three
  characters or more match by prefix.
- `"card declined"` — every word of the phrase; `-health` — must not contain.
- `key=value`, `key!=value`, `key~words` — a field equals / doesn't equal a value / has words.
  Keys include `span.name` and paths inside JSON attributes.
- Endpoint, status, service and duration filters in the side panel, with counts.
- Each result shows which field matched.

Searches go through a search index written next to the spans, so a search costs about as much as
the number of matching traces, not the number of spans in the time range. On 100,000 traces, a
search for a customer's email inside the request body went from about 1 s (PostgreSQL) and 22 s
(MariaDB) to under 40 ms. How each database keeps the index:

| Database | Search index |
|---|---|
| PostgreSQL | a `tsvector` column on the trace summary, GIN-indexed |
| MySQL / MariaDB | a FULLTEXT-indexed column on the trace summary |
| SQL Server | a `SearchTerms` table clustered on (term, time, trace), page-compressed, trace ids as `BINARY(16)` |
| SQLite | a `SearchTerms` table clustered on (term, time, trace) |
| MongoDB | a multikey-indexed term array on each trace document |

Large span attribute and event JSON is stored Brotli-compressed, which offsets most of the index.
On 100,000 test traces, the whole database was 17% larger than with the previous version on
PostgreSQL, 5% on MariaDB and 4% on SQL Server. Because of the compression, read span attributes
through the dashboard, not with the database's JSON functions.

On SQL Server, a deleted trace's search terms are removed by the retention sweep instead of with the
trace; searches never match them. Traces written before the index existed are indexed in the
background, newest first, by the app that mounts the dashboard.

Log attribute filters still read each log row's attribute JSON, so narrow them with a time range,
service or severity on large tables. The attribute picker reads a small `AttributeKeys` table that
lists every key ever written, including fields found inside JSON bodies.

## Upgrading from Acumency

This project was previously published as `Acumency.*`. Package IDs, namespaces and APIs moved from
`Acumency` to `PowerTraces` — e.g. `Acumency.MSSQL` → `PowerTraces.MSSQL`,
`AddAcumencyMsSqlExporter` → `AddPowerTracesMsSqlExporter`, `MapAcumencyDashboard` →
`MapPowerTracesDashboard`, `AcumencyDatabaseProvider` → `PowerTracesDatabaseProvider`.

The default schema (collection prefix for MongoDB) changed from `acumency` to `powertraces`. To keep
reading and writing existing data, set it explicitly on both sides:

```csharp
tracing.AddPowerTracesMsSqlExporter(connectionString, o => o.Schema = "acumency");
builder.Services.AddPowerTracesMsSqlDashboard(connectionString, o => o.Schema = "acumency");
```

The new tables are created, and the trace summaries backfilled from existing data, automatically
on the first start.

## Repository layout

```
src/
  PowerTraces/               core library
  PowerTraces.MSSQL/         SQL Server provider
  PowerTraces.PostgreSQL/    PostgreSQL provider
  PowerTraces.MongoDb/       MongoDB provider
  PowerTraces.MySQL/         MySQL/MariaDB provider
  PowerTraces.Sqlite/        SQLite provider (a local file, no database server)
  PowerTraces.Dashboard/     dashboard UI + API (React SPA in ClientWeb/, embedded at build time)
samples/
  PowerTraces.SampleApp/     minimal end-to-end example (SQL Server)
```

## Building

```
dotnet build PowerTraces.sln
```

The dashboard's React SPA is checked into `src/PowerTraces.Dashboard/wwwroot/` as a built artifact, so
a plain `dotnet build`/`dotnet pack` doesn't need Node/Yarn installed. To rebuild it after changing
`ClientWeb/`:

```
cd src/PowerTraces.Dashboard/ClientWeb
yarn install
yarn build
```

## Trying it locally

`docker-compose.yml` at the repo root starts a local SQL Server instance matching the sample app's
default connection string:

```
docker compose up -d
dotnet run --project samples/PowerTraces.SampleApp
```

## Packing

```
dotnet pack PowerTraces.sln -c Release
```

Produces `.nupkg`/`.snupkg` files for the seven packages under `nupkgs/` (only the packages under
`src/` — the sample app isn't packed).

## Publishing

[`.github/workflows/publish.yml`](.github/workflows/publish.yml) builds the dashboard frontend,
packs every package, and pushes to nuget.org — triggered manually from the Actions tab (never on
push/PR, since publishing isn't reversible). Optionally pass a `version` to publish something other
than whatever's committed in `src/Directory.Build.props`, or `dry_run: true` to pack and upload the
`.nupkg`/`.snupkg` files as a workflow artifact without pushing.

Authentication is [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
(OIDC) — no long-lived NuGet API key is stored in this repo. One-time setup:

1. On nuget.org: sign in → your username → **Trusted Publishing** → add a policy with
   **Repository Owner**/**Repository** set to this repo and **Workflow File** set to `publish.yml`
   (the filename only, not the full path). Leave **Environment** blank, or set it to `release` to
   match the workflow's `environment: release`.
2. In this repo: add a `NUGET_USER` repository secret (Settings → Secrets and variables → Actions)
   set to your nuget.org username (profile name, not your email).

A freshly created policy on a private repo starts in a 7-day "pending full activation" window —
the first successful publish makes it permanent.

To hide older releases, run the workflow with `action: unlist`. It builds and pushes nothing: it
unlists every listed version of this repo's packages except the newest, so only that one shows on
nuget.org; anyone who pins an older version can still restore it. `keep_version` keeps a specific
version instead (`none` unlists them all), and `package_ids` picks other packages, such as the old
`Acumency.*` ones. With `dry_run: true` it only lists what it would unlist, in the run summary.

Unlisting uses the same Trusted Publishing policy, which needs the **Unlist or relist package
versions** scope. Packages owned by another nuget.org account aren't covered by it: add an API key
from that account, with the **Unlist package** scope, as the `NUGET_UNLIST_API_KEY` secret.

## License

[PowerTraces Free License](LICENSE): free for any use, including commercial use. You may use the library and ship it unmodified inside your applications, configure it, and implement its public interfaces in your own code. You may not modify it, publish changed copies, or redistribute it on its own. Versions before 2.0.0 are MIT.
