using System.Data;
using System.Data.Common;

namespace PowerTraces.Storage.Sql;

/// <summary>What differs between the SQL databases the shared <see cref="SqlDashboardStore"/>
/// queries: identifier quoting, paging syntax, LIKE escaping, and how a few parameter types must be
/// bound. Each SQL provider package supplies one; implement it (plus an <see cref="IStorageWriter"/>)
/// to add another SQL database.
///
/// Query text is written once, with ANSI double-quoted identifiers, and passed through
/// <see cref="Sql"/> to become this dialect's quoting — so no string literal in a query template
/// may contain a double quote.</summary>
public abstract class SqlDialect
{
    /// <summary>Short stable name, used in cache keys (e.g. "sqlserver").</summary>
    public abstract string Name { get; }

    public abstract DbConnection CreateConnection(string connectionString);

    /// <summary>Rewrites a query template's <c>"identifier"</c> quoting into this dialect's.</summary>
    public abstract string Sql(string template);

    /// <summary>A schema-qualified, quoted table name.</summary>
    public abstract string Table(string schema, string table);

    /// <summary>Paging clause placed after ORDER BY, reading the <c>@Skip</c>/<c>@Take</c> parameters.</summary>
    public abstract string Paging { get; }

    /// <summary><c>SELECT {columns} {fromWhere}</c> limited to <paramref name="limit"/> rows (no
    /// ordering required).</summary>
    public abstract string SelectLimited(string columns, string fromWhere, int limit);

    /// <summary>A case-insensitive substring predicate: <paramref name="column"/> contains the
    /// (already <see cref="EscapeLike"/>-escaped) text bound to <paramref name="parameter"/>.</summary>
    public abstract string ContainsPredicate(string column, string parameter);

    /// <summary>Which bucket <paramref name="column"/> falls in: whole <c>@BucketSeconds</c>
    /// intervals elapsed since <c>@From</c>, as an integer.</summary>
    public abstract string BucketIndex(string column);

    /// <summary>Milliseconds from <paramref name="start"/> to <paramref name="end"/> as a
    /// floating-point expression (so AVG doesn't truncate).</summary>
    public abstract string MillisecondsBetween(string start, string end);

    /// <summary>The text of one attribute inside an attribute-JSON column, or NULL when the row
    /// doesn't have it: a string attribute's own text, anything else (number, boolean, array,
    /// object) as its JSON text — the same text <see cref="AttributeText.Flatten"/> produces, so
    /// <c>http.response.status_code = 500</c> matches the filter value <c>"500"</c> however it was
    /// recorded. <paramref name="keyParameter"/> is bound to <see cref="JsonAttributeKey"/>.</summary>
    public abstract string JsonAttributeText(string jsonColumn, string keyParameter);

    /// <summary>What <see cref="JsonAttributeText"/>'s key parameter is bound to: by default a JSON
    /// path naming the key as one member (<c>$."http.route"</c> — keys contain dots).</summary>
    public virtual string JsonAttributeKey(string key) =>
        "$.\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>A case-insensitive substring match on a <see cref="JsonAttributeText"/>
    /// expression. Defaults to <see cref="ContainsPredicate"/>; a database whose JSON functions
    /// return case-sensitive text overrides it.</summary>
    public virtual string JsonContainsPredicate(string expression, string parameter) => ContainsPredicate(expression, parameter);

    /// <summary>Whether the trace search index is the <see cref="PowerTracesSchema.SearchTermsTable"/>
    /// table (true), or something the provider keeps elsewhere — PostgreSQL keeps it in a
    /// <c>tsvector</c> column of the trace summary.</summary>
    public virtual bool UsesSearchTermsTable => true;

    /// <summary>Whether the <see cref="PowerTracesSchema.SearchTermsTable"/> table has an index on
    /// its trace id. Without one (SQL Server, to save space), a trace's terms aren't deleted with the
    /// trace — searches join to the live traces, so leftover terms never match — and retention
    /// removes the leftovers once they're past the retention cutoff.</summary>
    public virtual bool SearchTermsIndexedByTrace => true;

    /// <summary>The search-term table's trace id as the 32-character lowercase hex the trace tables
    /// use, for a term-table alias.</summary>
    public virtual string SearchTraceIdText(string alias) => $"{alias}.\"TraceId\"";

    /// <summary>A trace table's trace id column converted to how the search-term table stores it.</summary>
    public virtual string SearchTraceIdOf(string traceIdColumn) => traceIdColumn;

    /// <summary>A trace id as the search-term table stores it.</summary>
    public virtual object SearchTraceIdValue(string traceId) => traceId;

    /// <summary>A DELETE of at most <paramref name="limit"/> rows of <paramref name="table"/>, aliased
    /// <paramref name="alias"/>, matching <paramref name="where"/>. Needed only by dialects without
    /// <see cref="SearchTermsIndexedByTrace"/>.</summary>
    public virtual string DeleteLimited(string table, string alias, string where, int limit) =>
        throw new NotSupportedException($"The {Name} dialect does not implement {nameof(DeleteLimited)}.");

    /// <summary>The insert that skips rows whose primary key already exists (the search index's
    /// rows are keyed on the full row where the database clusters on it).</summary>
    public virtual string InsertIgnoreInto => "INSERT INTO";

    /// <summary>Escapes LIKE wildcards so user text is matched literally.</summary>
    public abstract string EscapeLike(string value);

    /// <summary>Appended to a <c>SELECT</c> that has a WHERE but no FROM (MySQL needs
    /// <c>FROM DUAL</c> there).</summary>
    public virtual string FromDual => "";

    /// <summary>Binds <paramref name="value"/> as <paramref name="name"/>. <paramref name="ansi"/>
    /// marks values compared against VARCHAR (not NVARCHAR) columns — trace/span ids — which SQL
    /// Server must receive as non-Unicode, or it converts the column side and can't seek its
    /// index.</summary>
    public virtual DbParameter AddParameter(DbCommand command, string name, object? value, bool ansi = false)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => value,
        };

        if (ansi && value is string)
        {
            parameter.DbType = DbType.AnsiString;
        }

        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Every stored timestamp is UTC; some drivers hand it back as
    /// <see cref="DateTimeKind.Unspecified"/>, which would serialize without a "Z" and be
    /// misread as local time by the browser.</summary>
    public static DateTime ReadUtc(DbDataReader reader, int ordinal) =>
        DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    public static DateTime? ReadUtcOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    public static string? ReadStringOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Guids are stored natively on SQL Server/PostgreSQL and as text on MySQL.</summary>
    public static Guid ReadGuid(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        byte[] b => new Guid(b),
        var other => throw new InvalidCastException($"Can't read {other.GetType()} as a Guid."),
    };

    /// <summary>Counts/sums come back as int, long or decimal depending on the database.</summary>
    public static long ReadInt64(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value);

    /// <summary>BIT, BOOLEAN and TINYINT(1) all read as a boolean.</summary>
    public static bool ReadBool(DbDataReader reader, int ordinal) => Convert.ToBoolean(reader.GetValue(ordinal));
}
