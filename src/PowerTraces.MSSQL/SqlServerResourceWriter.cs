using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using PowerTraces;
using PowerTraces.Storage;

namespace PowerTraces.MSSQL;

/// <summary>Resolves the DB row Id for a Resource (the process that produced a span/log), reusing
/// one row per distinct attribute set. A process normally has exactly one Resource for its whole
/// lifetime, so the in-memory cache turns this into a one-time lookup per exporter instance rather
/// than a round-trip per export. An id is only cached once the caller's transaction has committed
/// (<see cref="Remember"/>) — caching it earlier would hand a rolled-back row's id to every later
/// batch.</summary>
internal sealed class SqlServerResourceWriter(string schema)
{
    private readonly ConcurrentDictionary<string, Guid> _cache = new();

    public async Task<Guid> GetOrCreateAsync(SqlConnection connection, SqlTransaction transaction, ResourceInfo resource, CancellationToken ct)
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
                INSERT INTO {PowerTracesSchema.Qualify(schema, PowerTracesSchema.ResourcesTable)}
                    (Id, ServiceName, AttributesJson, SchemaUrl, FirstSeenAt, LastSeenAt)
                VALUES (@Id, @ServiceName, @AttributesJson, @SchemaUrl, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;
            cmd.Parameters.AddWithValue("@Id", newId);
            cmd.Parameters.AddWithValue("@ServiceName", (object?)resource.ServiceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AttributesJson", resource.AttributesJson);
            cmd.Parameters.AddWithValue("@SchemaUrl", (object?)resource.SchemaUrl ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return newId;
    }

    /// <summary>Call after the transaction that resolved <paramref name="id"/> commits.</summary>
    public void Remember(ResourceInfo resource, Guid id) => _cache[resource.AttributesJson] = id;

    private async Task<Guid?> FindAsync(SqlConnection connection, SqlTransaction transaction, string attributesJson, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT TOP (1) Id FROM {PowerTracesSchema.Qualify(schema, PowerTracesSchema.ResourcesTable)}
            WHERE AttributesHash = CHECKSUM(@AttributesJson) AND AttributesJson = @AttributesJson;
            """;
        cmd.Parameters.AddWithValue("@AttributesJson", attributesJson);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is Guid id ? id : null;
    }

    private async Task TouchAsync(SqlConnection connection, SqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"UPDATE {PowerTracesSchema.Qualify(schema, PowerTracesSchema.ResourcesTable)} SET LastSeenAt = SYSUTCDATETIME() WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
