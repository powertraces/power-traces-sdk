using System.Text.Json;

namespace PowerTraces;

/// <summary>Options for exporting traces/logs straight to a database via connection string — no
/// EF Core, no migrations. Tables/collections are created automatically on first use. Which
/// database is fixed by which package's extension method you call (e.g.
/// <c>AddPowerTracesMsSqlExporter</c> from PowerTraces.MSSQL) — these options
/// just carry the connection details common to all of them.</summary>
public sealed class PowerTracesExporterOptions
{
    /// <summary>Connection string for the database the calling package's extension method targets.
    /// Required.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Namespace the library's tables/collections live under — a SQL schema for
    /// SQL Server/PostgreSQL/MySQL, a collection-name prefix for MongoDB. Defaults to "powertraces" so
    /// it stays out of the way of the host application's own data.</summary>
    public string Schema { get; set; } = PowerTracesSchema.DefaultSchemaName;

    /// <summary>Command timeout (seconds) for writes.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Called when a batch fails to write (e.g. DB unreachable). The batch is dropped
    /// either way — this is only a hook for surfacing the failure to the host's own logging.</summary>
    public Action<Exception>? OnExportError { get; set; }

    /// <summary>Request path prefixes to drop from export (case-insensitive), matched against the
    /// span's "http.route" or "url.path" tag. Set this to the dashboard's own mount prefix (e.g.
    /// "/otel") so browsing the dashboard doesn't fill the trace list with the dashboard's own
    /// requests.</summary>
    public IList<string> ExcludePathPrefixes { get; } = [];

    /// <summary>How attribute values that are .NET objects (e.g. a request model set as a tag, or
    /// logged as <c>{@Body}</c>) are written as JSON. Defaults to ASP.NET Core's web settings
    /// (camelCase property names). Pass your app's own options (for example the ones configured
    /// with <c>ConfigureHttpJsonOptions</c>) to store objects exactly the way your API writes them.
    /// Attribute keys, string values (such as a captured raw JSON body) and parsed JSON
    /// (<c>JsonElement</c>/<c>JsonNode</c>) are always stored exactly as given, whatever the case.</summary>
    public JsonSerializerOptions? AttributeJsonOptions { get; set; }

    /// <summary>Adds source snapshots to exceptions recorded by anything else — instrumentation
    /// (e.g. ASP.NET Core's or HttpClient's <c>RecordException</c>) or OpenTelemetry's own
    /// <c>Activity.AddException</c>/<c>RecordException</c>: the exporter reads the frames from the
    /// <c>exception.stacktrace</c> text and attaches the code around each of the app's own lines,
    /// as <see cref="ActivityExceptionExtensions.RecordExceptionWithSource"/> does. Off by default:
    /// it puts source code into the telemetry database. Source comes from PDB-embedded sources
    /// (<c>&lt;EmbedAllSources&gt;true&lt;/EmbedAllSources&gt;</c>) or the files on disk.</summary>
    public bool CaptureSourceSnapshots { get; set; }

    /// <summary>Lines of source before and after the line that ran, for
    /// <see cref="CaptureSourceSnapshots"/>.</summary>
    public int SourceSnapshotLines { get; set; } = 5;

    /// <summary>How many of the app's own frames per exception get a snapshot, for
    /// <see cref="CaptureSourceSnapshots"/>.</summary>
    public int SourceSnapshotFrames { get; set; } = 3;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"{nameof(PowerTracesExporterOptions)}.{nameof(ConnectionString)} must be set.");
        }

        PowerTracesSchema.ValidateSchemaName(Schema);
    }
}
