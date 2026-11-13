namespace Kb.Web;

public sealed record SignInRequest(string UserName, string Password);
public sealed record SetupRequest(string UserName, string DisplayName, string Password, string? Code);

public static class AuthRoutes
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/local/signin", (SignInRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SignIn { UserName = req.UserName, Password = req.Password }, ct).ToHttp()).AllowAnonymous().WithAction<SignIn>();

        api.MapPost("/auth/signout", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SignOut(), ct).ToHttp()).WithAction<SignOut>();

        api.MapGet("/me", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadMe(), ct).ToHttp()).WithAction<ReadMe>();
    }

    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/setup/status", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadSetupStatus(), ct).ToHttp()).AllowAnonymous().WithAction<ReadSetupStatus>();

        api.MapPost("/setup", (SetupRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RunSetup { UserName = req.UserName, DisplayName = req.DisplayName, Password = req.Password, Code = req.Code }, ct).ToHttp())
            .AllowAnonymous().WithAction<RunSetup>();
    }

    public static void MapUserSettingsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/settings", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadMySettings(), ct).ToHttp()).WithAction<ReadMySettings>();

        api.MapPut("/me/settings", (UserSettings req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveMySettings { Theme = req.Theme, Width = req.Width, StartPage = req.StartPage, CodeLanguage = req.CodeLanguage, Notify = req.Notify }, ct).ToHttp())
            .WithAction<SaveMySettings>();
    }
}
