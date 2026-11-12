using System.Data.Common;
using MySqlConnector;
using PowerTraces.Storage.Sql;

namespace PowerTraces.MySQL;

/// <summary>MySQL/MariaDB's take on the shared SQL layer: backtick quoting, LIMIT/OFFSET paging,
/// CONCAT-built LIKE patterns (MySQL's <c>+</c> is arithmetic, not concatenation), backslash-escaped
/// wildcards, and Guids bound as text since ids are stored as VARCHAR(36).</summary>
public sealed class MySqlDialect : SqlDialect
{
    public static readonly MySqlDialect Instance = new();

    public override string Name => "mysql";

    public override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public override string Sql(string template) => template.Replace('"', '`');

    public override string Table(string schema, string table) => MySqlIdentifiers.Qualify(schema, table);

    public override string Paging => "LIMIT @Take OFFSET @Skip";

    public override string SelectLimited(string columns, string fromWhere, int limit) =>
        $"SELECT {columns} {fromWhere} LIMIT {limit}";

    // Case sensitivity follows the column's collation — case-insensitive under MySQL's default
    // utf8mb4 collations.
    public override string ContainsPredicate(string column, string parameter) =>
        $"{column} LIKE CONCAT('%', {parameter}, '%')";

    public override string BucketIndex(string column) => $"FLOOR(TIMESTAMPDIFF(SECOND, @From, {column}) / @BucketSeconds)";

    public override string MillisecondsBetween(string start, string end) => $"(TIMESTAMPDIFF(MICROSECOND, {start}, {end}) / 1000.0)";

    public override string JsonAttributeText(string jsonColumn, string keyParameter) =>
        $"JSON_UNQUOTE(JSON_EXTRACT({jsonColumn}, {keyParameter}))";

    // JSON functions return binary-collated (case-sensitive) text on MySQL, so fold case explicitly.
    public override string JsonContainsPredicate(string expression, string parameter) =>
        $"LOWER({expression}) LIKE CONCAT('%', LOWER({parameter}), '%')";

    // The search index is a FULLTEXT-indexed column of the trace summary (see MySqlSearchText).
    public override bool UsesSearchTermsTable => false;

    public override string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public override string FromDual => " FROM DUAL";

    public override DbParameter AddParameter(DbCommand command, string name, object? value, bool ansi = false) =>
        base.AddParameter(command, name, value is Guid g ? g.ToString() : value, ansi);
}
