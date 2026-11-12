namespace PowerTraces.Storage;

/// <summary>The Resource (the process/service) that produced a batch of spans/logs — computed
/// once per export from the OTel SDK's Resource and handed to the storage writer alongside each
/// batch so it can dedupe/upsert the owning resource record.</summary>
public sealed record ResourceInfo(string? ServiceName, string AttributesJson, string? SchemaUrl);

/// <summary>A single span, flattened to the shape every storage provider writes.</summary>
public sealed class SpanRow
{
    public required Guid Id { get; init; }
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public required string ParentSpanId { get; init; }
    public required string Name { get; init; }
    public string? Kind { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public string? Status { get; init; }
    public string? StatusMessage { get; init; }
    public bool HasError { get; init; }
    public string? HttpStatus { get; init; }
    public required string AttributesJson { get; init; }
    public string? ScopeLibraryName { get; init; }
    public string? ScopeLibraryVersion { get; init; }
    public required string EventsJson { get; init; }
    public required string LinksJson { get; init; }

    /// <summary>The span's attributes flattened to text, one pair per attribute — what providers
    /// index for attribute filtering. Filled by the exporter; when <see langword="null"/> (a row
    /// built by hand) providers derive it from <see cref="AttributesJson"/> via
    /// <see cref="AttributeText.Flatten(string?)"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? AttributeValues { get; init; }
}

/// <summary>A single log record, flattened to the shape every storage provider writes.</summary>
public sealed class LogRow
{
    public required DateTime Timestamp { get; init; }
    public DateTime? ObservedTimestamp { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public int SeverityNumber { get; init; }
    public string? SeverityText { get; init; }
    public string? Body { get; init; }
    public required string AttributesJson { get; init; }
    public string? ScopeLibraryName { get; init; }
    public string? ScopeLibraryVersion { get; init; }
    public string? SchemaUrl { get; init; }

    /// <summary>Same as <see cref="SpanRow.AttributeValues"/>, for this log record.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? AttributeValues { get; init; }
}
