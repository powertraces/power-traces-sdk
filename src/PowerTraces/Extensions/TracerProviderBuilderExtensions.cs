using OpenTelemetry;
using OpenTelemetry.Trace;
using PowerTraces.Exporters;
using PowerTraces.Storage;

namespace PowerTraces.Extensions;

public static class TracerProviderBuilderExtensions
{
    /// <summary>Exports spans straight to <paramref name="writer"/>'s database — no collector, no
    /// EF Core, tables/collections created automatically. Batches on a background thread via
    /// BatchActivityExportProcessor, same as the OTLP/Console exporters. This is the extensibility
    /// point every provider package (PowerTraces.MSSQL, .PostgreSQL, .MySQL,
    /// .MongoDb) builds its own <c>AddPowerTracesXxxExporter(connectionString)</c> convenience on top
    /// of — call it directly yourself, with your own <see cref="IStorageWriter"/>, to plug in a
    /// database none of them cover.</summary>
    public static TracerProviderBuilder AddPowerTracesExporter(this TracerProviderBuilder builder, IStorageWriter writer, PowerTracesExporterOptions options)
    {
        options.Validate();
        return builder.AddProcessor(new BatchActivityExportProcessor(new PowerTracesTraceExporter(writer, options)));
    }

    public static TracerProviderBuilder AddPowerTracesExporter(this TracerProviderBuilder builder, Func<IServiceProvider, IStorageWriter> writer, PowerTracesExporterOptions options)
    {
        options.Validate();
        return builder.AddProcessor((sp) => new BatchActivityExportProcessor(new PowerTracesTraceExporter(writer(sp), options)));
    }
}
