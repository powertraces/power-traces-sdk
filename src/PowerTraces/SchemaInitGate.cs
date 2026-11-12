using System.Collections.Concurrent;

namespace PowerTraces;

/// <summary>Runs an idempotent async initializer at most once per distinct (connection string,
/// schema) pair, and shares that outcome across every caller holding a reference to the same
/// gate instance — e.g. so a provider's storage writer and its dashboard store, which both need
/// "has the schema been created yet?", can share one guard instead of each duplicating the same
/// double-checked-lock boilerplate around their own private <c>_schemaReady</c>/<c>_schemaInitLock</c>
/// fields. A key whose initializer throws is not cached, so the next call for that key retries
/// rather than the failure being permanent for the process's lifetime.</summary>
public sealed class SchemaInitGate
{
    private readonly ConcurrentDictionary<(string ConnectionString, string Schema), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<(string ConnectionString, string Schema), bool> _ready = new();

    public async Task RunOnceAsync(string connectionString, string schema, Func<CancellationToken, Task> initialize, CancellationToken ct)
    {
        var key = (connectionString, schema);
        if (_ready.ContainsKey(key))
        {
            return;
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready.ContainsKey(key))
            {
                return;
            }

            await initialize(ct).ConfigureAwait(false);
            _ready[key] = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
