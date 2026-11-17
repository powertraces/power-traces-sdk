#if !NET7_0_OR_GREATER
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace PowerTraces.Dashboard.Internal;

/// <summary>Stand-in for ASP.NET Core 7's route groups (<c>MapGroup</c>) and endpoint filters
/// (<c>AddEndpointFilter</c>), which ASP.NET Core 6 doesn't have. Only compiled into the net6.0
/// build; net7.0+ uses the real thing.</summary>
/// <remarks>
/// Works the same way the framework's own route groups do: endpoints mapped onto the group land in
/// the group's own <see cref="DataSources"/>, and the group registers one data source with its
/// parent that hands those endpoints out again with the group's prefix put in front of their route
/// pattern, the group's filters wrapped around their request delegate (first added = outermost,
/// parent group before child group, as with endpoint filters) and the group's conventions applied.
/// </remarks>
internal sealed class RouteGroupCompat : IEndpointRouteBuilder, IEndpointConventionBuilder
{
    private readonly IEndpointRouteBuilder _parent;
    private readonly string _prefix;
    private readonly List<Func<HttpContext, RequestDelegate, Task>> _filters = [];
    private readonly List<Action<EndpointBuilder>> _conventions = [];

    private RouteGroupCompat(IEndpointRouteBuilder parent, string prefix)
    {
        _parent = parent;
        _prefix = prefix;
    }

    /// <summary>The equivalent of <c>parent.MapGroup(prefix)</c>.</summary>
    public static RouteGroupCompat Create(IEndpointRouteBuilder parent, string prefix)
    {
        var group = new RouteGroupCompat(parent, prefix);
        parent.DataSources.Add(new GroupEndpointDataSource(group));
        return group;
    }

    /// <summary>The equivalent of <c>AddEndpointFilter</c>: runs <paramref name="filter"/> around
    /// every endpoint in the group (including nested groups). Call <c>next</c> to continue to the
    /// endpoint, or write the response yourself to short-circuit it.</summary>
    public RouteGroupCompat AddFilter(Func<HttpContext, RequestDelegate, Task> filter)
    {
        _filters.Add(filter);
        return this;
    }

    public IServiceProvider ServiceProvider => _parent.ServiceProvider;

    public ICollection<EndpointDataSource> DataSources { get; } = [];

    public IApplicationBuilder CreateApplicationBuilder() => _parent.CreateApplicationBuilder();

    public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

    private RequestDelegate Wrap(RequestDelegate endpoint)
    {
        var next = endpoint;
        for (var i = _filters.Count - 1; i >= 0; i--)
        {
            var filter = _filters[i];
            var inner = next;
            next = context => filter(context, inner);
        }

        return next;
    }

    private static string Combine(string prefix, string? pattern)
    {
        var tail = (pattern ?? "").TrimStart('/');
        var head = prefix.TrimEnd('/');
        return tail.Length == 0 ? (head.Length == 0 ? "/" : head) : head + "/" + tail;
    }

    private sealed class GroupEndpointDataSource(RouteGroupCompat group) : EndpointDataSource
    {
        private readonly object _gate = new();
        private IReadOnlyList<Endpoint>? _endpoints;

        // Built on first use, which routing does once the app has finished mapping endpoints, so
        // conventions added to the returned builder after MapPowerTracesDashboard are included.
        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                lock (_gate)
                {
                    return _endpoints ??= Build();
                }
            }
        }

        public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

        private List<Endpoint> Build()
        {
            var built = new List<Endpoint>();
            foreach (var source in group.DataSources)
            {
                foreach (var endpoint in source.Endpoints)
                {
                    if (endpoint is not RouteEndpoint route || route.RequestDelegate is null)
                    {
                        built.Add(endpoint);
                        continue;
                    }

                    var pattern = RoutePatternFactory.Parse(Combine(group._prefix, route.RoutePattern.RawText));
                    var builder = new RouteEndpointBuilder(group.Wrap(route.RequestDelegate), pattern, route.Order)
                    {
                        DisplayName = route.DisplayName,
                    };

                    foreach (var metadata in route.Metadata)
                    {
                        builder.Metadata.Add(metadata!);
                    }

                    foreach (var convention in group._conventions)
                    {
                        convention(builder);
                    }

                    built.Add(builder.Build());
                }
            }

            return built;
        }
    }
}
#endif
