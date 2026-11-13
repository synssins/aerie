using Kb.Web.Actions;

namespace Kb.Web;

public sealed record LogTargetRequest(string? Name, string? Kind, Dictionary<string, string?>? Settings, Dictionary<string, string?>? Secrets, bool Enabled = true);

/// <summary>Administration > Log shipping. Admins only; every change is audited; no answer carries a secret.</summary>
public static class LogRoutes
{
    public static void MapLogEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin").MapGroup("/logs"); // no filter: each handler answers 403 to a caller who is not an Admin

        g.MapGet("/", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadLogs(), ct).ToHttp()).WithAction<ReadLogs>();

        g.MapPost("/targets", (LogTargetRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AddLogTarget { Name = req.Name, Kind = req.Kind, Settings = req.Settings, Secrets = req.Secrets, Enabled = req.Enabled }, ct)
                .ToHttp(target => Results.Created($"/api/v1/admin/logs/targets/{target.Id}", target))).WithAction<AddLogTarget>();

        g.MapPut("/targets/{id:guid}", (Guid id, LogTargetRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveLogTarget { Id = id, Name = req.Name, Settings = req.Settings, Secrets = req.Secrets, Enabled = req.Enabled }, ct).ToHttp()).WithAction<SaveLogTarget>();

        g.MapPost("/targets/{id:guid}/test", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new TestLogTarget { Id = id }, ct).ToHttp()).WithAction<TestLogTarget>();

        g.MapDelete("/targets/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveLogTarget { Id = id }, ct).ToHttp()).WithAction<RemoveLogTarget>();
    }
}
