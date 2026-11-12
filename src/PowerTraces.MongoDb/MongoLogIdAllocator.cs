using MongoDB.Driver;

namespace PowerTraces.MongoDb;

/// <summary>Allocates sequential <see langword="long"/> ids for LogDocument.Id — the classic
/// MongoDB auto-increment pattern via a Counters collection, since Mongo has no native identity
/// column. Reserves a contiguous block of ids per batch (one atomic <c>$inc</c> round-trip per
/// <c>WriteLogsAsync</c> call rather than one per row) via <c>FindOneAndUpdateAsync</c> with
/// <c>ReturnDocument.After</c>: the returned counter value is the last id in the block, so the
/// first is <c>value - count + 1</c>.</summary>
internal static class MongoLogIdAllocator
{
    private const string LogsCounterId = "Logs";

    public static async Task<long> ReserveBlockAsync(IMongoDatabase database, string schema, int count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return 0;
        }

        var counters = database.GetCollection<CounterDocument>(MongoNames.Counters(schema));

        var filter = Builders<CounterDocument>.Filter.Eq(c => c.Id, LogsCounterId);
        // $inc on a field that doesn't exist yet initializes it from 0, and the upsert fills in
        // _id from the filter's equality condition — so no separate $setOnInsert for the starting
        // value is needed here.
        var update = Builders<CounterDocument>.Update.Inc(c => c.Value, count);
        var options = new FindOneAndUpdateOptions<CounterDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        var doc = await counters.FindOneAndUpdateAsync(filter, update, options, ct).ConfigureAwait(false);
        return doc.Value - count + 1;
    }
}
