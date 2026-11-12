namespace PowerTraces.Dashboard.Query;

/// <summary>A TTL-refreshed cache of one value — used by every provider's
/// <see cref="IDashboardStore.ListAttributeKeysAsync"/> so that discovering which attribute keys
/// exist (a sampling scan, not a free lookup) doesn't re-run on every dashboard page load. Each
/// store keeps one instance per <see cref="AttributeScope"/>.</summary>
public sealed class AttributeKeyCache(TimeSpan? ttl = null)
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<string> _keys = [];
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public async Task<IReadOnlyList<string>> GetOrRefreshAsync(
        Func<CancellationToken, Task<IReadOnlyList<string>>> factory, CancellationToken ct)
    {
        if (DateTime.UtcNow < _expiresAtUtc)
        {
            return _keys;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _expiresAtUtc)
            {
                return _keys;
            }

            _keys = await factory(ct).ConfigureAwait(false);
            _expiresAtUtc = DateTime.UtcNow + _ttl;
            return _keys;
        }
        finally
        {
            _lock.Release();
        }
    }
}
