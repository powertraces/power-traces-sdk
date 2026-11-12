using Microsoft.Data.Sqlite;

namespace PowerTraces.Sqlite;

/// <summary>Accepts either a full SQLite connection string or just a file path.</summary>
public static class SqliteConnections
{
    public static string Normalize(string connectionStringOrPath) =>
        connectionStringOrPath.Contains('=')
            ? connectionStringOrPath
            : new SqliteConnectionStringBuilder { DataSource = connectionStringOrPath }.ToString();
}
