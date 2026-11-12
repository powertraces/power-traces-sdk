using System.Collections.Concurrent;
using System.Data.Common;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage.Sql;

/// <summary>The <see cref="PowerTracesSchema.AttributeKeysTable"/> every SQL provider shares: one
/// row per distinct (attribute key, source) ever written, so the dashboard's attribute picker is a
/// single small read instead of a sample of recent rows. Writers call <see cref="RecordAsync"/>
/// after each batch; a per-database set of keys already recorded means the table is only touched
/// for keys this process hasn't seen before — after warm-up, almost never.</summary>
public static class SqlAttributeKeys
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<(string Key, string Source), byte>> Known = new(StringComparer.Ordinal);

    public static string SourceOf(AttributeScope scope) => scope == AttributeScope.Traces ? "traces" : "logs";

    /// <summary>Records keys of <paramref name="source"/> not yet recorded by this process. Only
    /// called once the batch carrying them has committed, so a key is never listed for telemetry
    /// that was rolled back.</summary>
    public static async Task RecordAsync(
        SqlDialect dialect, string connectionString, string schema, AttributeScope source, IEnumerable<string> keys, CancellationToken ct)
    {
        var sourceText = SourceOf(source);
        var known = Known.GetOrAdd(IgnoreRuleCache.KeyFor(dialect.Name, connectionString, schema), static _ => new());
        var fresh = keys
            .Select(k => AttributeText.Truncate(k, AttributeText.MaxKeyLength))
            .Distinct(StringComparer.Ordinal)
            .Where(k => !known.ContainsKey((k, sourceText)))
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var table = dialect.Table(schema, PowerTracesSchema.AttributeKeysTable);
        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        foreach (var key in fresh)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = dialect.Sql($"""
                INSERT INTO {table} ("Key", "Source", "FirstSeenAt")
                SELECT @Key, @Source, @Now{dialect.FromDual}
                WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE "Key" = @Key AND "Source" = @Source)
                """);
            dialect.AddParameter(cmd, "@Key", key);
            dialect.AddParameter(cmd, "@Source", sourceText);
            dialect.AddParameter(cmd, "@Now", DateTime.UtcNow);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                known.TryAdd((key, sourceText), 0);
            }
            catch (DbException)
            {
                // Most likely another process recorded the same key at the same moment (a
                // primary-key clash). Either way the key isn't marked known, so the next batch
                // carrying it tries again.
            }
        }
    }

    /// <summary>Records the searchable field keys of spans — attribute keys, event attribute keys,
    /// and the paths of values inside JSON attributes (see <see cref="Search.SearchTerms.Fields"/>).</summary>
    public static Task RecordAsync(SqlDialect dialect, string connectionString, string schema, IReadOnlyList<SpanRow> rows, CancellationToken ct) =>
        RecordAsync(dialect, connectionString, schema, AttributeScope.Traces, Search.SearchTerms.FieldKeys(rows), ct);

    public static Task RecordAsync(SqlDialect dialect, string connectionString, string schema, IReadOnlyList<LogRow> rows, CancellationToken ct) =>
        RecordAsync(dialect, connectionString, schema, AttributeScope.Logs, rows.SelectMany(r => AttributeText.ValuesOf(r).Select(a => a.Key)), ct);
}
