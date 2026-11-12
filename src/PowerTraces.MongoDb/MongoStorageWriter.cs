using MongoDB.Bson;
using MongoDB.Driver;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Owns schema/index setup and writes batches of spans/logs to MongoDB: makes sure
/// indexes exist (once, lazily), resolves the batch's Resource document, reserves sequential log
/// ids where needed, then bulk-inserts — plus, for spans, one upsert per trace into the Traces
/// collection the trace list reads. One instance is shared by both exporters for a given options
/// instance so index init and the resource cache are shared too.</summary>
public sealed class MongoStorageWriter(PowerTracesExporterOptions options) : IStorageWriter, IIgnoreRuleSource
{
    private readonly MongoResourceWriter _resources = new(options.Schema);

    // MongoSchemaInitializer guards its own idempotency (shared with MongoDashboardStore for the
    // same database) — nothing to duplicate here.
    public Task EnsureSchemaAsync(CancellationToken ct) =>
        MongoSchemaInitializer.EnsureCreatedAsync(options.ConnectionString, options.Schema, ct);

    public async Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await MongoIgnoreRules.ListAsync(options.ConnectionString, options.Schema, ct).ConfigureAwait(false);
    }

    public async Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var database = MongoConnection.GetDatabase(options.ConnectionString);
        await database.GetCollection<SpanDocument>(MongoNames.Spans(options.Schema))
            .DeleteManyAsync(Builders<SpanDocument>.Filter.In(s => s.TraceId, traceIds), ct).ConfigureAwait(false);
        await database.GetCollection<TraceDocument>(MongoNames.Traces(options.Schema))
            .DeleteManyAsync(Builders<TraceDocument>.Filter.In(t => t.Id, traceIds), ct).ConfigureAwait(false);
    }

    public async Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var timeout = CreateTimeoutSource(ct);
        var innerCt = timeout.Token;

        var database = MongoConnection.GetDatabase(options.ConnectionString);
        var resourceId = await _resources.GetOrCreateAsync(database, resource, innerCt).ConfigureAwait(false);

        var docs = new List<SpanDocument>(rows.Count);
        foreach (var r in rows)
        {
            docs.Add(new SpanDocument
            {
                Id = r.Id,
                TraceId = r.TraceId,
                SpanId = r.SpanId,
                ParentSpanId = r.ParentSpanId,
                ResourceId = resourceId,
                Name = r.Name,
                Kind = r.Kind,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                Status = r.Status,
                StatusMessage = r.StatusMessage,
                HasError = r.HasError,
                HttpStatus = r.HttpStatus,
                AttributesJson = StoredJson.Pack(r.AttributesJson),
                ScopeLibraryName = r.ScopeLibraryName,
                ScopeLibraryVersion = r.ScopeLibraryVersion,
                EventsJson = StoredJson.Pack(r.EventsJson),
                LinksJson = r.LinksJson,
            });
        }

        var spans = database.GetCollection<SpanDocument>(MongoNames.Spans(options.Schema));
        await spans.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false }, innerCt).ConfigureAwait(false);

        var terms = SearchTerms.FromBatch(rows).ToDictionary(t => t.TraceId, t => t.Terms, StringComparer.Ordinal);
        var updates = TraceSummary.FromBatch(rows, resource)
            .Select(summary => MongoTraceSummaries.Upsert(summary, terms[summary.TraceId], resourceId))
            .ToList();

        var traces = database.GetCollection<TraceDocument>(MongoNames.Traces(options.Schema));
        await traces.BulkWriteAsync(updates, new BulkWriteOptions { IsOrdered = false }, innerCt).ConfigureAwait(false);

        await MongoAttributeKeys.RecordAsync(database, options.ConnectionString, options.Schema, AttributeScope.Traces,
            SearchTerms.FieldKeys(rows), innerCt).ConfigureAwait(false);
    }

    public async Task WriteLogsAsync(IReadOnlyList<LogRow> rows, ResourceInfo resource, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var timeout = CreateTimeoutSource(ct);
        var innerCt = timeout.Token;

        var database = MongoConnection.GetDatabase(options.ConnectionString);
        var resourceId = await _resources.GetOrCreateAsync(database, resource, innerCt).ConfigureAwait(false);

        // Reserve the whole batch's worth of sequential ids in one round-trip rather than one
        // $inc per row.
        var firstId = await MongoLogIdAllocator.ReserveBlockAsync(database, options.Schema, rows.Count, innerCt).ConfigureAwait(false);

        var docs = new List<LogDocument>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            docs.Add(new LogDocument
            {
                Id = firstId + i,
                ResourceId = resourceId,
                Timestamp = r.Timestamp,
                ObservedTimestamp = r.ObservedTimestamp,
                TraceId = r.TraceId,
                SpanId = r.SpanId,
                SeverityNumber = r.SeverityNumber,
                SeverityText = r.SeverityText,
                Body = r.Body,
                AttributesJson = r.AttributesJson,
                Attributes = MongoTraceSummaries.ToEntries(AttributeText.ValuesOf(r)),
                ScopeLibraryName = r.ScopeLibraryName,
                ScopeLibraryVersion = r.ScopeLibraryVersion,
                SchemaUrl = r.SchemaUrl,
            });
        }

        var logs = database.GetCollection<LogDocument>(MongoNames.Logs(options.Schema));
        await logs.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false }, innerCt).ConfigureAwait(false);

        await MongoAttributeKeys.RecordAsync(database, options.ConnectionString, options.Schema, AttributeScope.Logs,
            rows.SelectMany(r => AttributeText.ValuesOf(r).Select(a => a.Key)), innerCt).ConfigureAwait(false);
    }

    private CancellationTokenSource CreateTimeoutSource(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
        return cts;
    }
}

/// <summary>Builds the per-trace upsert that merges one batch into a <see cref="TraceDocument"/>.</summary>
internal static class MongoTraceSummaries
{
    // Guards against a pathological trace (thousands of spans, each with unique values) growing its
    // summary document without bound. Searches for terms past the cap won't match that one trace.
    public const int MaxTerms = 10_000;

    public static List<AttributeEntry>? ToEntries(IReadOnlyList<KeyValuePair<string, string>> values) =>
        values.Count == 0 ? null : values.Select(v => new AttributeEntry { K = v.Key, V = v.Value }).ToList();

    /// <summary>An update pipeline (one <c>$set</c> stage, so every expression reads the stored
    /// document as it was before this update): start/end widen, the error flag ORs, span counts add,
    /// the root fields are replaced when this batch has the real root — or no root has been seen
    /// and this batch starts earlier — and the resource ids and search terms union in. Every value
    /// taken from telemetry is wrapped in <c>$literal</c>, so a span name or attribute value that
    /// happens to start with "$" is stored as text rather than read as a field path.</summary>
    public static UpdateOneModel<TraceDocument> Upsert(TraceSummary summary, IEnumerable<string> terms, Guid resourceId)
    {

        var start = new BsonDateTime(summary.StartTime);
        var replaceRoot = new BsonDocument("$or", new BsonArray
        {
            summary.HasRoot,
            new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { "$HasRoot", true }),
                new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$StartTime"), "missing" }),
                    new BsonDocument("$lt", new BsonArray { start, "$StartTime" }),
                }),
            }),
        });

        BsonValue Literal(BsonValue value) => new BsonDocument("$literal", value);
        BsonDocument IfRoot(BsonValue value, string field) =>
            new("$cond", new BsonArray { replaceRoot, Literal(value), "$" + field });
        BsonDocument Union(string field, BsonArray values, int cap) =>
            new("$slice", new BsonArray
            {
                new BsonDocument("$setUnion", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$" + field, new BsonArray() }), Literal(values) }),
                cap,
            });

        var set = new BsonDocument
        {
            { "RootName", IfRoot(summary.RootName, "RootName") },
            { "ServiceName", IfRoot(summary.ServiceName is null ? BsonNull.Value : summary.ServiceName, "ServiceName") },
            { "RootAttributesJson", IfRoot(StoredJson.Pack(summary.RootAttributesJson), "RootAttributesJson") },
            { "HasRoot", new BsonDocument("$or", new BsonArray { summary.HasRoot, new BsonDocument("$eq", new BsonArray { "$HasRoot", true }) }) },
            { "StartTime", new BsonDocument("$min", new BsonArray { "$StartTime", start }) },
            { "EndTime", new BsonDocument("$max", new BsonArray { "$EndTime", new BsonDateTime(summary.EndTime) }) },
            { "HasError", new BsonDocument("$or", new BsonArray { summary.HasError, new BsonDocument("$eq", new BsonArray { "$HasError", true }) }) },
            { "SpanCount", new BsonDocument("$add", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$SpanCount", 0 }), summary.SpanCount }) },
            { "ResourceIds", Union("ResourceIds", new BsonArray { new BsonBinaryData(resourceId, GuidRepresentation.Standard) }, int.MaxValue) },
            { "Terms", Union("Terms", new BsonArray(terms), MaxTerms) },
        };

        var pipeline = PipelineDefinition<TraceDocument, TraceDocument>.Create(new BsonDocument("$set", set));
        return new UpdateOneModel<TraceDocument>(
            Builders<TraceDocument>.Filter.Eq(t => t.Id, summary.TraceId),
            new PipelineUpdateDefinition<TraceDocument>(pipeline))
        {
            IsUpsert = true,
        };
    }
}
