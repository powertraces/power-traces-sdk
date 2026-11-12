using System.Data.Common;
using Microsoft.Data.Sqlite;
using PowerTraces.Storage.Sql;

namespace PowerTraces.Sqlite;

/// <summary>SQLite's take on the shared SQL layer. SQLite has no schemas, so the schema name
/// prefixes table names (<c>"powertraces_Spans"</c>); timestamps are ISO-8601 text (how
/// Microsoft.Data.Sqlite binds <see cref="DateTime"/>), which sorts chronologically and which
/// <c>julianday()</c> does arithmetic on; Guids are bound as their string form; booleans are 0/1.</summary>
public sealed class SqliteDialect : SqlDialect
{
    public static readonly SqliteDialect Instance = new();

    public override string Name => "sqlite";

    public override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public override string Sql(string template) => template;

    public override string Table(string schema, string table) => Qualify(schema, table);

    public static string Qualify(string schema, string table) => $"\"{schema}_{table}\"";

    public override string Paging => "LIMIT @Take OFFSET @Skip";

    public override string SelectLimited(string columns, string fromWhere, int limit) =>
        $"SELECT {columns} {fromWhere} LIMIT {limit}";

    // LIKE is case-insensitive for ASCII letters only.
    public override string ContainsPredicate(string column, string parameter) =>
        $"{column} LIKE '%' || {parameter} || '%' ESCAPE '\\'";

    public override string BucketIndex(string column) =>
        $"CAST((julianday({column}) - julianday(@From)) * 86400 / @BucketSeconds AS INTEGER)";

    public override string MillisecondsBetween(string start, string end) =>
        $"((julianday({end}) - julianday({start})) * 86400000.0)";

    // json_extract returns SQL values (1/0 for booleans, numbers as numbers), so booleans are named
    // explicitly and everything else cast to text — objects and arrays come back as their JSON.
    public override string JsonAttributeText(string jsonColumn, string keyParameter) =>
        $"(CASE json_type({jsonColumn}, {keyParameter}) WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' WHEN 'null' THEN NULL " +
        $"ELSE CAST(json_extract({jsonColumn}, {keyParameter}) AS TEXT) END)";

    public override string InsertIgnoreInto => "INSERT OR IGNORE INTO";

    public override string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public override DbParameter AddParameter(DbCommand command, string name, object? value, bool ansi = false) =>
        base.AddParameter(command, name, value is Guid g ? g.ToString() : value, ansi);
}
