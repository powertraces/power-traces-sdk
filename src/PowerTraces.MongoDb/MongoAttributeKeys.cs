using System.Collections.Concurrent;
using MongoDB.Driver;
using PowerTraces.Dashboard.Query;
using PowerTraces.Storage;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MongoDb;

/// <summary>The AttributeKeys collection — Mongo's counterpart of the SQL providers' shared table
/// (see <see cref="SqlAttributeKeys"/>): one document per distinct (key, source), written by
/// <see cref="MongoStorageWriter"/> for keys this process hasn't recorded yet, listed by the
/// dashboard's attribute picker.</summary>
internal static class MongoAttributeKeys
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Known = new(StringComparer.Ordinal);

    public static IMongoCollection<AttributeKeyDocument> Collection(IMongoDatabase database, string schema) =>
        database.GetCollection<AttributeKeyDocument>(MongoNames.AttributeKeys(schema));

    public static async Task RecordAsync(IMongoDatabase database, string connectionString, string schema, AttributeScope scope, IEnumerable<string> keys, CancellationToken ct)
    {
        var source = SqlAttributeKeys.SourceOf(scope);
        var known = Known.GetOrAdd(IgnoreRuleCache.KeyFor("mongodb", connectionString, schema), static _ => new(StringComparer.Ordinal));
        var fresh = keys
            .Select(k => $"{source}:{AttributeText.Truncate(k, AttributeText.MaxKeyLength)}")
            .Distinct(StringComparer.Ordinal)
            .Where(id => !known.ContainsKey(id))
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var writes = fresh.Select(id => new UpdateOneModel<AttributeKeyDocument>(
            Builders<AttributeKeyDocument>.Filter.Eq(d => d.Id, id),
            Builders<AttributeKeyDocument>.Update
                .SetOnInsert(d => d.Key, id[(source.Length + 1)..])
                .SetOnInsert(d => d.Source, source)
                .SetOnInsert(d => d.FirstSeenAt, now))
        { IsUpsert = true }).ToList();

        await Collection(database, schema).BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        foreach (var id in fresh)
        {
            known.TryAdd(id, 0);
        }
    }
}
