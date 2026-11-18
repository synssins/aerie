using System.Text.Json.Serialization;

namespace Kb.Web.Actions;

// Sign-in methods (contract 0.8.0; providers an Admin adds, 0.9.0): a plugin describes an OpenID Connect provider, the app runs the sign-in. Core only: no plugin can
// hook these actions. Nobody is signed in when they are sent, so each handler permits everyone and checks as the work.

/// <summary>A sign-in method on the sign-in screen: how the API names it ("{plugin}/{id}") and the text of its button. Nothing else.</summary>
public sealed record SignInMethodDto(string Id, string Name);

/// <summary>
/// What the sign-in screen shows after a sign-in method sent the browser back: the message of a refused sign-in (shown once), and
/// "code" or "enroll" when the person must give a one-time code (or set codes up) to finish.
/// </summary>
public sealed record SignInOutcome(string? Message, string? Step);

/// <summary>Where the browser goes next.</summary>
public sealed record SignInRedirect(string Location);

[Action("auth.methods.list", Read = true)]
public sealed record ListSignInMethods : IAction<IReadOnlyList<SignInMethodDto>>;

[Action("auth.methods.outcome")]
public sealed record ReadSignInOutcome : IAction<SignInOutcome>;

[Action("auth.method.start")]
public sealed record StartSignIn : IAction<SignInRedirect>
{
    public required string Plugin { get; init; }
    public required string Method { get; init; }
    /// <summary>A path on this site to go to once signed in; anything else is "/".</summary>
    public string? ReturnTo { get; init; }
}

/// <summary>The provider's answer: the query of the callback. It holds the authorization code, so it is never written out.</summary>
[Action("auth.method.finish")]
public sealed record FinishSignIn : IAction<SignInRedirect>
{
    public required string Plugin { get; init; }
    public required string Method { get; init; }
    [JsonIgnore] public required IReadOnlyDictionary<string, string> Query { get; init; }

    public override string ToString() => $"FinishSignIn {{ Plugin = {Plugin}, Method = {Method} }}";
}

internal sealed class ListSignInMethodsHandler(SignInMethods methods) : IActionHandler<ListSignInMethods, IReadOnlyList<SignInMethodDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListSignInMethods> ctx) => Task.FromResult<Problem?>(null);

    public Task<Outcome<IReadOnlyList<SignInMethodDto>>> HandleAsync(ActionContext<ListSignInMethods> ctx) =>
        Task.FromResult<Outcome<IReadOnlyList<SignInMethodDto>>>(methods.Ready.Select(m => new SignInMethodDto(m.Key, m.Name)).ToList());
}

internal sealed class ReadSignInOutcomeHandler(SignInFlow flow) : IActionHandler<ReadSignInOutcome, SignInOutcome>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadSignInOutcome> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<SignInOutcome>> HandleAsync(ActionContext<ReadSignInOutcome> ctx) => await flow.OutcomeAsync();
}

internal sealed class StartSignInHandler(SignInMethods methods, SignInFlow flow) : IActionHandler<StartSignIn, SignInRedirect>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<StartSignIn> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<SignInRedirect>> HandleAsync(ActionContext<StartSignIn> ctx)
    {
        // A method that is not there, or is not set up yet (a client id; for a provider an Admin added, an issuer too), is not on the sign-in screen either.
        if (methods.Find(ctx.Action.Plugin, ctx.Action.Method) is not { } m || !methods.IsReady(m) || methods.ClientId(m) is not { } clientId) return Problem.NotFound;
        return new SignInRedirect(await flow.StartAsync(m, clientId, ctx.Action.ReturnTo, ctx.Cancel));
    }
}

internal sealed class FinishSignInHandler(SignInMethods methods, SignInFlow flow) : IActionHandler<FinishSignIn, SignInRedirect>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<FinishSignIn> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<SignInRedirect>> HandleAsync(ActionContext<FinishSignIn> ctx) =>
        methods.Find(ctx.Action.Plugin, ctx.Action.Method) is { } m ? new SignInRedirect(await flow.FinishAsync(m, ctx.Action.Query, ctx.Cancel)) : Problem.NotFound;
}
