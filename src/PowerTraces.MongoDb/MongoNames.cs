using PowerTraces.Dashboard;

namespace PowerTraces.MongoDb;

/// <summary>Collection-name helpers shared by the exporter's write side and the dashboard's read
/// side, so both agree on where telemetry lives in MongoDB without either depending on the
/// other's internals (the collection-naming equivalent of <see cref="PowerTracesSchema.Qualify"/> for
/// SQL Server). <see cref="PowerTracesExporterOptions.Schema"/>/<see cref="PowerTracesDashboardOptions"/>'s
/// Schema is used as a collection-name prefix rather than a SQL schema.</summary>
public static class MongoNames
{
    public static string Resources(string schema) => $"{schema}_{PowerTracesSchema.ResourcesTable}";

    public static string Spans(string schema) => $"{schema}_{PowerTracesSchema.SpansTable}";

    public static string Logs(string schema) => $"{schema}_{PowerTracesSchema.LogsTable}";

    /// <summary>One <see cref="TraceDocument"/> per trace, maintained on write — what the trace
    /// list queries.</summary>
    public static string Traces(string schema) => $"{schema}_{PowerTracesSchema.TracesTable}";

    public static string IgnoreRules(string schema) => $"{schema}_{PowerTracesSchema.IgnoreRulesTable}";

    public static string AttributeKeys(string schema) => $"{schema}_{PowerTracesSchema.AttributeKeysTable}";

    public static string Settings(string schema) => $"{schema}_{PowerTracesSchema.SettingsTable}";

    public static string PinnedTraces(string schema) => $"{schema}_{PowerTracesSchema.PinnedTracesTable}";

    /// <summary>Holds the auto-increment counter(s) used to keep <c>LogDocument.Id</c> a
    /// sequential <see langword="long"/> (the public dashboard DTOs require that, so log rows
    /// can't just use a Mongo ObjectId) — one document per counter, e.g. <c>{ _id: "Logs", value:
    /// &lt;long&gt; }</c>.</summary>
    public static string Counters(string schema) => $"{schema}_Counters";
}
