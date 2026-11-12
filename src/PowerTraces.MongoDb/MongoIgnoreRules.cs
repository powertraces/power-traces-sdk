using MongoDB.Driver;
using PowerTraces.Dashboard.Query;
using PowerTraces.Storage;

namespace PowerTraces.MongoDb;

/// <summary>Reads/edits a MongoDB database's ignore rules through <see cref="IgnoreRuleCache"/> —
/// shared by <see cref="MongoStorageWriter"/> (which applies them) and
/// <see cref="MongoDashboardStore"/> (which edits them), so both hit one cache entry.</summary>
internal static class MongoIgnoreRules
{
    private static string CacheKey(string connectionString, string schema) =>
        IgnoreRuleCache.KeyFor("mongodb", connectionString, schema);

    private static IMongoCollection<IgnoreRuleDocument> Collection(string connectionString, string schema) =>
        MongoConnection.GetDatabase(connectionString).GetCollection<IgnoreRuleDocument>(MongoNames.IgnoreRules(schema));

    public static Task<IReadOnlyList<IgnoreRuleDto>> ListAsync(string connectionString, string schema, CancellationToken ct) =>
        IgnoreRuleCache.GetAsync(CacheKey(connectionString, schema), async loadCt =>
        {
            var docs = await Collection(connectionString, schema)
                .Find(Builders<IgnoreRuleDocument>.Filter.Empty)
                .SortByDescending(r => r.CreatedAt)
                .ToListAsync(loadCt)
                .ConfigureAwait(false);
            return (IReadOnlyList<IgnoreRuleDto>)docs
                .Select(d => new IgnoreRuleDto(d.Id, d.Kind, d.Name, DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc)))
                .ToList();
        }, ct);

    public static async Task AddAsync(string connectionString, string schema, IgnoreRuleKind kind, string name, CancellationToken ct)
    {
        var kindText = kind.ToString().ToLowerInvariant();
        var filter = Builders<IgnoreRuleDocument>.Filter.Eq(r => r.Kind, kindText) & Builders<IgnoreRuleDocument>.Filter.Eq(r => r.Name, name);
        var update = Builders<IgnoreRuleDocument>.Update
            .SetOnInsert(r => r.Id, Guid.NewGuid())
            .SetOnInsert(r => r.CreatedAt, DateTime.UtcNow);

        try
        {
            await Collection(connectionString, schema).UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another request added the same rule at the same moment — it exists either way.
        }

        IgnoreRuleCache.Invalidate(CacheKey(connectionString, schema));
    }

    public static async Task DeleteAsync(string connectionString, string schema, Guid id, CancellationToken ct)
    {
        await Collection(connectionString, schema).DeleteOneAsync(Builders<IgnoreRuleDocument>.Filter.Eq(r => r.Id, id), ct).ConfigureAwait(false);
        IgnoreRuleCache.Invalidate(CacheKey(connectionString, schema));
    }
}
