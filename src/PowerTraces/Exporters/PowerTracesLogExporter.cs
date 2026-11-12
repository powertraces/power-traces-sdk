using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using PowerTraces.Internal;
using PowerTraces.Storage;

namespace PowerTraces.Exporters;

/// <summary>Writes emitted LogRecords straight to whichever database <paramref name="writer"/>
/// targets. Meant to run behind a BatchLogRecordExportProcessor (the default when added via
/// <see cref="Extensions.LoggerProviderBuilderExtensions.AddPowerTracesExporter"/>).</summary>
public sealed class PowerTracesLogExporter(IStorageWriter writer, PowerTracesExporterOptions options) : BaseExporter<LogRecord>
{
    private readonly IStorageWriter _writer = writer;
    private ResourceInfo? _resourceInfo;

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        var rows = new List<LogRow>();
        foreach (var record in batch)
        {
            rows.Add(Map(record, options.AttributeJsonOptions));
        }

        if (rows.Count == 0)
        {
            return ExportResult.Success;
        }

        try
        {
            // Same reason as the trace exporter: this exporter's own DB traffic mustn't produce
            // spans of its own.
            using var suppress = SuppressInstrumentationScope.Begin();
            _writer.WriteLogsAsync(rows, GetResourceInfo(), CancellationToken.None).GetAwaiter().GetResult();
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            options.OnExportError?.Invoke(ex);
            return ExportResult.Failure;
        }
    }

    private ResourceInfo GetResourceInfo()
    {
        return _resourceInfo ??= PowerTracesTraceExporter.BuildResourceInfo(ParentProvider?.GetResource() ?? Resource.Empty);
    }

    private static LogRow Map(LogRecord record, System.Text.Json.JsonSerializerOptions? attributeJsonOptions)
    {
        var body = record.Body ?? record.FormattedMessage;

        var traceId = record.TraceId != default ? record.TraceId.ToHexString() : null;
        var spanId = record.SpanId != default ? record.SpanId.ToHexString() : null;

        var attributesJson = AttributeJson.Serialize(record.Attributes, attributeJsonOptions);

        return new LogRow
        {
            Timestamp = record.Timestamp,
            ObservedTimestamp = record.ObservedTimestamp,
            TraceId = traceId,
            SpanId = spanId,
            SeverityNumber = ToOtelSeverityNumber(record.LogLevel),
            SeverityText = record.LogLevel.ToString(),
            Body = body,
            AttributesJson = attributesJson,
            AttributeValues = AttributeText.Flatten(attributesJson),
            ScopeLibraryName = record.CategoryName,
            ScopeLibraryVersion = null,
            SchemaUrl = null,
        };
    }

    /// <summary>Maps Microsoft.Extensions.Logging's LogLevel onto the OTel log data model's
    /// SeverityNumber ranges (TRACE=1-4, DEBUG=5-8, INFO=9-12, WARN=13-16, ERROR=17-20, FATAL=21-24).</summary>
    private static int ToOtelSeverityNumber(LogLevel level) => level switch
    {
        LogLevel.Trace => 1,
        LogLevel.Debug => 5,
        LogLevel.Information => 9,
        LogLevel.Warning => 13,
        LogLevel.Error => 17,
        LogLevel.Critical => 21,
        _ => 0,
    };
}
