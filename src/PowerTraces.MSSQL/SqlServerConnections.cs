using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace PowerTraces.MSSQL;

/// <summary>Gives this library's connections their own identity. SqlClient pools connections per
/// exact connection string, so a host app and PowerTraces given the same string would otherwise
/// share one pool, and the exporter and dashboard would compete with the app for its connections.
/// Unless the string already names an application, PowerTraces connects as
/// <see cref="ApplicationName"/>: that's a separate pool, and the library's sessions are easy to
/// tell apart in <c>sys.dm_exec_sessions.program_name</c>.</summary>
public static class SqlServerConnections
{
    public const string ApplicationName = "PowerTraces";

    private static readonly ConcurrentDictionary<string, string> Prepared = new(StringComparer.Ordinal);

    public static SqlConnection Create(string connectionString) => new(Prepare(connectionString));

    public static string Prepare(string connectionString) =>
        Prepared.GetOrAdd(connectionString, static cs =>
        {
            var builder = new SqlConnectionStringBuilder(cs);
            if (string.IsNullOrWhiteSpace(builder.ApplicationName) ||
                builder.ApplicationName.EndsWith("Microsoft SqlClient Data Provider", StringComparison.Ordinal))
            {
                builder.ApplicationName = ApplicationName;
            }

            return builder.ConnectionString;
        });
}
