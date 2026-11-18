using Kb.Web.Actions;

namespace Kb.Web;

public static class MyPasswordRoutes
{
    /// <summary>Someone's own password, changed by giving the current one.</summary>
    public static void MapMyPassword(this RouteGroupBuilder api)
    {
        api.MapPost("/me/password", (MyPasswordRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetMyPassword { Current = req.Current ?? "", Password = req.Password ?? "" }, ct).ToHttp(_ => Results.NoContent())).WithAction<SetMyPassword>();
    }
}
