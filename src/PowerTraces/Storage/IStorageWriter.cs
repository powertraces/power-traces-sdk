namespace PowerTraces.Storage;

/// <summary>The contract every database backend implements to receive exported spans/logs.
/// SQL Server, PostgreSQL, MongoDB, MySQL and SQLite ship built in as their own
/// provider packages, each constructing an implementation of this and handing it to
/// <c>AddPowerTracesExporter</c>. Implement this yourself to plug in any other database — and
/// <see cref="IIgnoreRuleSource"/> too if it should honor ignore rules created from the
/// dashboard.</summary>
public interface IStorageWriter
{
    /// <summary>Creates whatever tables/collections/indexes this provider needs, if they don't
    /// already exist. Called automatically (once, lazily) before the first write — implementations
    /// don't need to call this themselves.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    Task WriteSpansAsync(IReadOnlyList<SpanRow> rows, ResourceInfo resource, CancellationToken ct);

    Task WriteLogsAsync(IReadOnlyList<LogRow> rows, ResourceInfo resource, CancellationToken ct);

    /// <summary>Removes every stored span of these traces. The trace exporter calls this when an
    /// ignore rule (or <c>ExcludePathPrefixes</c>) matches a trace's root span after some of that
    /// trace's child spans were already written by an earlier export batch — child spans usually
    /// end, and so get exported, before their root does. The default does nothing, which just
    /// leaves those partial traces visible.</summary>
    Task DeleteTracesAsync(IReadOnlyCollection<string> traceIds, CancellationToken ct) => Task.CompletedTask;
}
