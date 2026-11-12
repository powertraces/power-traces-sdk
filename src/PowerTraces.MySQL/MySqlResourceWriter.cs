using System.Collections.Concurrent;
using MySqlConnector;
using PowerTraces;
using PowerTraces.Storage;

namespace PowerTraces.MySQL;

/// <summary>Resolves the DB row Id for a Resource (the process that produced a span/log), reusing
/// one row per distinct attribute set. A process normally has exactly one Resource for its whole
/// lifetime, so the in-memory cache turns this into a one-time lookup per exporter instance rather
/// than a round-trip per export. An id is only cached once the caller's transaction has committed
/// (<see cref="Remember"/>) — caching it earlier would hand a rolled-back row's id to every later
/// batch.
///
/// MySQL has no native GUID type, so <c>Id</c> is stored as VARCHAR(36) — the Guid's string form —
/// bound and read back explicitly via <see cref="Guid.ToString()"/>/<see cref="Guid.Parse(string)"/>
/// rather than relying on driver-specific GUID-column mapping.</summary>
internal sealed class MySqlResourceWriter(string schema)
{
    private readonly ConcurrentDictionary<string, Guid> _cache = new();
    private readonly string _resources = MySqlIdentifiers.Qualify(schema, PowerTracesSchema.ResourcesTable);

    public async Task<Guid> GetOrCreateAsync(MySqlConnection connection, MySqlTransaction transaction, ResourceInfo resource, CancellationToken ct)
    {
        if (_cache.TryGetValue(resource.AttributesJson, out var cached))
        {
            return cached;
        }

        var existing = await FindAsync(connection, transaction, resource.AttributesJson, ct).ConfigureAwait(false);
        if (existing is { } id)
        {
            await TouchAsync(connection, transaction, id, ct).ConfigureAwait(false);
            return id;
        }

        var newId = Guid.NewGuid();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {_resources}
                    (`Id`, `ServiceName`, `AttributesJson`, `SchemaUrl`, `FirstSeenAt`, `LastSeenAt`)
                VALUES (@Id, @ServiceName, @AttributesJson, @SchemaUrl, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """;
            cmd.Parameters.AddWithValue("@Id", newId.ToString());
            cmd.Parameters.AddWithValue("@ServiceName", (object?)resource.ServiceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AttributesJson", resource.AttributesJson);
            cmd.Parameters.AddWithValue("@SchemaUrl", (object?)resource.SchemaUrl ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return newId;
    }

    /// <summary>Call after the transaction that resolved <paramref name="id"/> commits.</summary>
    public void Remember(ResourceInfo resource, Guid id) => _cache[resource.AttributesJson] = id;

    private async Task<Guid?> FindAsync(MySqlConnection connection, MySqlTransaction transaction, string attributesJson, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT `Id` FROM {_resources}
            WHERE `AttributesHash` = MD5(@AttributesJson) AND `AttributesJson` = @AttributesJson
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@AttributesJson", attributesJson);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string s ? Guid.Parse(s) : null;
    }

    private async Task TouchAsync(MySqlConnection connection, MySqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"UPDATE {_resources} SET `LastSeenAt` = UTC_TIMESTAMP(6) WHERE `Id` = @Id;";
        cmd.Parameters.AddWithValue("@Id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
