using OpenTelemetry;
using OpenTelemetry.Logs;
using PowerTraces.Exporters;
using PowerTraces.Storage;

namespace PowerTraces.Extensions;

public static class LoggerProviderBuilderExtensions
{
    /// <summary>Exports log records straight to <paramref name="writer"/>'s database — no
    /// collector, no EF Core, tables/collections created automatically. Batches on a background
    /// thread via BatchLogRecordExportProcessor, same as the OTLP/Console exporters. This is the
    /// extensibility point every provider package builds its own
    /// <c>AddPowerTracesXxxExporter(connectionString)</c> convenience on top of.</summary>
    public static LoggerProviderBuilder AddPowerTracesExporter(this LoggerProviderBuilder builder, IStorageWriter writer, PowerTracesExporterOptions options)
    {
        options.Validate();
        return builder.AddProcessor(new BatchLogRecordExportProcessor(new PowerTracesLogExporter(writer, options)));
    }

    public static LoggerProviderBuilder AddPowerTracesExporter(this LoggerProviderBuilder builder, Func<IServiceProvider, IStorageWriter> writer, PowerTracesExporterOptions options)
    {
        options.Validate();
        return builder.AddProcessor((sp) => new BatchLogRecordExportProcessor(new PowerTracesLogExporter(writer(sp), options)));
    }
}
