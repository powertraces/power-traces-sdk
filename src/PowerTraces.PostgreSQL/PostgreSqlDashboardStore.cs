using System.Data.Common;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Search;
using PowerTraces.Storage.Sql;

namespace PowerTraces.PostgreSQL;

/// <summary>PostgreSQL's dashboard read side: the shared <see cref="SqlDashboardStore"/>, with the
/// trace search index in a <c>tsvector</c> column of the trace summary (GIN-indexed) instead of
/// the <see cref="PowerTracesSchema.SearchTermsTable"/> table. A search becomes one
/// <c>@@ tsquery</c> predicate whose lexemes are the search terms themselves, quoted, so nothing
/// in them is parsed or normalized.</summary>
public sealed class PostgreSqlDashboardStore(PowerTracesDashboardOptions options)
    : SqlDashboardStore(PostgreSqlDialect.Instance, options)
{
    private const int FieldValueSampleSize = 2_000;
    private const int FieldValueCount = 20;

    // PostgreSqlSchemaInitializer guards its own idempotency (shared with
    // PostgreSqlStorageWriter for the same database) — nothing to duplicate here.
    public override Task EnsureSchemaAsync(CancellationToken ct) =>
        PostgreSqlSchemaInitializer.EnsureCreatedAsync(Options.ConnectionString, Options.Schema, ct);

    protected override void AppendTermPredicates(DbCommand cmd, StringBuilder where, TraceSearchPlan plan, bool boundedByFrom)
    {
        if (!plan.UsesIndex)
        {
            return;
        }

        var parts = plan.Required.Select(group => "(" + string.Join(" | ", group.Select(Lexeme)) + ")")
            .Concat(plan.Excluded.Select(lookup => "!" + Lexeme(lookup)));
        where.Append($""" AND t."{PostgreSqlSchemaInitializer.SearchColumn}" @@ @SearchQuery::tsquery""");
        cmd.Parameters.Add(new NpgsqlParameter("@SearchQuery", NpgsqlDbType.Text) { Value = string.Join(" & ", parts) });
    }

    /// <summary>The field's values among the newest <see cref="FieldValueSampleSize"/> traces that
    /// have one starting with <paramref name="prefix"/> — found through the GIN index, then read off
    /// those traces' term lists.</summary>
    public override async Task<IReadOnlyList<FacetValueDto>> ListFieldValuesAsync(string key, string? prefix, DateTime since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var low = SearchTerms.Attribute(key.Trim(), prefix ?? "");
        var valueStart = SearchTerms.Attribute(key.Trim(), "").Length;
        var column = PostgreSqlSchemaInitializer.SearchColumn;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT x.term, COUNT(*) FROM (
                SELECT unnest(tsvector_to_array(s."{column}")) AS term FROM (
                    SELECT t."{column}" FROM {TracesTable} t
                    WHERE t."{column}" @@ @Query::tsquery AND t."StartTime" >= @Since
                    ORDER BY t."StartTime" DESC LIMIT {FieldValueSampleSize}
                ) s
            ) x
            WHERE x.term >= @Low AND x.term < @High
            GROUP BY x.term ORDER BY COUNT(*) DESC, x.term LIMIT {FieldValueCount}
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@Query", NpgsqlDbType.Text) { Value = Lexeme(new TermLookup(low, true)) });
        cmd.Parameters.Add(new NpgsqlParameter("@Since", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(since, DateTimeKind.Utc) });
        cmd.Parameters.Add(new NpgsqlParameter("@Low", NpgsqlDbType.Text) { Value = low });
        cmd.Parameters.Add(new NpgsqlParameter("@High", NpgsqlDbType.Text) { Value = SearchTerms.PrefixUpperBound(low) });

        var values = new List<FacetValueDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var term = reader.GetString(0);
            if (term.Length > valueStart)
            {
                values.Add(new FacetValueDto(term[valueStart..], (int)reader.GetInt64(1)));
            }
        }

        return values;
    }

    protected override async Task WriteBackfillTermsAsync(QueryConnection connection, IReadOnlyList<TraceTerms> traces, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        var column = PostgreSqlSchemaInitializer.SearchColumn;
        cmd.CommandText = $"""
            UPDATE {TracesTable} t SET "{column}" = t."{column}" || array_to_tsvector(string_to_array(u.terms, E'\x1F'))
            FROM unnest(@Ids, @Terms) AS u(id, terms)
            WHERE t."TraceId" = u.id
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@Ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = traces.Select(t => t.TraceId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("@Terms", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = traces.Select(t => string.Join('\u001F', t.Terms)).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A term as a quoted tsquery lexeme — quotes doubled, backslashes escaped — with
    /// <c>:*</c> for a prefix lookup.</summary>
    private static string Lexeme(TermLookup lookup) =>
        "'" + lookup.Term.Replace("\\", "\\\\").Replace("'", "''") + "'" + (lookup.Prefix ? ":*" : "");
}
