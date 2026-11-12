using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace PowerTraces.Dashboard.Internal;

/// <summary>HTTP Basic Authentication for the dashboard — applied as an endpoint filter on the
/// whole mount group, so it covers the SPA and its query API alike with one check.</summary>
internal static class BasicAuthFilter
{
    private const string Realm = "PowerTraces Dashboard";

    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Create(string username, string password)
    {
        var expectedUser = Encoding.UTF8.GetBytes(username);
        var expectedPass = Encoding.UTF8.GetBytes(password);

        return async (context, next) =>
        {
            if (TryGetCredentials(context.HttpContext.Request, out var user, out var pass) &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), expectedUser) &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pass), expectedPass))
            {
                return await next(context).ConfigureAwait(false);
            }

            context.HttpContext.Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
            return Results.Unauthorized();
        };
    }

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
