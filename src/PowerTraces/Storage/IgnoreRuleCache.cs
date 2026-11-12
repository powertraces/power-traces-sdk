using System.Collections.Concurrent;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage;

/// <summary>Process-wide cache of each database's ignore rules, keyed by provider + connection +
/// schema so a provider's storage writer and its dashboard store share one entry. Adding or
/// deleting a rule through the dashboard invalidates the entry, so this process applies the change
/// on its next export; other processes writing to the same database pick it up within
/// <see cref="RefreshInterval"/>.</summary>
public static class IgnoreRuleCache
{
    /// <summary>How long a loaded rule set is reused before the next export reloads it.</summary>
    public static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>Builds a cache key that never contains the connection string's secret in clear
    /// text — only its hash.</summary>
    public static string KeyFor(string provider, string connectionString, string schema) =>
        $"{provider}:{StringComparer.Ordinal.GetHashCode(connectionString):x8}:{connectionString.Length}:{schema}";

    public static async Task<IReadOnlyList<IgnoreRuleDto>> GetAsync(
        string key, Func<CancellationToken, Task<IReadOnlyList<IgnoreRuleDto>>> load, CancellationToken ct)
    {
        var entry = Entries.GetOrAdd(key, static _ => new Entry());
        var snapshot = entry.Snapshot;
        if (snapshot is not null && DateTime.UtcNow < snapshot.ExpiresAtUtc)
        {
            return snapshot.Rules;
        }

        await entry.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = entry.Snapshot;
            if (snapshot is not null && DateTime.UtcNow < snapshot.ExpiresAtUtc)
            {
                return snapshot.Rules;
            }

            var version = Volatile.Read(ref entry.Version);
            IReadOnlyList<IgnoreRuleDto> rules;
            try
            {
                rules = await load(ct).ConfigureAwait(false);
            }
            catch when (snapshot is not null)
            {
                // A transient read failure keeps the last known rules rather than suddenly
                // exporting everything that was being ignored.
                return snapshot.Rules;
            }

            // An Invalidate() that raced this load means `rules` may predate the change — return
            // them to this caller, but don't cache them.
            if (Volatile.Read(ref entry.Version) == version)
            {
                entry.Snapshot = new Snapshot(rules, DateTime.UtcNow + RefreshInterval);
            }

            return rules;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public static void Invalidate(string key)
    {
        if (Entries.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref entry.Version);
            entry.Snapshot = null;
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public long Version;
        public volatile Snapshot? Snapshot;
    }

    private sealed record Snapshot(IReadOnlyList<IgnoreRuleDto> Rules, DateTime ExpiresAtUtc);
}
