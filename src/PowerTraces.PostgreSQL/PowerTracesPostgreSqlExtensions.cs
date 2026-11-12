using Microsoft.Extensions.DependencyInjection;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace PowerTraces.PostgreSQL;

public static class PowerTracesPostgreSqlExtensions
{
    /// <summary>Registers a PostgreSQL-backed <see cref="IDashboardStore"/> keyed under
    /// <see cref="PowerTracesDatabaseProvider.PostgreSql"/> — resolved by
    /// <c>app.MapPowerTracesDashboard(prefix, PowerTracesDatabaseProvider.PostgreSql)</c> from the
    /// PowerTraces.Dashboard package.</summary>
    public static IServiceCollection AddPowerTracesPostgreSqlDashboard(this IServiceCollection services, string connectionString, Action<PowerTracesDashboardOptions>? configure = null)
    {
        var options = new PowerTracesDashboardOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<IDashboardStore>(PowerTracesDatabaseProvider.PostgreSql, (_, _) => new PostgreSqlDashboardStore(options));
        return services;
    }

    /// <summary>Exports spans straight to PostgreSQL via connection string — no collector, no EF
    /// Core, tables created automatically.</summary>
    public static TracerProviderBuilder AddPowerTracesPostgreSqlExporter(this TracerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new PostgreSqlStorageWriter(options), options);
    }

    /// <summary>Exports log records straight to PostgreSQL via connection string — no collector,
    /// no EF Core, tables created automatically.</summary>
    public static LoggerProviderBuilder AddPowerTracesPostgreSqlExporter(this LoggerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new PostgreSqlStorageWriter(options), options);
    }

    private static PowerTracesExporterOptions BuildOptions(string connectionString, Action<PowerTracesExporterOptions>? configure)
    {
        var options = new PowerTracesExporterOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        return options;
    }
}
