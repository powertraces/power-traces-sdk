using Microsoft.Extensions.DependencyInjection;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace PowerTraces.Sqlite;

/// <summary><c>connectionStringOrPath</c> is either a SQLite connection string
/// (<c>"Data Source=telemetry/traces.db;Default Timeout=30"</c>) or just the database file's path
/// (<c>"telemetry/traces.db"</c>); the file and its folder are created on first use.</summary>
public static class PowerTracesSqliteExtensions
{
    public const string DefaultPath = "powertraces.db";

    /// <summary>Registers a SQLite-backed <see cref="IDashboardStore"/> keyed under
    /// <see cref="PowerTracesDatabaseProvider.Sqlite"/> — resolved by
    /// <c>app.MapPowerTracesDashboard(prefix, PowerTracesDatabaseProvider.Sqlite)</c> from the
    /// PowerTraces.Dashboard package.</summary>
    public static IServiceCollection AddPowerTracesSqliteDashboard(this IServiceCollection services, string connectionStringOrPath = DefaultPath, Action<PowerTracesDashboardOptions>? configure = null)
    {
        var options = new PowerTracesDashboardOptions { ConnectionString = SqliteConnections.Normalize(connectionStringOrPath) };
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<IDashboardStore>(PowerTracesDatabaseProvider.Sqlite, (_, _) => new SqliteDashboardStore(options));
        return services;
    }

    /// <summary>Exports spans to a local SQLite file — no database server, no collector, tables
    /// created automatically.</summary>
    public static TracerProviderBuilder AddPowerTracesSqliteExporter(this TracerProviderBuilder builder, string connectionStringOrPath = DefaultPath, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionStringOrPath, configure);
        return builder.AddPowerTracesExporter(new SqliteStorageWriter(options), options);
    }

    /// <summary>Exports log records to a local SQLite file — no database server, no collector,
    /// tables created automatically.</summary>
    public static LoggerProviderBuilder AddPowerTracesSqliteExporter(this LoggerProviderBuilder builder, string connectionStringOrPath = DefaultPath, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionStringOrPath, configure);
        return builder.AddPowerTracesExporter(new SqliteStorageWriter(options), options);
    }

    private static PowerTracesExporterOptions BuildOptions(string connectionStringOrPath, Action<PowerTracesExporterOptions>? configure)
    {
        var options = new PowerTracesExporterOptions { ConnectionString = SqliteConnections.Normalize(connectionStringOrPath) };
        configure?.Invoke(options);
        return options;
    }
}
