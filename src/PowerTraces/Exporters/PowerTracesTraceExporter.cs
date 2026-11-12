using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using PowerTraces.Internal;
using PowerTraces.Storage;

namespace PowerTraces.Exporters;

/// <summary>Writes ended Activities (spans) straight to whichever database <paramref name="writer"/>
/// targets — construct one from a provider package (e.g. PowerTraces.MSSQL's
/// <c>SqlServerStorageWriter</c>) or your own <see cref="IStorageWriter"/>. Meant to run behind a
/// BatchActivityExportProcessor (the default when added via <see cref="Extensions.TracerProviderBuilderExtensions.AddPowerTracesExporter"/>),
/// so Export is called from a dedicated background thread and blocking on the DB write here is fine.
///
/// Ignore rules (when the writer implements <see cref="IIgnoreRuleSource"/>) and
/// <see cref="PowerTracesExporterOptions.ExcludePathPrefixes"/> are applied here, once, for every
/// provider: a span rule drops spans with that name; a trace rule — or an excluded path on a
/// trace's local root span — drops the whole trace, including child spans that end before the
/// root does (found by walking the in-process parent chain), spans that end after it (remembered
/// by trace id), and child spans an earlier batch already wrote (purged via
/// <see cref="IStorageWriter.DeleteTracesAsync"/>).</summary>
public sealed class PowerTracesTraceExporter(IStorageWriter writer, PowerTracesExporterOptions options) : BaseExporter<Activity>
{
    private const int RecentTraceCapacity = 10_000;

    private readonly IStorageWriter _writer = writer;
    private readonly RecentTraceIds _ignoredTraceIds = new(RecentTraceCapacity);
    private readonly RecentTraceIds _tracesWithWrittenChildren = new(RecentTraceCapacity);
    private ResourceInfo? _resourceInfo;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        // A Batch over the processor's circular buffer is consumed as it's enumerated, so copy it
        // once — the ignore pass needs to see every root before deciding about any child.
        var activities = new List<Activity>();
        foreach (var activity in batch)
        {
            activities.Add(activity);
        }

        if (activities.Count == 0)
        {
            return ExportResult.Success;
        }

        // Rule lookups and writes go to the same database the host may be instrumenting — keep
        // this exporter's own traffic from producing spans that would be exported right back.
        using var suppress = SuppressInstrumentationScope.Begin();

        var rules = LoadRules();

        // Pass 1: which traces does this batch reveal as ignored?
        List<string>? newlyIgnored = null;
        foreach (var activity in activities)
        {
            if (activity.Parent is null && IsIgnoredLocalRoot(activity, rules))
            {
                var traceId = activity.TraceId.ToHexString();
                if (!_ignoredTraceIds.Contains(traceId))
                {
                    _ignoredTraceIds.Add(traceId);
                    (newlyIgnored ??= []).Add(traceId);
                }
            }
        }

        // Pass 2: map everything that survives.
        var rows = new List<SpanRow>(activities.Count);
        List<string>? childTraceIds = null;
        foreach (var activity in activities)
        {
            if (IsExcluded(activity, rules))
            {
                continue;
            }

            rows.Add(Map(activity, options));
            if (activity.Parent is not null || activity.ParentSpanId != default)
            {
                (childTraceIds ??= []).Add(rows[^1].TraceId);
            }
        }

        try
        {
            if (rows.Count > 0)
            {
                _writer.WriteSpansAsync(rows, GetResourceInfo(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            options.OnExportError?.Invoke(ex);
            return ExportResult.Failure;
        }

        if (childTraceIds is not null)
        {
            foreach (var traceId in childTraceIds)
            {
                _tracesWithWrittenChildren.Add(traceId);
            }
        }

        PurgePartiallyWrittenTraces(newlyIgnored);
        return ExportResult.Success;
    }

    private IgnoreRuleSet LoadRules()
    {
        if (_writer is not IIgnoreRuleSource source)
        {
            return IgnoreRuleSet.Empty;
        }

        try
        {
            return IgnoreRuleSet.From(source.ListIgnoreRulesAsync(CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            // Can't read the rules (e.g. the database is briefly unreachable) — export unfiltered
            // rather than fail the batch; the write itself surfaces a persistent outage.
            options.OnExportError?.Invoke(ex);
            return IgnoreRuleSet.Empty;
        }
    }

    /// <summary>Removes the child spans earlier batches already wrote for traces this batch just
    /// found to be ignored. Best-effort: a failure leaves a partial trace visible, which is only
    /// cosmetic, so it's reported but doesn't fail the export.</summary>
    private void PurgePartiallyWrittenTraces(List<string>? newlyIgnored)
    {
        if (newlyIgnored is null)
        {
            return;
        }

        var toDelete = newlyIgnored.Where(_tracesWithWrittenChildren.Contains).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        try
        {
            _writer.DeleteTracesAsync(toDelete, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            options.OnExportError?.Invoke(ex);
        }
    }

    private ResourceInfo GetResourceInfo()
    {
        // The Resource is fixed for the lifetime of the TracerProvider, so compute its JSON once.
        return _resourceInfo ??= BuildResourceInfo(ParentProvider?.GetResource() ?? Resource.Empty);
    }

    internal static ResourceInfo BuildResourceInfo(Resource resource)
    {
        var attributesJson = AttributeJson.Serialize(resource.Attributes.Select(a => new KeyValuePair<string, object?>(a.Key, a.Value)));
        var serviceName = resource.Attributes.FirstOrDefault(a => a.Key == "service.name").Value?.ToString();
        return new ResourceInfo(serviceName, attributesJson, null);
    }

    // Tag names client/driver instrumentation (SqlClient, Npgsql, MySqlConnector, the Mongo
    // driver, ...) uses for the raw query text and, separately, the collection/table being hit —
    // covers both the older and the current stable OTel semantic-convention names since host apps
    // may be on either.
    private static readonly string[] DbStatementTagNames = ["db.statement", "db.query.text"];
    private static readonly string[] DbCollectionTagNames = ["db.collection.name", "db.sql.table", "db.mongodb.collection"];

    private bool IsExcluded(Activity activity, IgnoreRuleSet rules)
    {
        // Regardless of ExcludePathPrefixes: if a host has DB client instrumentation enabled
        // (e.g. AddSqlClientInstrumentation()), every write this exporter itself makes to its own
        // tables generates a span — which would otherwise get exported right back into the same
        // tables, forever. The schema/table names are always known here (they're this exporter's
        // own configuration), so this needs no opt-in.
        if (IsOwnTableActivity(activity))
        {
            return true;
        }

        if (_ignoredTraceIds.Contains(activity.TraceId.ToHexString()))
        {
            return true;
        }

        if (rules.SpanNames.Contains(activity.DisplayName))
        {
            return true;
        }

        // A child whose local root is ignored — the root usually hasn't ended (so isn't in any
        // batch yet) when its children are exported, but it's still reachable through Parent.
        if (activity.Parent is not null)
        {
            var root = activity.Parent;
            while (root.Parent is not null)
            {
                root = root.Parent;
            }

            if (IsIgnoredLocalRoot(root, rules))
            {
                _ignoredTraceIds.Add(activity.TraceId.ToHexString());
                return true;
            }
        }

        return MatchesExcludedPath(activity);
    }

    /// <summary>True when <paramref name="root"/> — a span with no in-process parent — marks its
    /// whole trace as ignored: it matches a trace rule by name, or its request path is excluded.</summary>
    private bool IsIgnoredLocalRoot(Activity root, IgnoreRuleSet rules) =>
        rules.TraceRootNames.Contains(root.DisplayName) || MatchesExcludedPath(root);

    private bool MatchesExcludedPath(Activity activity)
    {
        if (options.ExcludePathPrefixes.Count == 0)
        {
            return false;
        }

        var path = activity.GetTagItem("http.route")?.ToString() ?? activity.GetTagItem("url.path")?.ToString();
        if (path is null)
        {
            return false;
        }

        foreach (var prefix in options.ExcludePathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if this span is a DB client call against one of this exporter's own tables
    /// (or, for Mongo, the equivalently-named collections) in the configured schema — checked
    /// against whichever query-text or collection/table tag the instrumentation happened to set,
    /// since which one exists depends on the driver and the OTel instrumentation library's
    /// version.</summary>
    private bool IsOwnTableActivity(Activity activity)
    {
        var schema = options.Schema;

        foreach (var tagName in DbStatementTagNames)
        {
            var statement = activity.GetTagItem(tagName)?.ToString();
            if (!string.IsNullOrEmpty(statement) &&
                statement.Contains(schema, StringComparison.OrdinalIgnoreCase) &&
                MentionsOwnTable(statement))
            {
                return true;
            }
        }

        foreach (var tagName in DbCollectionTagNames)
        {
            var collection = activity.GetTagItem(tagName)?.ToString();
            if (!string.IsNullOrEmpty(collection) &&
                collection.Contains(schema, StringComparison.OrdinalIgnoreCase) &&
                MentionsOwnTable(collection))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsOwnTable(string text)
    {
        foreach (var table in PowerTracesSchema.AllTables)
        {
            if (text.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static SpanRow Map(Activity activity, PowerTracesExporterOptions options)
    {
        var httpStatus = activity.GetTagItem("http.response.status_code")?.ToString()
            ?? activity.GetTagItem("http.status_code")?.ToString();

        var attributesJson = AttributeJson.Serialize(activity.TagObjects, options.AttributeJsonOptions);

        return new SpanRow
        {
            Id = Guid.NewGuid(),
            TraceId = activity.TraceId.ToHexString(),
            SpanId = activity.SpanId.ToHexString(),
            ParentSpanId = activity.ParentSpanId == default ? "" : activity.ParentSpanId.ToHexString(),
            Name = activity.DisplayName,
            Kind = activity.Kind.ToString(),
            StartTime = activity.StartTimeUtc,
            EndTime = activity.Duration > TimeSpan.Zero ? activity.StartTimeUtc + activity.Duration : null,
            Status = activity.Status.ToString(),
            StatusMessage = activity.StatusDescription,
            HasError = activity.Status == ActivityStatusCode.Error,
            HttpStatus = httpStatus,
            AttributesJson = attributesJson,
            AttributeValues = AttributeText.Flatten(attributesJson),
            ScopeLibraryName = activity.Source.Name,
            ScopeLibraryVersion = activity.Source.Version,
            EventsJson = AttributeJson.SerializeEvents(activity.Events, options.CaptureSourceSnapshots ? attributes => AddSourceSnapshots(attributes, options) : null),
            LinksJson = AttributeJson.SerializeLinks(activity.Links),
        };
    }

    /// <summary>For <see cref="PowerTracesExporterOptions.CaptureSourceSnapshots"/>: an
    /// <c>exception</c> event without <c>exception.details</c> gets them from its stack trace text.</summary>
    private static void AddSourceSnapshots(Dictionary<string, object?> attributes, PowerTracesExporterOptions options)
    {
        if (attributes.ContainsKey(Diagnostics.ExceptionDetails.AttributeKey))
        {
            return;
        }

        try
        {
            var details = Diagnostics.ExceptionDetails.FromStackTraceText(
                attributes.GetValueOrDefault("exception.type")?.ToString(),
                attributes.GetValueOrDefault("exception.message")?.ToString(),
                attributes.GetValueOrDefault("exception.stacktrace")?.ToString(),
                Math.Clamp(options.SourceSnapshotLines, 0, 50),
                Math.Clamp(options.SourceSnapshotFrames, 0, 20));
            if (details is not null)
            {
                attributes[Diagnostics.ExceptionDetails.AttributeKey] = details;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The event is exported as recorded.
        }
    }
}
