using System.Data.Common;
using System.Text;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MySQL;

/// <summary>MySQL/MariaDB's dashboard read side: the shared <see cref="SqlDashboardStore"/>, with
/// the trace search index in a FULLTEXT-indexed column of the trace summary (see
/// <see cref="MySqlSearchText"/>) instead of the <see cref="PowerTracesSchema.SearchTermsTable"/>
/// table. A search becomes one <c>MATCH ... AGAINST</c> in boolean mode: <c>+(a b*)</c> per
/// required group, <c>-c</c> per exclusion.</summary>
public sealed class MySqlDashboardStore(PowerTracesDashboardOptions options)
    : SqlDashboardStore(MySqlDialect.Instance, options)
{
    private const int FieldValueSampleSize = 2_000;
    private const int FieldValueCount = 20;

    private static string Match => $"MATCH(t.`{MySqlSearchText.Column}`) AGAINST(@SearchQuery IN BOOLEAN MODE)";

    // MySqlSchemaInitializer guards its own idempotency (shared with MySqlStorageWriter for the
    // same database) — nothing to duplicate here.
    public override Task EnsureSchemaAsync(CancellationToken ct) =>
        MySqlSchemaInitializer.EnsureCreatedAsync(Options.ConnectionString, Options.Schema, ct);

    protected override void AppendTermPredicates(DbCommand cmd, StringBuilder where, TraceSearchPlan plan, bool boundedByFrom)
    {
        if (!plan.UsesIndex)
        {
            return;
        }

        if (plan.Required.Count == 0)
        {
            // Boolean mode matches nothing without a positive term — exclude the other way round.
            where.Append($" AND NOT {Match}");
            Dialect.AddParameter(cmd, "@SearchQuery", string.Join(' ', plan.Excluded.Select(Token)));
            return;
        }

        var query = plan.Required.Select(group => "+(" + string.Join(' ', group.Select(Token)) + ")")
            .Concat(plan.Excluded.Select(lookup => "-" + Token(lookup)));
        where.Append($" AND {Match}");
        Dialect.AddParameter(cmd, "@SearchQuery", string.Join(' ', query));
    }

    /// <summary>The field's values among the newest <see cref="FieldValueSampleSize"/> traces that
    /// have one starting with <paramref name="prefix"/>, read off those traces' search text.</summary>
    public override async Task<IReadOnlyList<FacetValueDto>> ListFieldValuesAsync(string key, string? prefix, DateTime since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var low = SearchTerms.Attribute(key.Trim(), prefix ?? "");
        var head = SearchTerms.Attribute(key.Trim(), "");
        var tokenPrefix = MySqlSearchText.EncodePrefix(low);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.`{MySqlSearchText.Column}` FROM {TracesTable} t
            WHERE {Match} AND t.`StartTime` >= @Since
            ORDER BY t.`StartTime` DESC LIMIT {FieldValueSampleSize}
            """;
        Dialect.AddParameter(cmd, "@SearchQuery", "+" + tokenPrefix + "*");
        Dialect.AddParameter(cmd, "@Since", since);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in (reader.IsDBNull(0) ? "" : reader.GetString(0)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith(tokenPrefix, StringComparison.OrdinalIgnoreCase)
                        && MySqlSearchText.Decode(token) is { } term
                        && term.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                        && term.Length > head.Length
                        && seen.Add(term))
                    {
                        var value = term[head.Length..];
                        counts[value] = counts.GetValueOrDefault(value) + 1;
                    }
                }
            }
        }

        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(FieldValueCount).Select(c => new FacetValueDto(c.Key, c.Value)).ToList();
    }

    protected override async Task WriteBackfillTermsAsync(QueryConnection connection, IReadOnlyList<TraceTerms> traces, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var trace in traces)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                UPDATE {TracesTable} SET `{MySqlSearchText.Column}` = CONCAT_WS(' ', `{MySqlSearchText.Column}`, @Text) WHERE `TraceId` = @TraceId
                """;
            Dialect.AddParameter(cmd, "@Text", MySqlSearchText.Join(trace.Terms));
            Dialect.AddParameter(cmd, "@TraceId", trace.TraceId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string Token(TermLookup lookup) =>
        lookup.Prefix ? MySqlSearchText.EncodePrefix(lookup.Term) + "*" : MySqlSearchText.Encode(lookup.Term);
}
