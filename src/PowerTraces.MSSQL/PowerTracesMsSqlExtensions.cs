using Microsoft.Extensions.DependencyInjection;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace PowerTraces.MSSQL;

public static class PowerTracesMsSqlExtensions
{
    /// <summary>Registers a SQL Server-backed <see cref="IDashboardStore"/> keyed under
    /// <see cref="PowerTracesDatabaseProvider.SqlServer"/> — resolved by
    /// <c>app.MapPowerTracesDashboard(prefix, PowerTracesDatabaseProvider.SqlServer)</c> from the
    /// PowerTraces.Dashboard package.</summary>
    public static IServiceCollection AddPowerTracesMsSqlDashboard(this IServiceCollection services, string connectionString, Action<PowerTracesDashboardOptions>? configure = null)
    {
        var options = new PowerTracesDashboardOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<IDashboardStore>(PowerTracesDatabaseProvider.SqlServer, (_, _) => new SqlServerDashboardStore(options));
        return services;
    }

    /// <summary>Exports spans straight to SQL Server via connection string — no collector, no EF
    /// Core, tables created automatically.</summary>
    public static TracerProviderBuilder AddPowerTracesMsSqlExporter(this TracerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new SqlServerStorageWriter(options), options);
    }

    /// <summary>Exports log records straight to SQL Server via connection string — no collector,
    /// no EF Core, tables created automatically.</summary>
    public static LoggerProviderBuilder AddPowerTracesMsSqlExporter(this LoggerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new SqlServerStorageWriter(options), options);
    }

    private static PowerTracesExporterOptions BuildOptions(string connectionString, Action<PowerTracesExporterOptions>? configure)
    {
        var options = new PowerTracesExporterOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        return options;
    }
}
