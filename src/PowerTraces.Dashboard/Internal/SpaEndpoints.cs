using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace PowerTraces.Dashboard.Internal;

internal static class SpaEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapSpa(this IEndpointRouteBuilder group, string mountPrefix, IFileProvider fileProvider, DashboardIndexHtml indexHtml, PowerTracesDashboardUiOptions uiOptions)
    {
        // The mount prefix is fixed for the app's lifetime, so the rendered shell is built once.
        var shell = indexHtml.Render(mountPrefix, uiOptions);

        group.MapGet("/", () => Results.Text(shell, "text/html", System.Text.Encoding.UTF8));

        group.MapGet("/{**path}", (string? path) =>
        {
            if (!string.IsNullOrEmpty(path))
            {
                var file = fileProvider.GetFileInfo(path);
                if (file.Exists && !file.IsDirectory)
                {
                    var contentType = ContentTypes.TryGetContentType(file.Name, out var type)
                        ? type
                        : "application/octet-stream";
                    return Results.Stream(file.CreateReadStream(), contentType);
                }
            }

            // Not a known asset — treat it as a client-side route (e.g. /otel/traces/abcd) and let
            // React Router take it from there.
            return Results.Text(shell, "text/html", System.Text.Encoding.UTF8);
        });
    }
}
