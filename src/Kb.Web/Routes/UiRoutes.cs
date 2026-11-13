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
            // The address names the version and the package's hash, so the file behind it never changes.
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(found.File, found.ContentType);
        });
    }
}
