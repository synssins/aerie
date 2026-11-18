using System.Security.Cryptography;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Data;
using Kb.Web.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Sign-in providers an Admin adds (contract 0.9.0), on the page of a plugin that offers them (SignInProvidersReg). Each is a sign-in method
// of its own: a stable id that is not its title (its redirect address keeps working after a rename), its settings under that id. Each
// change applies at once, without a restart, and is audited.

public sealed record SignInProviderRequest(string? Title);

/// <summary>A provider an Admin added: its id, its title (the button's text), and the redirect address to register at the provider.</summary>
public sealed record SignInProviderDto(string Id, string Title, string RedirectAddress);

[Action("plugin.signin.provider.add")]
public sealed record AddSignInProvider : IAction<SignInProviderDto>
{
    public required string PluginId { get; init; }
    public required string Title { get; init; }
}

[Action("plugin.signin.provider.rename")]
public sealed record RenameSignInProvider : IAction<SignInProviderDto>
{
    public required string PluginId { get; init; }
    public required string ProviderId { get; init; }
    public required string Title { get; init; }
}

[Action("plugin.signin.provider.remove")]
public sealed record RemoveSignInProvider : IAction<Unit>
{
    public required string PluginId { get; init; }
    public required string ProviderId { get; init; }
}

internal static class SignInProviderRules
{
    /// <summary>403 for anyone but an Admin; 404 for a plugin that is not loaded or offers no providers.</summary>
    public static async Task<Problem?> AllowedAsync(IAccess access, PluginRegistry registry, PluginSettingsStore settings, Actor actor, string pluginId, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(actor, ct)) return Problem.Forbidden;
        return registry.Find(pluginId) is { Loaded: true } && settings.OffersProviders(pluginId) ? null : Problem.NotFound;
    }

    public static Problem? Title(string title) => SignInSettings.IsTitle(title) ? null : Problem.Invalid("title", "A title is 1 to 60 characters.");

    public static SignInProviderDto Dto(HttpRequest request, string pluginId, SignInSettings.Provider p) =>
        new(p.Id, p.Title, SignInMethods.RedirectAddress(request, new SignInMethodInfo(pluginId, p.Id, p.Title, null)));
}

internal sealed class AddSignInProviderHandler(IAccess access, PluginRegistry registry, PluginSettingsStore settings, SignInMethods methods, IHttpContextAccessor http)
    : IActionHandler<AddSignInProvider, SignInProviderDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<AddSignInProvider> ctx) =>
        SignInProviderRules.AllowedAsync(access, registry, settings, ctx.Actor, ctx.Action.PluginId, ctx.Cancel);

    public async Task<Outcome<SignInProviderDto>> HandleAsync(ActionContext<AddSignInProvider> ctx)
    {
        var (pluginId, title) = (ctx.Action.PluginId, ctx.Action.Title.Trim());
        if (SignInProviderRules.Title(title) is { } bad) return bad;
        var list = settings.Providers(pluginId);
        if (list.Count >= SignInSettings.MaxProviders) return Problem.Conflict($"A plugin has at most {SignInSettings.MaxProviders} providers.");
        // The id is random, not made from the title: the redirect address registered at the provider must outlive a rename.
        string id;
        do id = "p" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
        while (methods.Find(pluginId, id) is not null);
        var made = new SignInSettings.Provider(id, title);
        await settings.SaveProvidersAsync(pluginId, [.. list, made], null, ctx.Actor.UserId!.Value);
        ctx.Audit(AuditActions.SignInProviderAdd, "plugin", pluginId, data: new Dictionary<string, string> { ["provider"] = id, ["title"] = title });
        return SignInProviderRules.Dto(http.HttpContext!.Request, pluginId, made);
    }
}

internal sealed class RenameSignInProviderHandler(IAccess access, PluginRegistry registry, PluginSettingsStore settings, IHttpContextAccessor http)
    : IActionHandler<RenameSignInProvider, SignInProviderDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RenameSignInProvider> ctx)
    {
        if (await SignInProviderRules.AllowedAsync(access, registry, settings, ctx.Actor, ctx.Action.PluginId, ctx.Cancel) is { } refused) return refused;
        return settings.Providers(ctx.Action.PluginId).Any(p => p.Id == ctx.Action.ProviderId) ? null : Problem.NotFound;
    }

    public async Task<Outcome<SignInProviderDto>> HandleAsync(ActionContext<RenameSignInProvider> ctx)
    {
        var (pluginId, id, title) = (ctx.Action.PluginId, ctx.Action.ProviderId, ctx.Action.Title.Trim());
        if (SignInProviderRules.Title(title) is { } bad) return bad;
        var list = settings.Providers(pluginId);
        var before = list.First(p => p.Id == id);
        var renamed = before with { Title = title };
        await settings.SaveProvidersAsync(pluginId, [.. list.Select(p => p.Id == id ? renamed : p)], null, ctx.Actor.UserId!.Value);
        ctx.Audit(AuditActions.SignInProviderRename, "plugin", pluginId, data: new Dictionary<string, string> { ["provider"] = id, ["from"] = before.Title, ["title"] = title });
        return SignInProviderRules.Dto(http.HttpContext!.Request, pluginId, renamed);
    }
}

/// <summary>
/// Removes a provider and its settings. Accounts linked through it keep their link (issuer and subject): they have no password, so they cannot
/// sign in until a provider with the same issuer is set up again, which finds them by the same link. The audit entry counts them.
/// </summary>
internal sealed class RemoveSignInProviderHandler(IAccess access, PluginRegistry registry, PluginSettingsStore settings, KbDbContext db)
    : IActionHandler<RemoveSignInProvider, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveSignInProvider> ctx)
    {
        if (await SignInProviderRules.AllowedAsync(access, registry, settings, ctx.Actor, ctx.Action.PluginId, ctx.Cancel) is { } refused) return refused;
        return settings.Providers(ctx.Action.PluginId).Any(p => p.Id == ctx.Action.ProviderId) ? null : Problem.NotFound;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveSignInProvider> ctx)
    {
        var (pluginId, id) = (ctx.Action.PluginId, ctx.Action.ProviderId);
        var list = settings.Providers(pluginId);
        var gone = list.First(p => p.Id == id);
        var linked = await SignInProviderLinks.CountAsync(db, settings.Get(pluginId, SignInSettings.Issuer(id)), ctx.Cancel);
        await settings.SaveProvidersAsync(pluginId, [.. list.Where(p => p.Id != id)], id, ctx.Actor.UserId!.Value);
        ctx.Audit(AuditActions.SignInProviderRemove, "plugin", pluginId,
            data: new Dictionary<string, string> { ["provider"] = id, ["title"] = gone.Title, ["linkedAccounts"] = linked.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return new Unit();
    }
}

internal static class SignInProviderLinks
{
    /// <summary>The accounts (not deleted) linked to this issuer: those a provider with it signs in.</summary>
    public static async Task<int> CountAsync(KbDbContext db, string? issuer, CancellationToken ct) =>
        issuer?.Trim() is { Length: > 0 } i ? await db.Users.CountAsync(u => u.ExternalIssuer == i && u.DeletedAt == null, ct) : 0;
}
