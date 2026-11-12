using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MSSQL;

/// <summary>SQL Server's take on the shared SQL layer: bracket quoting, OFFSET/FETCH paging,
/// TOP for limits, bracket-escaped LIKE wildcards, and DATETIME2/VARCHAR parameter typing so
/// comparisons against indexed columns never force an implicit conversion on the column side.</summary>
public sealed partial class SqlServerDialect : SqlDialect
{
    public static readonly SqlServerDialect Instance = new();

    public override string Name => "sqlserver";

    public override DbConnection CreateConnection(string connectionString) => SqlServerConnections.Create(connectionString);

    public override string Sql(string template) => QuotedIdentifier().Replace(template, "[$1]");

    public override string Table(string schema, string table) => PowerTracesSchema.Qualify(schema, table);

    public override string Paging => "OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

    public override string SelectLimited(string columns, string fromWhere, int limit) =>
        $"SELECT TOP ({limit}) {columns} {fromWhere}";

    // Case sensitivity follows the column's collation — case-insensitive under SQL Server's
    // default collations.
    public override string ContainsPredicate(string column, string parameter) =>
        $"{column} LIKE '%' + {parameter} + '%'";

    public override string BucketIndex(string column) => $"DATEDIFF_BIG(second, @From, {column}) / @BucketSeconds";

    public override string MillisecondsBetween(string start, string end) => $"CAST(DATEDIFF_BIG(millisecond, {start}, {end}) AS FLOAT)";

    // JSON_VALUE returns scalars (booleans as true/false), JSON_QUERY objects and arrays; each
    // yields NULL for the other kind. A parameterized path needs SQL Server 2017+.
    public override string JsonAttributeText(string jsonColumn, string keyParameter) =>
        $"COALESCE(JSON_VALUE({jsonColumn}, {keyParameter}), JSON_QUERY({jsonColumn}, {keyParameter}))";

    // The search-term table keeps trace ids as BINARY(16) and has no index on them: half the width
    // in its clustered index, and no second index (see SqlDialect.SearchTermsIndexedByTrace).
    public override bool SearchTermsIndexedByTrace => false;

    public override string SearchTraceIdText(string alias) => $"LOWER(CONVERT(VARCHAR(32), {alias}.[TraceId], 2))";

    public override string SearchTraceIdOf(string traceIdColumn) => $"CONVERT(BINARY(16), {traceIdColumn}, 2)";

    public override object SearchTraceIdValue(string traceId) => Convert.FromHexString(traceId);

    public override string DeleteLimited(string table, string alias, string where, int limit) =>
        $"DELETE TOP ({limit}) {alias} FROM {table} {alias} WHERE {where}";

    public override string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    public override DbParameter AddParameter(DbCommand command, string name, object? value, bool ansi = false)
    {
        var parameter = base.AddParameter(command, name, value, ansi);
        if (value is DateTime)
        {
            // AddWithValue-style inference picks the legacy DATETIME type (3ms precision);
            // every timestamp column here is DATETIME2.
            ((SqlParameter)parameter).SqlDbType = SqlDbType.DateTime2;
        }

        return parameter;
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedIdentifier();
}
