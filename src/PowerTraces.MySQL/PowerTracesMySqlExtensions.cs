using Microsoft.Extensions.DependencyInjection;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace PowerTraces.MySQL;

public static class PowerTracesMySqlExtensions
{
    /// <summary>Registers a MySQL/MariaDB-backed <see cref="IDashboardStore"/> keyed under
    /// <see cref="PowerTracesDatabaseProvider.MySql"/> — resolved by
    /// <c>app.MapPowerTracesDashboard(prefix, PowerTracesDatabaseProvider.MySql)</c> from the
    /// PowerTraces.Dashboard package.</summary>
    public static IServiceCollection AddPowerTracesMySqlDashboard(this IServiceCollection services, string connectionString, Action<PowerTracesDashboardOptions>? configure = null)
    {
        var options = new PowerTracesDashboardOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<IDashboardStore>(PowerTracesDatabaseProvider.MySql, (_, _) => new MySqlDashboardStore(options));
        return services;
    }

    /// <summary>Exports spans straight to MySQL/MariaDB via connection string — no collector, no
    /// EF Core, tables created automatically.</summary>
    public static TracerProviderBuilder AddPowerTracesMySqlExporter(this TracerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new MySqlStorageWriter(options), options);
    }

    /// <summary>Exports log records straight to MySQL/MariaDB via connection string — no collector,
    /// no EF Core, tables created automatically.</summary>
    public static LoggerProviderBuilder AddPowerTracesMySqlExporter(this LoggerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new MySqlStorageWriter(options), options);
    }

    private static PowerTracesExporterOptions BuildOptions(string connectionString, Action<PowerTracesExporterOptions>? configure)
    {
        var options = new PowerTracesExporterOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        return options;
    }
}
