namespace PowerTraces.Dashboard;

/// <summary>Connection-level options a provider package's <c>AddPowerTracesXxxDashboard(...)</c> DI
/// registration builds and hands to its <c>IDashboardStore</c> implementation. Independent of
/// <see cref="PowerTracesExporterOptions"/> — the dashboard only ever reads, so it can point at a
/// database populated by a different process than the one hosting it.</summary>
public sealed class PowerTracesDashboardOptions
{
    /// <summary>Connection string for whichever database the registering provider package
    /// targets. Required.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Must match the schema/collection-prefix the exporter was configured with.
    /// Defaults to "powertraces".</summary>
    public string Schema { get; set; } = PowerTracesSchema.DefaultSchemaName;

    /// <summary>The most database connections this dashboard uses at once (default 8). Further
    /// requests queue — without holding a connection — until one finishes, so however many people
    /// have the dashboard open, it can't drain the connection pool the host application relies on.
    /// Requests the browser abandons (a superseded search, leaving the page) are cancelled and
    /// release their slot immediately.</summary>
    public int MaxConcurrentQueries { get; set; } = 8;

    /// <summary>Called by each provider package's <c>AddPowerTracesXxxDashboard(...)</c> registration
    /// — not something you need to call yourself.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"{nameof(PowerTracesDashboardOptions)}.{nameof(ConnectionString)} must be set.");
        }

        PowerTracesSchema.ValidateSchemaName(Schema);

        if (MaxConcurrentQueries < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(PowerTracesDashboardOptions)}.{nameof(MaxConcurrentQueries)} must be at least 1.");
        }
    }
}
