using MongoDB.Driver;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Creates the library's indexes in the target MongoDB database if they don't already
/// exist. There's no DDL/schema to create in Mongo — collections come into existence lazily on
/// first write — so this just makes sure the right indexes exist. Safe to call every time the host
/// application starts: <c>CreateIndexAsync</c>/<c>CreateManyAsync</c> no-op when an index with the
/// same key spec already exists, so there's no need for the lock-guarded DDL dance the SQL Server
/// initializer needs. Data written by an earlier version is migrated once (see
/// <see cref="BackfillIfNeededAsync"/>).</summary>
public static class MongoSchemaInitializer
{
    // Shared by every MongoStorageWriter/MongoDashboardStore instance in the process, so a writer
    // and a dashboard store pointed at the same database only actually run this once between them,
    // instead of each independently guarding its own duplicate "have I done this already?" check.
    private static readonly SchemaInitGate Gate = new();

    public static Task EnsureCreatedAsync(string connectionString, CancellationToken ct = default)
        => EnsureCreatedAsync(connectionString, PowerTracesSchema.DefaultSchemaName, ct);

    public static Task EnsureCreatedAsync(string connectionString, string schema, CancellationToken ct = default)
    {
        PowerTracesSchema.ValidateSchemaName(schema);
        return Gate.RunOnceAsync(connectionString, schema, ct2 => EnsureCreatedCoreAsync(connectionString, schema, ct2), ct);
    }

    private static async Task EnsureCreatedCoreAsync(string connectionString, string schema, CancellationToken ct)
    {
        var database = MongoConnection.GetDatabase(connectionString);

        var resources = database.GetCollection<ResourceDocument>(MongoNames.Resources(schema));
        await resources.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ResourceDocument>(Builders<ResourceDocument>.IndexKeys.Ascending(r => r.AttributesHash)),
                new CreateIndexModel<ResourceDocument>(Builders<ResourceDocument>.IndexKeys.Ascending(r => r.ServiceName)),
            ],
            cancellationToken: ct).ConfigureAwait(false);

        var spans = database.GetCollection<SpanDocument>(MongoNames.Spans(schema));
        await spans.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SpanDocument>(Builders<SpanDocument>.IndexKeys.Ascending(s => s.TraceId)),
                new CreateIndexModel<SpanDocument>(Builders<SpanDocument>.IndexKeys.Ascending(s => s.StartTime)),
            ],
            cancellationToken: ct).ConfigureAwait(false);

        var logs = database.GetCollection<LogDocument>(MongoNames.Logs(schema));
        await logs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Ascending(l => l.Timestamp)),
                new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Ascending(l => l.TraceId)),
                new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Ascending(l => l.SeverityNumber)),
                new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Ascending(l => l.ResourceId)),
                new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Ascending("Attributes.K").Ascending("Attributes.V")),
            ],
            cancellationToken: ct).ConfigureAwait(false);

        var traces = database.GetCollection<TraceDocument>(MongoNames.Traces(schema));
        await traces.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<TraceDocument>(Builders<TraceDocument>.IndexKeys.Descending(t => t.StartTime).Ascending(t => t.Id)),
                new CreateIndexModel<TraceDocument>(Builders<TraceDocument>.IndexKeys.Ascending(t => t.HasError).Descending(t => t.StartTime)),
                new CreateIndexModel<TraceDocument>(Builders<TraceDocument>.IndexKeys.Ascending(t => t.ResourceIds).Descending(t => t.StartTime)),
                new CreateIndexModel<TraceDocument>(Builders<TraceDocument>.IndexKeys.Ascending(t => t.Terms).Descending(t => t.StartTime)),
                new CreateIndexModel<TraceDocument>(Builders<TraceDocument>.IndexKeys.Ascending(t => t.RootName).Descending(t => t.StartTime)),
            ],
            cancellationToken: ct).ConfigureAwait(false);

        var ignoreRules = database.GetCollection<IgnoreRuleDocument>(MongoNames.IgnoreRules(schema));
        await ignoreRules.Indexes.CreateOneAsync(
            new CreateIndexModel<IgnoreRuleDocument>(
                Builders<IgnoreRuleDocument>.IndexKeys.Ascending(r => r.Kind).Ascending(r => r.Name),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct).ConfigureAwait(false);

        await BackfillIfNeededAsync(database, schema, ct).ConfigureAwait(false);
    }

    /// <summary>Bumped when stored data needs a one-time migration; recorded in the Counters
    /// collection (the "SchemaVersion" document) once that migration has run.</summary>
    private const long CurrentDataVersion = 4;

    /// <summary>Data written by an earlier version has no Traces documents and no indexed log
    /// attributes. Rebuilds both once, client-side (Mongo can't parse the stored JSON strings
    /// server-side). Every step recomputes from the stored spans/logs and overwrites, so if two
    /// processes start at once and both run it, they converge on the same result.</summary>
    private static async Task BackfillIfNeededAsync(IMongoDatabase database, string schema, CancellationToken ct)
    {
        var counters = database.GetCollection<CounterDocument>(MongoNames.Counters(schema));
        var versionFilter = Builders<CounterDocument>.Filter.Eq(c => c.Id, "SchemaVersion");
        var version = await counters.Find(versionFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (version?.Value >= CurrentDataVersion)
        {
            return;
        }

        if (version is null || version.Value < 2)
        {
            await BackfillTracesAsync(database, schema, ct).ConfigureAwait(false);
            await BackfillLogAttributesAsync(database, schema, ct).ConfigureAwait(false);
        }

        if (version is null || version.Value < 3)
        {
            await SeedAttributeKeysAsync(database, schema, ct).ConfigureAwait(false);
        }

        // Traces written before the search index existed get indexed in the background by the
        // dashboard (MongoDashboardStore.BackfillSearchIndexAsync); this marks that work as due.
        if (await database.GetCollection<TraceDocument>(MongoNames.Traces(schema))
                .Find(Builders<TraceDocument>.Filter.Empty).Limit(1).AnyAsync(ct).ConfigureAwait(false))
        {
            await database.GetCollection<SettingDocument>(MongoNames.Settings(schema)).UpdateOneAsync(
                Builders<SettingDocument>.Filter.Eq(d => d.Id, PowerTracesSchema.SearchBackfillSettingKey),
                Builders<SettingDocument>.Update
                    .SetOnInsert(d => d.Value, "{}")
                    .SetOnInsert(d => d.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true },
                ct).ConfigureAwait(false);
        }

        await counters.UpdateOneAsync(
            versionFilter,
            Builders<CounterDocument>.Update.Max(c => c.Value, CurrentDataVersion),
            new UpdateOptions { IsUpsert = true },
            ct).ConfigureAwait(false);
    }

    /// <summary>Records every attribute key already present on trace and log documents, so the
    /// attribute picker isn't empty right after upgrading.</summary>
    private static async Task SeedAttributeKeysAsync(IMongoDatabase database, string schema, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var writes = new List<WriteModel<AttributeKeyDocument>>();
        foreach (var (source, keys) in new[]
        {
            ("traces", await (await database.GetCollection<TraceDocument>(MongoNames.Traces(schema))
                .DistinctAsync<string>("Attributes.K", Builders<TraceDocument>.Filter.Empty, cancellationToken: ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false)),
            ("logs", await (await database.GetCollection<LogDocument>(MongoNames.Logs(schema))
                .DistinctAsync<string>("Attributes.K", Builders<LogDocument>.Filter.Empty, cancellationToken: ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false)),
        })
        {
            foreach (var key in keys)
            {
                writes.Add(new UpdateOneModel<AttributeKeyDocument>(
                    Builders<AttributeKeyDocument>.Filter.Eq(d => d.Id, $"{source}:{key}"),
                    Builders<AttributeKeyDocument>.Update
                        .SetOnInsert(d => d.Key, key)
                        .SetOnInsert(d => d.Source, source)
                        .SetOnInsert(d => d.FirstSeenAt, now))
                { IsUpsert = true });
            }
        }

        if (writes.Count > 0)
        {
            await MongoAttributeKeys.Collection(database, schema).BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        }
    }

    private static async Task BackfillTracesAsync(IMongoDatabase database, string schema, CancellationToken ct)
    {
        var spans = database.GetCollection<SpanDocument>(MongoNames.Spans(schema));
        var traces = database.GetCollection<TraceDocument>(MongoNames.Traces(schema));
        var serviceNames = (await database.GetCollection<ResourceDocument>(MongoNames.Resources(schema))
                .Find(Builders<ResourceDocument>.Filter.Empty).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => r.ServiceName);

        var pending = new List<WriteModel<TraceDocument>>();
        var current = new List<SpanDocument>();

        using var cursor = await spans.Find(Builders<SpanDocument>.Filter.Empty)
            .Sort(Builders<SpanDocument>.Sort.Ascending(s => s.TraceId))
            .ToCursorAsync(ct).ConfigureAwait(false);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var span in cursor.Current)
            {
                if (current.Count > 0 && current[0].TraceId != span.TraceId)
                {
                    await FlushAsync().ConfigureAwait(false);
                }

                current.Add(span);
            }
        }

        await FlushAsync().ConfigureAwait(false);
        if (pending.Count > 0)
        {
            await traces.BulkWriteAsync(pending, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        }

        async Task FlushAsync()
        {
            if (current.Count == 0)
            {
                return;
            }

            var root = current.FirstOrDefault(s => s.ParentSpanId.Length == 0)
                ?? current.OrderBy(s => s.StartTime).ThenBy(s => s.SpanId, StringComparer.Ordinal).First();
            var doc = new TraceDocument
            {
                Id = current[0].TraceId,
                StartTime = current.Min(s => s.StartTime),
                EndTime = current.Max(s => s.EndTime ?? s.StartTime),
                HasError = current.Any(s => s.HasError),
                SpanCount = current.Count,
                HasRoot = root.ParentSpanId.Length == 0,
                RootName = root.Name,
                ServiceName = root.ResourceId is { } rid ? serviceNames.GetValueOrDefault(rid) : null,
                RootAttributesJson = root.AttributesJson,
                ResourceIds = current.Where(s => s.ResourceId.HasValue).Select(s => s.ResourceId!.Value).Distinct().ToList(),
            };

            pending.Add(new ReplaceOneModel<TraceDocument>(Builders<TraceDocument>.Filter.Eq(t => t.Id, doc.Id), doc) { IsUpsert = true });
            current.Clear();

            if (pending.Count >= 500)
            {
                await traces.BulkWriteAsync(pending, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
                pending.Clear();
            }
        }
    }

    private static async Task BackfillLogAttributesAsync(IMongoDatabase database, string schema, CancellationToken ct)
    {
        var logs = database.GetCollection<LogDocument>(MongoNames.Logs(schema));
        var pending = new List<WriteModel<LogDocument>>();

        using var cursor = await logs.Find(Builders<LogDocument>.Filter.Exists("Attributes", false))
            .Project(l => new { l.Id, l.AttributesJson })
            .ToCursorAsync(ct).ConfigureAwait(false);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var log in cursor.Current)
            {
                var entries = MongoTraceSummaries.ToEntries(AttributeText.Flatten(log.AttributesJson));
                if (entries is null)
                {
                    continue;
                }

                pending.Add(new UpdateOneModel<LogDocument>(
                    Builders<LogDocument>.Filter.Eq(l => l.Id, log.Id),
                    Builders<LogDocument>.Update.Set(l => l.Attributes, entries)));

                if (pending.Count >= 1000)
                {
                    await logs.BulkWriteAsync(pending, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
                    pending.Clear();
                }
            }
        }

        if (pending.Count > 0)
        {
            await logs.BulkWriteAsync(pending, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        }
    }
}
