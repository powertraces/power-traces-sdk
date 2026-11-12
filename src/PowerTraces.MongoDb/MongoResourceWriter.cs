using System.Collections.Concurrent;
using MongoDB.Driver;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Resolves the Mongo _id for a Resource (the process that produced a span/log), reusing
/// one document per distinct attribute set. A process normally has exactly one Resource for its
/// whole lifetime, so the in-memory cache turns this into a one-time lookup per exporter instance
/// rather than a round-trip per export — same shape as <c>SqlServerResourceWriter</c>.
///
/// Unlike the SQL Server version, dedup here needs no separate SELECT-then-INSERT and no
/// application-level locking: <c>FindOneAndUpdateAsync</c> with <c>IsUpsert = true</c> finds-or-
/// creates the Resource document atomically, so two instances racing to write the same new
/// Resource for the first time can't create a duplicate row the way the SQL Server implementation
/// theoretically can.</summary>
internal sealed class MongoResourceWriter(string schema)
{
    private readonly ConcurrentDictionary<string, Guid> _cache = new();

    public async Task<Guid> GetOrCreateAsync(IMongoDatabase database, ResourceInfo resource, CancellationToken ct)
    {
        if (_cache.TryGetValue(resource.AttributesJson, out var cached))
        {
            return cached;
        }

        var resources = database.GetCollection<ResourceDocument>(MongoNames.Resources(schema));
        var hash = AttributesHasher.Compute(resource.AttributesJson);
        var now = DateTime.UtcNow;

        // Mirrors SQL Server's WHERE AttributesHash = CHECKSUM(@Json) AND AttributesJson = @Json —
        // the hash narrows the index scan, the full-JSON comparison guards against hash collisions.
        var filter = Builders<ResourceDocument>.Filter.Eq(r => r.AttributesHash, hash)
            & Builders<ResourceDocument>.Filter.Eq(r => r.AttributesJson, resource.AttributesJson);

        var update = Builders<ResourceDocument>.Update
            .SetOnInsert(r => r.Id, Guid.NewGuid())
            .SetOnInsert(r => r.ServiceName, resource.ServiceName)
            .SetOnInsert(r => r.AttributesJson, resource.AttributesJson)
            .SetOnInsert(r => r.AttributesHash, hash)
            .SetOnInsert(r => r.SchemaUrl, resource.SchemaUrl)
            .SetOnInsert(r => r.FirstSeenAt, now)
            .Set(r => r.LastSeenAt, now);

        var options = new FindOneAndUpdateOptions<ResourceDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        var doc = await resources.FindOneAndUpdateAsync(filter, update, options, ct).ConfigureAwait(false);
        _cache[resource.AttributesJson] = doc.Id;
        return doc.Id;
    }
}
