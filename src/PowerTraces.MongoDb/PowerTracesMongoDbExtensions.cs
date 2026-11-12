using Microsoft.Extensions.DependencyInjection;
using PowerTraces.Dashboard;
using PowerTraces.Dashboard.Query;
using PowerTraces.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace PowerTraces.MongoDb;

public static class PowerTracesMongoDbExtensions
{
    /// <summary>Registers a MongoDB-backed <see cref="IDashboardStore"/> keyed under
    /// <see cref="PowerTracesDatabaseProvider.MongoDb"/> — resolved by
    /// <c>app.MapPowerTracesDashboard(prefix, PowerTracesDatabaseProvider.MongoDb)</c> from the
    /// PowerTraces.Dashboard package.</summary>
    public static IServiceCollection AddPowerTracesMongoDbDashboard(this IServiceCollection services, string connectionString, Action<PowerTracesDashboardOptions>? configure = null)
    {
        var options = new PowerTracesDashboardOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<IDashboardStore>(PowerTracesDatabaseProvider.MongoDb, (_, _) => new MongoDashboardStore(options));
        return services;
    }

    /// <summary>Exports spans straight to MongoDB via connection string — no collector, no EF
    /// Core, collections/indexes created automatically.</summary>
    public static TracerProviderBuilder AddPowerTracesMongoDbExporter(this TracerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new MongoStorageWriter(options), options);
    }

    /// <summary>Exports log records straight to MongoDB via connection string — no collector, no
    /// EF Core, collections/indexes created automatically.</summary>
    public static LoggerProviderBuilder AddPowerTracesMongoDbExporter(this LoggerProviderBuilder builder, string connectionString, Action<PowerTracesExporterOptions>? configure = null)
    {
        var options = BuildOptions(connectionString, configure);
        return builder.AddPowerTracesExporter(new MongoStorageWriter(options), options);
    }

    private static PowerTracesExporterOptions BuildOptions(string connectionString, Action<PowerTracesExporterOptions>? configure)
    {
        var options = new PowerTracesExporterOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        return options;
    }
}
