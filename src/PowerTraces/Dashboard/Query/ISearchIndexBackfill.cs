namespace PowerTraces.Dashboard.Query;

/// <summary>Optional capability of a dashboard store: indexing traces written before the trace
/// search index existed. The dashboard calls it in the background after startup until it returns
/// 0. Every built-in provider implements it.</summary>
public interface ISearchIndexBackfill
{
    /// <summary>Indexes up to <paramref name="maxTraces"/> not-yet-indexed traces, newest first,
    /// and returns how many it indexed — 0 once there is nothing left.</summary>
    Task<int> BackfillSearchIndexAsync(int maxTraces, CancellationToken ct);
}
