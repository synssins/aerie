using Kb.Web.Actions;
using Kb.Web.Plugins;
using Microsoft.AspNetCore.Http.Features;

namespace Kb.Web;

public sealed record PluginStateRequest(bool Enabled);
public sealed record PluginPermissionsRequest(Dictionary<string, string>? Roles);

/// <summary>The body of an install request as the package.</summary>
internal sealed class PluginUpload(HttpRequest request) : UploadContent
{
    private bool _read;

    public override string? FileName => null;
    public override long? Length => request.ContentLength;

    /// <summary>ASP.NET Core has its own 30 MB cap per request; a package may be larger. It can be set only before the body is read.</summary>
    public void Allow(long maxBytes)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = maxBytes;
    }

    // shortcut: the body is not kept, so it can be read once; buffer it when something other than the installer has to read a package.
    public override Task<Stream> OpenReadAsync(CancellationToken ct)
    {
        if (_read) throw new InvalidOperationException("A plugin upload can be read once.");
        _read = true;
        return Task.FromResult(request.Body);
    }
}

/// <summary>Administration > Plugins. Admins only. Each change answers at once and takes effect when the app has restarted.</summary>
public static class PluginAdminRoutes
{
    public static void MapPluginAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/plugins", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListPlugins(), ct).ToHttp()).WithAction<ListPlugins>();

        // The package is the raw request body. The web server's own size limit still applies and answers 413.
        admin.MapGet("/plugins/health", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPluginHealth(), ct).ToHttp()).WithAction<ReadPluginHealth>();

        admin.MapPost("/plugins", (HttpContext http, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new InstallPlugin { Package = new PluginUpload(http.Request) }, ct).ToHttp()).WithAction<InstallPlugin>();

        admin.MapPut("/plugins/{id}/state", (string id, PluginStateRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetPluginState { Id = id, Enabled = req.Enabled }, ct).ToHttp()).WithAction<SetPluginState>();

        admin.MapDelete("/plugins/{id}", (string id, bool? deleteData, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemovePlugin { Id = id, DeleteData = deleteData ?? false }, ct).ToHttp()).WithAction<RemovePlugin>();

        admin.MapDelete("/plugins/{id}/data", (string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeletePluginData { Id = id }, ct).ToHttp()).WithAction<DeletePluginData>();

        admin.MapGet("/plugins/{id}/settings", (string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPluginSettings { Id = id }, ct).ToHttp()).WithAction<ReadPluginSettings>();

        admin.MapGet("/plugins/{id}/permissions", (string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPluginPermissions { Id = id }, ct).ToHttp()).WithAction<ReadPluginPermissions>();

        admin.MapPut("/plugins/{id}/permissions", (string id, PluginPermissionsRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SavePluginPermissions { Id = id, Roles = req.Roles ?? [] }, ct).ToHttp()).WithAction<SavePluginPermissions>();

        admin.MapPut("/plugins/{id}/settings", (string id, PluginSettingsRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SavePluginSettings { Id = id, Values = req.Values ?? [] }, ct).ToHttp()).WithAction<SavePluginSettings>();

        admin.MapPost("/plugins/{id}/release-lock", (string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReleasePluginLock { Id = id }, ct).ToHttp()).WithAction<ReleasePluginLock>();
    }
}
