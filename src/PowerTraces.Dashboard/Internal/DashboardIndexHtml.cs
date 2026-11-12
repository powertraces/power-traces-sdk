using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace PowerTraces.Dashboard.Internal;

/// <summary>The dashboard's mount prefix is chosen by the host at runtime (MapPowerTracesDashboard's
/// `prefix` argument), not known when the SPA was built — so the built index.html (with relative
/// asset URLs, thanks to vite's `base: "./"`) gets a `&lt;base href&gt;` and a small global spliced
/// in per-mount, once, instead of baking a path into the build.</summary>
internal sealed class DashboardIndexHtml
{
    private readonly string _template;

    public DashboardIndexHtml(IFileProvider fileProvider)
    {
        var file = fileProvider.GetFileInfo("index.html");
        if (!file.Exists)
        {
            throw new InvalidOperationException(
                "PowerTraces.Dashboard's embedded wwwroot/index.html is missing. Build ClientWeb (yarn build) " +
                "before building this project, or pass /p:BuildFrontend=true.");
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        _template = reader.ReadToEnd();
    }

    public string Render(string mountPrefix, PowerTracesDashboardUiOptions uiOptions)
    {
        var baseHref = mountPrefix.Length == 0 ? "/" : $"{mountPrefix}/";
        var config = JsonSerializer.Serialize(new { title = uiOptions.Title, subtitle = uiOptions.Subtitle });
        var injected = $"""<base href="{baseHref}"><script>window.__POWERTRACES_BASE__={JsonSerializer.Serialize(mountPrefix)};window.__POWERTRACES_CONFIG__={config};</script>""";

        var html = ReplaceTitleTag(_template, uiOptions.Title);

        var headIndex = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIndex < 0)
        {
            return injected + html;
        }

        var insertAt = headIndex + "<head>".Length;
        return string.Concat(html.AsSpan(0, insertAt), injected, html.AsSpan(insertAt));
    }

    /// <summary>The browser tab should reflect the same configured title as the sidebar, not the
    /// static string baked into the SPA at build time — replaced directly in the markup (rather
    /// than only via a script setting <c>document.title</c>) so it's correct even before any JS
    /// runs.</summary>
    private static string ReplaceTitleTag(string html, string title)
    {
        var start = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return html;
        }

        var contentStart = start + "<title>".Length;
        var end = html.IndexOf("</title>", contentStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return html;
        }

        return string.Concat(html.AsSpan(0, contentStart), System.Net.WebUtility.HtmlEncode(title), html.AsSpan(end));
    }
}
