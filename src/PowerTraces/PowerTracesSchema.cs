using System.Text.RegularExpressions;

namespace PowerTraces;

/// <summary>Table/schema name constants shared by the exporter (writes) and the dashboard (reads),
/// so both sides agree on where telemetry lives without either one depending on the other's internals.</summary>
public static partial class PowerTracesSchema
{
    public const string DefaultSchemaName = "powertraces";

    public const string ResourcesTable = "Resources";
    public const string SpansTable = "Spans";
    public const string LogsTable = "Logs";
    public const string IgnoreRulesTable = "IgnoreRules";

    /// <summary>Key/value settings edited from the dashboard (the retention policy and the last
    /// retention run, as JSON).</summary>
    public const string SettingsTable = "Settings";

    /// <summary>Traces excluded from retention.</summary>
    public const string PinnedTracesTable = "PinnedTraces";

    /// <summary>One row per trace, maintained on write (start/end, error flag, span count, root
    /// span name/service/attributes) — lets the trace list page through an index instead of
    /// grouping every span on every request.</summary>
    public const string TracesTable = "Traces";

    /// <summary>One row per distinct (attribute key, source) ever written — what the dashboard's
    /// attribute picker lists. Attribute values themselves are only stored in each row's
    /// attribute JSON, which is what attribute filters search.</summary>
    public const string AttributeKeysTable = "AttributeKeys";

    /// <summary>The trace search index: one row per (search term, trace) — see
    /// <see cref="Search.SearchTerms"/>. Every trace search is a lookup on its (Term, StartTime)
    /// index instead of a scan of the spans' attribute JSON.</summary>
    public const string SearchTermsTable = "SearchTerms";

    /// <summary><see cref="SettingsTable"/> key present while traces written before the search
    /// index existed still need indexing; its value records how far the backfill has got.</summary>
    public const string SearchBackfillSettingKey = "search-index-backfill";

    /// <summary>Per-(span, attribute) and per-(log, attribute) index tables written by earlier
    /// versions. No longer used: schema initialization drops them.</summary>
    public const string LegacySpanAttributesTable = "SpanAttributes";

    /// <inheritdoc cref="LegacySpanAttributesTable"/>
    public const string LegacyLogAttributesTable = "LogAttributes";

    /// <summary>Every table/collection this library owns — used to recognize (and drop) spans the
    /// host's own DB instrumentation records for the library's own reads and writes.</summary>
    public static readonly IReadOnlyList<string> AllTables =
        [ResourcesTable, SpansTable, LogsTable, IgnoreRulesTable, TracesTable, AttributeKeysTable, SettingsTable, PinnedTracesTable, SearchTermsTable];

    /// <summary>Bracket-qualifies a table name with a schema, e.g. "powertraces"+"Spans" -&gt; "[powertraces].[Spans]".</summary>
    public static string Qualify(string schema, string table) => $"[{schema}].[{table}]";

    /// <summary>Schema names go straight into DDL text (SQL doesn't support parameterized identifiers),
    /// so validate them as plain identifiers up front rather than trusting caller-supplied config.</summary>
    public static void ValidateSchemaName(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || !ValidIdentifier().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a valid SQL schema name. Use letters, digits, and underscores, starting with a letter or underscore.",
                nameof(schema));
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidIdentifier();
}
