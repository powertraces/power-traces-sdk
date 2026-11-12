using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using PowerTraces.Dashboard.Internal;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Dashboard;

public static class PowerTracesDashboardEndpointRouteBuilderExtensions
{
    /// <summary>Mounts the PowerTraces dashboard (traces/logs UI + its query API) at
    /// <paramref name="prefix"/> (default "/otel"), backed by whichever <see cref="IDashboardStore"/>
    /// is registered under <paramref name="provider"/> — call the matching provider package's
    /// <c>AddPowerTracesXxxDashboard(connectionString)</c> in <c>ConfigureServices</c> first (e.g.
    /// PowerTraces.MSSQL's <c>AddPowerTracesMsSqlDashboard</c>). This package
    /// itself has no idea which databases exist — call this more than once, with different
    /// prefixes/providers, to serve several databases' worth of traces/logs side by side.</summary>
    public static IEndpointConventionBuilder MapPowerTracesDashboard(
        this IEndpointRouteBuilder endpoints, string prefix, PowerTracesDatabaseProvider provider,
        string? username = null, string? password = null, Action<PowerTracesDashboardUiOptions>? configureUi = null)
    {
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"{nameof(MapPowerTracesDashboard)}: set both {nameof(username)} and {nameof(password)} to require a login, or leave both unset.");
        }

        var store = endpoints.ServiceProvider.GetRequiredKeyedService<IDashboardStore>(provider);

        var uiOptions = new PowerTracesDashboardUiOptions();
        configureUi?.Invoke(uiOptions);

        if (store is IRetentionStore retention && uiOptions.RetentionSweepInterval is { } sweepInterval && sweepInterval > TimeSpan.Zero)
        {
            RetentionSweeper.Start(endpoints.ServiceProvider, store, retention, sweepInterval);
        }

        if (store is ISearchIndexBackfill backfill)
        {
            SearchIndexBackfiller.Start(endpoints.ServiceProvider, store, backfill);
        }

        var mountPrefix = NormalizePrefix(prefix);

        var fileProvider = new ManifestEmbeddedFileProvider(typeof(PowerTracesDashboardEndpointRouteBuilderExtensions).Assembly, "wwwroot");
        var indexHtml = new DashboardIndexHtml(fileProvider);

        var group = endpoints.MapGroup(mountPrefix);

        // Applied on the parent group so it covers the SPA and the /api sub-group alike — nested
        // route groups inherit filters from their parent.
        if (!string.IsNullOrEmpty(username))
        {
            group.AddEndpointFilter(BasicAuthFilter.Create(username, password!));
        }

        var api = group.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            // Cheap after the first call — each IDashboardStore guards its own schema/index
            // creation so this is a no-op once warm, not a per-request DB round-trip.
            await store.EnsureSchemaAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            return await next(ctx).ConfigureAwait(false);
        });
        api.MapQueryEndpoints(store);

        group.MapSpa(mountPrefix, fileProvider, indexHtml, uiOptions);

        return group;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
        {
            return "";
        }

        return "/" + prefix.Trim('/');
    }
}
