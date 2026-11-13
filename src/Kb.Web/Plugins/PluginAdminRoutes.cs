using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web.Pipeline;

namespace Kb.Web.Plugins;

public sealed record PluginInfoDto(string Id, string Name, string Version, string Publisher, string? Description, PluginOrigin Origin, PluginState State, string? Reason,
    bool Locked, bool Loaded, string Source, IReadOnlyList<string> Requires, IReadOnlyList<string> RequiredBy, IReadOnlyList<string> ExtensionPoints, IReadOnlyList<string> OutboundHosts,
    IReadOnlyList<PluginPermission> Permissions, bool Enforcing, IReadOnlyList<PluginSetting> Settings, bool Removed);
public sealed record PluginListDto(IReadOnlyList<PluginInfoDto> Plugins, bool SafeMode);
public sealed record PluginStateRequest(bool Enabled);

/// <summary>Administration > Plugins. Admins only. Each change answers at once and takes effect when the app has restarted.</summary>
public static class PluginAdminRoutes
{
    public static void MapPluginAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.AdminGroup();

        // The list is what the database says now; "loaded" is what this server loaded when it started.
        admin.MapGet("/plugins", async (PluginInstaller installer, PluginRegistry registry, CancellationToken ct) =>
        {
            var known = await installer.KnownAsync(ct);
            var manifests = known.Select(k => k.Manifest).ToList();
            return Results.Ok(new PluginListDto([.. known.Select(k => new PluginInfoDto(
                k.Manifest.Id, PluginManifests.NameOf(k.Manifest), k.Manifest.Version, k.Manifest.Publisher, k.Manifest.Description, k.Origin, k.Status.State, k.Status.Reason,
                k.Status.LockedAt is not null, registry.Find(k.Manifest.Id) is { Loaded: true } loaded && loaded.Manifest.Version == k.Manifest.Version, k.Source,
                [.. k.Manifest.Requires.Select(r => r.Id)], PluginGraph.Dependents(k.Manifest.Id, manifests), k.Manifest.ExtensionPoints, k.Manifest.OutboundHosts,
                k.Manifest.Permissions, k.Manifest.Enforcing, k.Manifest.Settings, Removed: false))], registry.SafeMode));
        });

        // The package is the raw request body. The web server's own size limit still applies and answers 413.
        admin.MapPost("/plugins", async (HttpContext http, PluginInstaller installer, CancellationToken ct) =>
        {
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = PluginPackageReader.MaxUnpackedBytes;
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + n > PluginPackageReader.MaxUnpackedBytes) return HttpOutcome.ToHttp(new Problem(400, "Package too large."));
                body.Write(buffer, 0, n);
            }
            return await installer.InstallAsync(body.ToArray(), PluginSource.Upload, http.User.UserId(), ct).ToHttp();
        });

        admin.MapPut("/plugins/{id}/state", (string id, PluginStateRequest req, HttpContext http, PluginInstaller installer, CancellationToken ct) =>
            installer.SetEnabledAsync(id, req.Enabled, http.User.UserId(), ct).ToHttp());

        admin.MapDelete("/plugins/{id}", (string id, bool? deleteData, HttpContext http, PluginInstaller installer, CancellationToken ct) =>
            installer.RemoveAsync(id, deleteData ?? false, http.User.UserId(), ct).ToHttp());

        admin.MapDelete("/plugins/{id}/data", (string id, HttpContext http, PluginInstaller installer, CancellationToken ct) =>
            installer.DeleteDataAsync(id, http.User.UserId(), ct).ToHttp());

        admin.MapPost("/plugins/{id}/release-lock", (string id, HttpContext http, PluginInstaller installer, CancellationToken ct) =>
            installer.ReleaseLockAsync(id, http.User.UserId(), ct).ToHttp());
    }
}
