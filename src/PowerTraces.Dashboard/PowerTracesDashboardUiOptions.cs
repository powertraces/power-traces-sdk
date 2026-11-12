namespace PowerTraces.Dashboard;

/// <summary>Per-mount options passed to <c>MapPowerTracesDashboard(..., configureUi: o => ...)</c>:
/// the UI shell's branding, and how often this mount applies the retention policy. Unset values
/// keep the defaults below, so an unconfigured mount looks exactly as it always has.</summary>
public sealed class PowerTracesDashboardUiOptions
{
    public string Title { get; set; } = "PowerTraces";
    public string Subtitle { get; set; } = "Telemetry";

    /// <summary>How often this process applies the retention policy set on the dashboard's
    /// Retention page (default one hour; also once at startup). The policy itself is stored in the
    /// database, so any one process mounting the dashboard is enough — set this to
    /// <see langword="null"/> on the others to leave sweeping to it. Nothing is deleted while the
    /// policy is disabled, which it is until someone enables it.</summary>
    public TimeSpan? RetentionSweepInterval { get; set; } = TimeSpan.FromHours(1);
}
