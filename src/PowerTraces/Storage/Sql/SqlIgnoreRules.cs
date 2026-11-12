using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage.Sql;

/// <summary>Reads a SQL database's ignore rules through <see cref="IgnoreRuleCache"/> — shared by
/// each SQL provider's storage writer (which applies them) and dashboard store (which edits
/// them), so both hit one cache entry and an edit is visible to the next export.</summary>
public static class SqlIgnoreRules
{
    public static Task<IReadOnlyList<IgnoreRuleDto>> ListAsync(SqlDialect dialect, string connectionString, string schema, CancellationToken ct) =>
        IgnoreRuleCache.GetAsync(
            IgnoreRuleCache.KeyFor(dialect.Name, connectionString, schema),
            loadCt => LoadAsync(dialect, connectionString, schema, loadCt),
            ct);

    public static void Invalidate(SqlDialect dialect, string connectionString, string schema) =>
        IgnoreRuleCache.Invalidate(IgnoreRuleCache.KeyFor(dialect.Name, connectionString, schema));

    private static async Task<IReadOnlyList<IgnoreRuleDto>> LoadAsync(SqlDialect dialect, string connectionString, string schema, CancellationToken ct)
    {
        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""
            SELECT "Id", "Kind", "Name", "CreatedAt" FROM {dialect.Table(schema, PowerTracesSchema.IgnoreRulesTable)} ORDER BY "CreatedAt" DESC
            """);

        var rules = new List<IgnoreRuleDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rules.Add(new IgnoreRuleDto(
                SqlDialect.ReadGuid(reader, 0),
                reader.GetString(1),
                reader.GetString(2),
                SqlDialect.ReadUtc(reader, 3)));
        }

        return rules;
    }

    /// <summary>Deletes every row of these traces from the span, search-index and trace-summary tables — the
    /// <see cref="IStorageWriter.DeleteTracesAsync"/> implementation every SQL provider shares.</summary>
    public static async Task DeleteTracesAsync(SqlDialect dialect, string connectionString, string schema, IReadOnlyCollection<string> traceIds, CancellationToken ct)
    {
        if (traceIds.Count == 0)
        {
            return;
        }

        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var chunk in traceIds.Chunk(500))
        {
            foreach (var table in new[] { PowerTracesSchema.SpansTable, PowerTracesSchema.SearchTermsTable, PowerTracesSchema.TracesTable })
            {
                if (table == PowerTracesSchema.SearchTermsTable && !(dialect.UsesSearchTermsTable && dialect.SearchTermsIndexedByTrace))
                {
                    continue;
                }

                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                var names = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    names[i] = $"@t{i}";
                    dialect.AddParameter(cmd, names[i], chunk[i], ansi: true);
                }

                cmd.CommandText = dialect.Sql($"""DELETE FROM {dialect.Table(schema, table)} WHERE "TraceId" IN ({string.Join(", ", names)})""");
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
