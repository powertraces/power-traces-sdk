using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace PowerTraces.Dashboard.Internal;

/// <summary>HTTP Basic Authentication for the dashboard — applied as an endpoint filter on the
/// whole mount group, so it covers the SPA and its query API alike with one check.</summary>
internal static class BasicAuthFilter
{
    private const string Realm = "PowerTraces Dashboard";

#if NET7_0_OR_GREATER
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Create(string username, string password)
    {
        var check = CreateCheck(username, password);

        return async (context, next) =>
        {
            if (check(context.HttpContext.Request))
            {
                return await next(context).ConfigureAwait(false);
            }

            SetChallenge(context.HttpContext.Response);
            return Results.Unauthorized();
        };
    }
#else
    /// <summary>The ASP.NET Core 6 version of <c>Create</c>, for <see cref="RouteGroupCompat"/>
    /// (no endpoint filters there).</summary>
    public static Func<HttpContext, RequestDelegate, Task> CreateMiddleware(string username, string password)
    {
        var check = CreateCheck(username, password);

        return (context, next) =>
        {
            if (check(context.Request))
            {
                return next(context);
            }

            SetChallenge(context.Response);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    }
#endif

    private static Func<HttpRequest, bool> CreateCheck(string username, string password)
    {
        var expectedUser = Encoding.UTF8.GetBytes(username);
        var expectedPass = Encoding.UTF8.GetBytes(password);

        return request =>
            TryGetCredentials(request, out var user, out var pass) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), expectedUser) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pass), expectedPass);
    }

    private static void SetChallenge(HttpResponse response) =>
        response.Headers["WWW-Authenticate"] = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";

    private static bool TryGetCredentials(HttpRequest request, out string username, out string password)
    {
        username = "";
        password = "";

        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return false;
        }

        var value = header.ToString();
        if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        username = decoded[..separator];
        password = decoded[(separator + 1)..];
        return true;
    }
}
