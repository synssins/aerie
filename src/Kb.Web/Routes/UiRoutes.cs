using Kb.Core.Plugins;
using Kb.Web.Actions;
using Kb.Web.Pipeline;

namespace Kb.Web;

public sealed record UiErrorRequest(string? Plugin, string? Where, string? Message);

/// <summary>What the browser needs to load plugin scripts: the list, the files, and where to report a script that failed.</summary>
public static class UiRoutes
{
    public static void MapUiEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/ui/plugins", (IActionBus bus, CancellationToken ct) => bus.SendAsync(new ListUiPlugins(), ct).ToHttp()).WithAction<ListUiPlugins>();

        api.MapPost("/ui/errors", (UiErrorRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReportUiError { Plugin = req.Plugin ?? "", Where = req.Where, Message = req.Message }, ct).ToHttp()).WithAction<ReportUiError>();
    }

    /// <summary>
    /// A file of a loaded plugin's ui folder: /plugins/&lt;id&gt;/&lt;version&gt;/ui/... It needs sign-in like everything else.
    /// It is not an action: it serves a file of a package an Admin installed, to the browser that runs the plugin's screens.
    /// </summary>
    public static void MapPluginFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/plugins/{id}/{version}/{**path}", (string id, string version, string? path, PluginRegistry registry, HttpContext http) =>
        {
            if (PluginStatic.Find(registry, id, version, path) is not { } found) return Results.NotFound();
            // Only an address with the loaded package's hash names a file that never changes. A script's own address has it.
            // What the script fetches beside itself (a stylesheet, an imported module) has not, so the browser asks again each time:
            // the same version can be installed again with other content.
            var hash = PluginStatic.HashOf(registry, id);
            http.Response.Headers.CacheControl = hash.Length > 0 && http.Request.Query["v"] == hash ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.File(found.File, found.ContentType);
        });
    }
}
