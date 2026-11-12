namespace PowerTraces.Dashboard.Query;

/// <summary>Which stored rows <see cref="IDashboardStore.ListAttributeKeysAsync"/> samples for
/// distinct attribute keys — the trace side's root spans, or the log records themselves.</summary>
public enum AttributeScope
{
    Traces,
    Logs,
}
