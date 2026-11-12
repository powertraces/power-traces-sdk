using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Dashboard.Internal;

/// <summary>Indexes traces written before the trace search index existed, in the background, once
/// the host has started: small batches, newest first (so recent traces become searchable first),
/// until the store reports nothing left. A failure is logged and retried later. Started by
/// <c>MapPowerTracesDashboard</c>, once per store.</summary>
internal static class SearchIndexBackfiller
{
    private const int BatchSize = 200;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private static readonly ConditionalWeakTable<ISearchIndexBackfill, object> Started = [];

    public static void Start(IServiceProvider services, IDashboardStore store, ISearchIndexBackfill backfill)
    {
        if (services.GetService<IHostApplicationLifetime>() is not { } lifetime || !Started.TryAdd(backfill, new object()))
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("PowerTraces.Dashboard.SearchIndex") ?? NullLogger.Instance;
        lifetime.ApplicationStarted.Register(() => _ = RunAsync(store, backfill, logger, lifetime.ApplicationStopping));
    }

    private static async Task RunAsync(IDashboardStore store, ISearchIndexBackfill backfill, ILogger logger, CancellationToken stopping)
    {
        // Its own database calls shouldn't show up as traces.
        using var _ = SuppressInstrumentationScope.Begin();
        long total = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await store.EnsureSchemaAsync(stopping).ConfigureAwait(false);
                    var indexed = await backfill.BackfillSearchIndexAsync(BatchSize, stopping).ConfigureAwait(false);
                    if (indexed == 0)
                    {
                        if (total > 0)
                        {
                            logger.LogInformation("PowerTraces indexed {Traces} existing traces for search", total);
                        }

                        return;
                    }

                    total += indexed;
                    await Task.Delay(Pause, stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "PowerTraces search indexing of existing traces failed; retrying in {Delay}", RetryAfter);
                    await Task.Delay(RetryAfter, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping — the cursor is saved, the next start carries on.
        }
    }
}
