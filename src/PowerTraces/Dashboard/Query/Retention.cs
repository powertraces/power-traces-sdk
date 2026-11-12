namespace PowerTraces.Dashboard.Query;

/// <summary>How long stored telemetry is kept — edited from the dashboard and persisted in the
/// provider's own storage, so every process sharing that storage applies the same policy.
/// <see cref="KeepErrorTraces"/>/<see cref="KeepErrorLogs"/> exempt failures from deletion, and
/// pinned traces (see <see cref="IRetentionStore.PinTraceAsync"/>) are never deleted, together
/// with the logs that belong to them.</summary>
public record RetentionSettingsDto(
    bool Enabled,
    int TraceRetentionDays,
    int LogRetentionDays,
    bool KeepErrorTraces,
    bool KeepErrorLogs)
{
    public const int MaxRetentionDays = 3650;

    /// <summary>What a store reports before anyone has saved a policy: nothing is deleted.</summary>
    public static readonly RetentionSettingsDto Disabled = new(false, 30, 30, true, true);

    /// <summary>A problem with these values, or <see langword="null"/> when they're usable.</summary>
    public string? Validate() =>
        TraceRetentionDays is < 1 or > MaxRetentionDays || LogRetentionDays is < 1 or > MaxRetentionDays
            ? $"Retention must be between 1 and {MaxRetentionDays} days."
            : null;
}

/// <summary>The outcome of one retention sweep.</summary>
public record RetentionRunDto(DateTime StartedAt, DateTime FinishedAt, long TracesDeleted, long LogsDeleted, string? Error = null);

/// <summary>The policy in force, when it was last changed, and how the most recent sweep went.</summary>
public record RetentionStateDto(RetentionSettingsDto Settings, DateTime? UpdatedAt, RetentionRunDto? LastRun);

/// <summary>A trace excluded from retention. The summary fields are captured when it's pinned, so
/// the pinned list renders without looking every trace up again.</summary>
public record PinnedTraceDto(string TraceId, string? RootName, string? ServiceName, DateTime? StartTime, bool HasError, DateTime PinnedAt);

/// <summary>Optional capability for dashboard stores that can enforce a retention policy (every
/// built-in provider implements it). The dashboard runs <see cref="ApplyRetentionAsync"/> on a
/// timer in whichever process mounts it, and on demand from its Retention page.</summary>
public interface IRetentionStore
{
    Task<RetentionStateDto> GetRetentionAsync(CancellationToken ct);

    Task SaveRetentionSettingsAsync(RetentionSettingsDto settings, CancellationToken ct);

    Task<IReadOnlyList<PinnedTraceDto>> ListPinnedTracesAsync(CancellationToken ct);

    Task<bool> IsTracePinnedAsync(string traceId, CancellationToken ct);

    /// <summary>Excludes a trace (and its logs) from retention. Pinning an already pinned trace
    /// is a no-op.</summary>
    Task PinTraceAsync(PinnedTraceDto trace, CancellationToken ct);

    Task UnpinTraceAsync(string traceId, CancellationToken ct);

    /// <summary>Deletes whatever the saved policy says has expired and records the outcome as
    /// <see cref="RetentionStateDto.LastRun"/>. Does nothing (and returns <see langword="null"/>)
    /// while the policy is disabled.</summary>
    Task<RetentionRunDto?> ApplyRetentionAsync(CancellationToken ct);
}

/// <summary>Helpers every <see cref="IRetentionStore"/> shares.</summary>
public static class Retention
{
    /// <summary>Logs at or above this severity (OpenTelemetry's ERROR range) count as errors.</summary>
    public const int ErrorSeverityNumber = 17;

    /// <summary>Rows are deleted in batches of this many, so a large backlog never becomes one
    /// huge transaction or lock.</summary>
    public const int BatchSize = 500;

    public static DateTime Cutoff(int days, DateTime nowUtc) => nowUtc.AddDays(-days);

    /// <summary>Runs <paramref name="sweep"/> and turns its result (or failure) into the run
    /// record a store persists.</summary>
    public static async Task<RetentionRunDto> RunAsync(Func<Task<(long Traces, long Logs)>> sweep)
    {
        var started = DateTime.UtcNow;
        try
        {
            var (traces, logs) = await sweep().ConfigureAwait(false);
            return new RetentionRunDto(started, DateTime.UtcNow, traces, logs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RetentionRunDto(started, DateTime.UtcNow, 0, 0, ex.Message);
        }
    }
}
