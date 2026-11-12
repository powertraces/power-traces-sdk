namespace PowerTraces.Dashboard.Query;

/// <summary>One attribute constraint on a trace/log list query — <see cref="Key"/> must be present
/// with a value that either equals or contains (substring match) <see cref="Value"/>, per
/// <see cref="Contains"/>. Case sensitivity follows whatever the underlying provider's default
/// string comparison is (e.g. a SQL Server column's collation) — not normalized here.
/// <see cref="IDashboardStore.ListTracesAsync"/>/<see cref="IDashboardStore.ListLogsAsync"/> accept
/// a list of these; a row must satisfy every filter in the list (AND), not just one.</summary>
public sealed record AttributeFilter(string Key, string Value, bool Contains);
