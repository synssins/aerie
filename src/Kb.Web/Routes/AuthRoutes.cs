using Kb.Web.Actions;
namespace Kb.Web;

public sealed record SignInRequest(string UserName, string Password);
public sealed record CodeRequest(string? Code)
{
    public override string ToString() => "CodeRequest";
}
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

        // The second step: the password was right a moment ago (a pending sign-in), and this is the code.
        api.MapPost("/auth/local/code", (CodeRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SignInCode { Code = req.Code ?? "" }, ct).ToHttp()).AllowAnonymous().WithAction<SignInCode>();

        // Setting up codes: pending at sign-in, or signed in and turning them on. The handlers decide who the caller is.
        api.MapPost("/auth/codes/key", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new NewCodeKey(), ct).ToHttp()).AllowAnonymous().WithAction<NewCodeKey>();

        api.MapPost("/auth/codes", (CodeRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new EnrollCodes { Code = req.Code ?? "" }, ct).ToHttp()).AllowAnonymous().WithAction<EnrollCodes>();

        api.MapGet("/me/codes", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadMyCodes(), ct).ToHttp()).WithAction<ReadMyCodes>();

        api.MapDelete("/me/codes", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new TurnOffMyCodes(), ct).ToHttp()).WithAction<TurnOffMyCodes>();

        // With the link an Admin handed over. Nobody is signed in; the link is the right to do it.
        api.MapPost("/auth/local/password", (SetPasswordRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetPassword { UserId = req.UserId, Token = req.Token ?? "", Password = req.Password ?? "" }, ct).ToHttp()).AllowAnonymous().WithAction<SetPassword>();

        // Sign-in methods of plugins (contract 0.8.0). The list holds only each method's id and button text.
        api.MapGet("/auth/methods", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListSignInMethods(), ct).ToHttp()).AllowAnonymous().WithAction<ListSignInMethods>();

        api.MapGet("/auth/methods/outcome", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadSignInOutcome(), ct).ToHttp()).AllowAnonymous().WithAction<ReadSignInOutcome>();

        // The browser goes here from the sign-in button, and is sent on to the provider.
        api.MapGet("/auth/methods/{plugin}/{method}/start", async (string plugin, string method, string? returnTo, HttpContext http, IActionBus bus, CancellationToken ct) =>
            Redirect(http, await bus.SendAsync(new StartSignIn { Plugin = plugin, Method = method, ReturnTo = returnTo }, ct)))
            .AllowAnonymous().WithAction<StartSignIn>();

        // The provider sends the browser back here: the redirect address registered with it.
        api.MapGet("/auth/methods/{plugin}/{method}/callback", async (string plugin, string method, HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var query = http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
            return Redirect(http, await bus.SendAsync(new FinishSignIn { Plugin = plugin, Method = method, Query = query }, ct));
        }).AllowAnonymous().WithAction<FinishSignIn>();

        api.MapPost("/auth/signout", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SignOut(), ct).ToHttp()).WithAction<SignOut>();

        api.MapGet("/me", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadMe(), ct).ToHttp()).WithAction<ReadMe>();
    }

    private static IResult Redirect(HttpContext http, Outcome<SignInRedirect> r)
    {
        // Every answer names a one-time state or code: nothing is cached, and no address of this page is sent on.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        return r.Ok ? Results.Redirect(r.Value!.Location) : Results.StatusCode(r.Problem!.Status);
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
