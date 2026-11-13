using Kb.Web.Actions;
namespace Kb.Web;

public sealed record SignInRequest(string UserName, string Password);
public sealed record SetupRequest(string UserName, string DisplayName, string Password, string? Code);

/// <summary>The body of the set-password request. Its text form leaves out the token and the password.</summary>
public sealed record SetPasswordRequest(Guid UserId, string? Token, string? Password)
{
    public override string ToString() => $"SetPasswordRequest {{ UserId = {UserId} }}";
}

public static class AuthRoutes
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/local/signin", (SignInRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SignIn { UserName = req.UserName, Password = req.Password }, ct).ToHttp()).AllowAnonymous().WithAction<SignIn>();

        // With the link an Admin handed over. Nobody is signed in; the link is the right to do it.
        api.MapPost("/auth/local/password", (SetPasswordRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetPassword { UserId = req.UserId, Token = req.Token ?? "", Password = req.Password ?? "" }, ct).ToHttp()).AllowAnonymous().WithAction<SetPassword>();

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
