using System.Collections.Concurrent;

namespace PowerTraces.Dashboard.Query;

/// <summary>Compares a trace with recent traces of the same operation that didn't fail (see
/// <see cref="TraceBaselines"/>). Built only on <see cref="IDashboardStore"/>'s search and trace
/// lookups, so every provider — custom ones included — gets it without new queries. The peers of
/// an operation are loaded once and reused for a few minutes, so opening trace after trace of the
/// same endpoint costs one comparison each.</summary>
internal sealed class TraceBaselineService(IDashboardStore store)
{
    private static readonly TimeSpan PeerCacheTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);
    private const int MaxCachedOperations = 200;
    private const int Parallelism = 4;

    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<TraceDetailDto> Peers)> peerCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim verdictLock = new(1, 1);

    public async Task<TraceBaselineDto?> GetAsync(string traceId, CancellationToken ct)
    {
        var trace = await store.GetTraceAsync(traceId, ct).ConfigureAwait(false);
        if (trace is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(trace.RootName))
        {
            return TraceBaselineDto.Empty;
        }

        var peers = await PeersAsync(trace, ct).ConfigureAwait(false);
        var verdicts = store is IDashboardSettingsStore settings
            ? TraceBaselines.ParseVerdicts(await settings.GetSettingAsync(TraceBaselines.VerdictsSettingKey, ct).ConfigureAwait(false))
            : [];
        return TraceBaselines.Compare(trace, peers, verdicts);
    }

    /// <summary>Records (or, with a null verdict, forgets) a person's call on replies of one
    /// shape. Returns false when the store can't keep it.</summary>
    public async Task<bool> SetVerdictAsync(string shapeKey, string? verdict, CancellationToken ct)
    {
        if (store is not IDashboardSettingsStore settings)
        {
            return false;
        }

        // Read-modify-write of one value: serialized here so two clicks in this process can't
        // drop each other's verdict.
        await verdictLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var verdicts = TraceBaselines.ParseVerdicts(await settings.GetSettingAsync(TraceBaselines.VerdictsSettingKey, ct).ConfigureAwait(false));
            var json = TraceBaselines.WithVerdict(verdicts, shapeKey, verdict, DateTime.UtcNow);
            await settings.SetSettingAsync(TraceBaselines.VerdictsSettingKey, json, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            verdictLock.Release();
        }
    }

    private async Task<IReadOnlyList<TraceDetailDto>> PeersAsync(TraceDetailDto trace, CancellationToken ct)
    {
        var key = $"{trace.ServiceName}|{trace.RootName}";
        var now = DateTime.UtcNow;
        if (peerCache.TryGetValue(key, out var cached) && now - cached.At < PeerCacheTime)
        {
            return cached.Peers;
        }

        var found = await store.SearchTracesAsync(new TraceSearchQuery
        {
            Endpoints = [trace.RootName!],
            HasError = false,
            From = now - Lookback,
            To = now,
            Page = 1,
            PageSize = TraceBaselines.PeerCount + 1,
        }, ct).ConfigureAwait(false);

        var ids = found.Items
            .Where(t => !string.Equals(t.TraceId, trace.TraceId, StringComparison.OrdinalIgnoreCase) && t.ServiceName == trace.ServiceName)
            .Select(t => t.TraceId)
            .Take(TraceBaselines.PeerCount)
            .ToList();

        var peers = new TraceDetailDto?[ids.Count];
        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(ids.Select(async (id, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                peers[i] = await store.GetTraceAsync(id, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var result = peers.OfType<TraceDetailDto>().ToList();
        if (peerCache.Count >= MaxCachedOperations)
        {
            peerCache.Clear();
        }

        peerCache[key] = (now, result);
        return result;
    }
}
