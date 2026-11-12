using System.Data.Common;
using Npgsql;
using PowerTraces.Storage.Sql;

namespace PowerTraces.PostgreSQL;

/// <summary>PostgreSQL's take on the shared SQL layer: native double-quoted identifiers,
/// LIMIT/OFFSET paging, ILIKE for case-insensitive substring matching, backslash-escaped LIKE
/// wildcards.</summary>
public sealed class PostgreSqlDialect : SqlDialect
{
    public static readonly PostgreSqlDialect Instance = new();

    public override string Name => "postgresql";

    public override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public override string Sql(string template) => template;

    public override string Table(string schema, string table) => PostgreSqlIdentifiers.Qualify(schema, table);

    public override string Paging => "LIMIT @Take OFFSET @Skip";

    public override string SelectLimited(string columns, string fromWhere, int limit) =>
        $"SELECT {columns} {fromWhere} LIMIT {limit}";

    public override string ContainsPredicate(string column, string parameter) =>
        $"{column} ILIKE '%' || {parameter} || '%'";

    public override string BucketIndex(string column) => $"floor(extract(epoch from ({column} - @From)) / @BucketSeconds)::bigint";

    public override string MillisecondsBetween(string start, string end) => $"(extract(epoch from ({end} - {start})) * 1000)::float8";

    // ->> takes the key itself, not a JSON path.
    public override string JsonAttributeText(string jsonColumn, string keyParameter) => $"({jsonColumn}::jsonb ->> {keyParameter})";

    public override string JsonAttributeKey(string key) => key;

    // The search index is a tsvector column on the trace summary (see PostgreSqlDashboardStore).
    public override bool UsesSearchTermsTable => false;

    public override string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
