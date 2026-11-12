using System.Text.Json;
using MongoDB.Driver;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.MongoDb;

/// <summary>Retention for MongoDB: the policy and the last run are JSON values in the Settings
/// collection, pinned traces have a collection of their own. A sweep finds expired documents
/// through the StartTime/Timestamp indexes a batch at a time and deletes them by id.</summary>
public sealed partial class MongoDashboardStore : IRetentionStore
{
    private const string RetentionSettingsKey = "retention";
    private const string RetentionLastRunKey = "retention.lastRun";

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    private IMongoCollection<SettingDocument> Settings => Database.GetCollection<SettingDocument>(MongoNames.Settings(options.Schema));

    private IMongoCollection<PinnedTraceDocument> PinnedTraces => Database.GetCollection<PinnedTraceDocument>(MongoNames.PinnedTraces(options.Schema));

    public async Task<RetentionStateDto> GetRetentionAsync(CancellationToken ct)
    {
        var docs = await Settings
            .Find(Builders<SettingDocument>.Filter.In(d => d.Id, [RetentionSettingsKey, RetentionLastRunKey]))
            .ToListAsync(ct).ConfigureAwait(false);
        var settings = docs.FirstOrDefault(d => d.Id == RetentionSettingsKey);
        var lastRun = docs.FirstOrDefault(d => d.Id == RetentionLastRunKey);
        return new RetentionStateDto(
            Deserialize<RetentionSettingsDto>(settings?.Value) ?? RetentionSettingsDto.Disabled,
            settings is null ? null : Utc(settings.UpdatedAt),
            Deserialize<RetentionRunDto>(lastRun?.Value));
    }

    public Task SaveRetentionSettingsAsync(RetentionSettingsDto settings, CancellationToken ct) =>
        WriteSettingAsync(RetentionSettingsKey, JsonSerializer.Serialize(settings, SettingsJson), ct);

    public async Task<IReadOnlyList<PinnedTraceDto>> ListPinnedTracesAsync(CancellationToken ct)
    {
        var docs = await PinnedTraces.Find(Builders<PinnedTraceDocument>.Filter.Empty)
            .SortByDescending(d => d.PinnedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs
            .Select(d => new PinnedTraceDto(d.Id, d.RootName, d.ServiceName, d.StartTime is { } start ? Utc(start) : null, d.HasError, Utc(d.PinnedAt)))
            .ToList();
    }

    public async Task<bool> IsTracePinnedAsync(string traceId, CancellationToken ct) =>
        await PinnedTraces.CountDocumentsAsync(
            Builders<PinnedTraceDocument>.Filter.Eq(d => d.Id, NormalizeTraceId(traceId)),
            new CountOptions { Limit = 1 },
            ct).ConfigureAwait(false) > 0;

    public async Task PinTraceAsync(PinnedTraceDto trace, CancellationToken ct)
    {
        var id = NormalizeTraceId(trace.TraceId);
        var update = Builders<PinnedTraceDocument>.Update
            .SetOnInsert(d => d.RootName, trace.RootName)
            .SetOnInsert(d => d.ServiceName, trace.ServiceName)
            .SetOnInsert(d => d.StartTime, trace.StartTime)
            .SetOnInsert(d => d.HasError, trace.HasError)
            .SetOnInsert(d => d.PinnedAt, trace.PinnedAt);

        try
        {
            await PinnedTraces.UpdateOneAsync(Builders<PinnedTraceDocument>.Filter.Eq(d => d.Id, id), update, new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another request pinned the same trace at the same moment — it's pinned either way.
        }
    }

    public Task UnpinTraceAsync(string traceId, CancellationToken ct) =>
        PinnedTraces.DeleteOneAsync(Builders<PinnedTraceDocument>.Filter.Eq(d => d.Id, NormalizeTraceId(traceId)), ct);

    public async Task<RetentionRunDto?> ApplyRetentionAsync(CancellationToken ct)
    {
        var settings = (await GetRetentionAsync(ct).ConfigureAwait(false)).Settings;
        if (!settings.Enabled)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var run = await Retention.RunAsync(async () =>
        {
            // Pinned traces are few (they're picked by hand), so excluding them is a $nin.
            var pinned = (await ListPinnedTracesAsync(ct).ConfigureAwait(false)).Select(p => p.TraceId).ToList();
            return (
                await DeleteExpiredTracesAsync(Retention.Cutoff(settings.TraceRetentionDays, now), settings.KeepErrorTraces, pinned, ct).ConfigureAwait(false),
                await DeleteExpiredLogsAsync(Retention.Cutoff(settings.LogRetentionDays, now), settings.KeepErrorLogs, pinned, ct).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await WriteSettingAsync(RetentionLastRunKey, JsonSerializer.Serialize(run, SettingsJson), ct).ConfigureAwait(false);
        return run;
    }

    private async Task<long> DeleteExpiredTracesAsync(DateTime cutoff, bool keepErrors, List<string> pinned, CancellationToken ct)
    {
        var f = Builders<TraceDocument>.Filter;
        var filter = f.Lt(t => t.StartTime, cutoff) & f.Nin(t => t.Id, pinned);
        if (keepErrors)
        {
            filter &= f.Eq(t => t.HasError, false);
        }

        long deleted = 0;
        while (true)
        {
            var ids = await Traces.Find(filter).Limit(Retention.BatchSize).Project(t => t.Id).ToListAsync(ct).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return deleted;
            }

            await Spans.DeleteManyAsync(Builders<SpanDocument>.Filter.In(s => s.TraceId, ids), ct).ConfigureAwait(false);
            await Traces.DeleteManyAsync(f.In(t => t.Id, ids), ct).ConfigureAwait(false);
            deleted += ids.Count;
            if (ids.Count < Retention.BatchSize)
            {
                return deleted;
            }
        }
    }

    private async Task<long> DeleteExpiredLogsAsync(DateTime cutoff, bool keepErrors, List<string> pinned, CancellationToken ct)
    {
        var f = Builders<LogDocument>.Filter;
        var filter = f.Lt(l => l.Timestamp, cutoff);
        if (pinned.Count > 0)
        {
            filter &= f.Nin(l => l.TraceId, pinned);
        }

        if (keepErrors)
        {
            filter &= f.Lt(l => l.SeverityNumber, Retention.ErrorSeverityNumber);
        }

        long deleted = 0;
        while (true)
        {
            var ids = await Logs.Find(filter).Limit(Retention.BatchSize).Project(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return deleted;
            }

            await Logs.DeleteManyAsync(f.In(l => l.Id, ids), ct).ConfigureAwait(false);
            deleted += ids.Count;
            if (ids.Count < Retention.BatchSize)
            {
                return deleted;
            }
        }
    }

    private Task WriteSettingAsync(string key, string value, CancellationToken ct) =>
        Settings.ReplaceOneAsync(
            Builders<SettingDocument>.Filter.Eq(d => d.Id, key),
            new SettingDocument { Id = key, Value = value, UpdatedAt = DateTime.UtcNow },
            new ReplaceOptions { IsUpsert = true },
            ct);

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SettingsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeTraceId(string traceId) => traceId.Trim().ToLowerInvariant();
}
