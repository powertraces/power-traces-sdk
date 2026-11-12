using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Dashboard.Internal;

/// <summary>Applies a store's retention policy once the host has started and then on an interval,
/// until the host stops. Started by <c>MapPowerTracesDashboard</c> — tied to the host's lifetime
/// rather than registered as a hosted service, since endpoints are mapped after the service
/// collection is built. One sweeper per store, however many times it's mounted.</summary>
internal static class RetentionSweeper
{
    private static readonly ConditionalWeakTable<IRetentionStore, object> Started = [];

    public static void Start(IServiceProvider services, IDashboardStore store, IRetentionStore retention, TimeSpan interval)
    {
        if (services.GetService<IHostApplicationLifetime>() is not { } lifetime || !Started.TryAdd(retention, new object()))
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("PowerTraces.Dashboard.Retention") ?? NullLogger.Instance;
        lifetime.ApplicationStarted.Register(() => _ = RunAsync(store, retention, interval, logger, lifetime.ApplicationStopping));
    }

    private static async Task RunAsync(IDashboardStore store, IRetentionStore retention, TimeSpan interval, ILogger logger, CancellationToken stopping)
    {
        try
        {
            await SweepOnceAsync(store, retention, logger, stopping).ConfigureAwait(false);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                await SweepOnceAsync(store, retention, logger, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping — expected.
        }
    }

    private static async Task SweepOnceAsync(IDashboardStore store, IRetentionStore retention, ILogger logger, CancellationToken ct)
    {
        // The sweep's own database calls shouldn't show up as traces.
        using var _ = SuppressInstrumentationScope.Begin();
        try
        {
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
            var run = await retention.ApplyRetentionAsync(ct).ConfigureAwait(false);
            if (run?.Error is { } error)
            {
                logger.LogWarning("PowerTraces retention sweep failed: {Error}", error);
            }
            else if (run is not null && run.TracesDeleted + run.LogsDeleted > 0)
            {
                logger.LogInformation("PowerTraces retention removed {Traces} traces and {Logs} logs", run.TracesDeleted, run.LogsDeleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "PowerTraces retention sweep failed");
        }
    }
}
