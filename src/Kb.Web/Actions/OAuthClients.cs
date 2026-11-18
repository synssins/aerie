using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>A registered OAuth 2.0 client as administrators see it. The secret is never in it.</summary>
public sealed record OAuthClientDto(Guid Id, string ClientId, string Name, string Type, IReadOnlyList<string> RedirectUris, IReadOnlyList<string> Scopes,
    bool RequirePkce, bool Enabled, int ActiveGrants, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? SecretRotatedAt);

/// <summary>A client with its secret: the answer to create and rotate, the only two places the secret is ever shown.</summary>
public sealed record OAuthClientCreatedDto(string? ClientSecret, OAuthClientDto Client);

public sealed record OAuthClientRequest(string? Name, string? Type, IReadOnlyList<string>? RedirectUris, IReadOnlyList<string>? Scopes, bool? RequirePkce, bool? Enabled);

public sealed record CompatAuthSettingsDto(bool BasicPasswordEnabled, bool AllowHttpRedirects, int CodeSeconds, int AccessTokenSeconds, int RefreshTokenDays, int? GrantMaxDays);

[Action("oauth.client.list", Read = true)]
public sealed record ListOAuthClients : IAction<IReadOnlyList<OAuthClientDto>>;

[Action("oauth.client.read", Read = true)]
public sealed record ReadOAuthClient : IAction<OAuthClientDto>
{
    public required Guid Id { get; init; }
}

/// <summary>An Admin registers an application. A confidential client's secret is in the answer once and never again.</summary>
[Action("oauth.client.create")]
public sealed record CreateOAuthClient : IAction<OAuthClientCreatedDto>
{
    public required OAuthClientRequest Request { get; init; }
}

/// <summary>Changes name, redirect URIs, scopes, PKCE and enabled; the type stays. Removing a scope ends the tokens that hold it.</summary>
[Action("oauth.client.update")]
public sealed record UpdateOAuthClient : IAction<OAuthClientDto>
{
    public required Guid Id { get; init; }
    public required OAuthClientRequest Request { get; init; }
}

/// <summary>Deletes the client and, with it, every grant and token issued to it.</summary>
[Action("oauth.client.delete")]
public sealed record DeleteOAuthClient : IAction<Unit>
{
    public required Guid Id { get; init; }
}

/// <summary>Replaces a confidential client's secret and shows the new one once. Existing tokens stay valid.</summary>
[Action("oauth.client.secret.rotate")]
public sealed record RotateOAuthClientSecret : IAction<OAuthClientCreatedDto>
{
    public required Guid Id { get; init; }
}

[Action("auth.compat.read", Read = true)]
public sealed record ReadCompatAuthSettings : IAction<CompatAuthSettingsDto>;

[Action("auth.compat.save")]
public sealed record SaveCompatAuthSettings : IAction<CompatAuthSettingsDto>
{
    public required CompatAuthSettingsDto Settings { get; init; }
}

/// <summary>What the client handlers share: the Admin check, the rules of identity.md "Clients" and "Redirect URI rules", and the DTO.</summary>
internal static class OAuthClientRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<string> Uris(OAuthClient c) => JsonSerializer.Deserialize<List<string>>(c.RedirectUrisJson, Json) ?? [];

    public static async Task<OAuthClientDto> DtoAsync(KbDbContext db, OAuthClient c, CancellationToken ct)
    {
        var by = await db.Users.Where(u => u.Id == c.CreatedBy).Select(u => u.UserName).FirstOrDefaultAsync(ct) ?? "";
        var grants = await db.OAuthGrants.CountAsync(g => g.ClientId == c.Id, ct);
        return new OAuthClientDto(c.Id, c.ClientId, c.Name, c.Type, Uris(c), c.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), c.RequirePkce, c.Enabled, grants, by, c.CreatedAt, c.SecretRotatedAt);
    }

    /// <summary>The checked fields, or the first one that is wrong. Fields left out keep the client's value (for update).</summary>
    public static async Task<(string Name, string Type, List<string> Uris, string Scopes, bool Pkce, bool Enabled)?> CheckAsync(
        KbDbContext db, OAuthClientRequest r, OAuthClient? current, Action<Problem> fail, CancellationToken ct)
    {
        var name = (r.Name ?? current?.Name ?? "").Trim();
        if (name.Length is 0 or > 100) { fail(Problem.Invalid("name", "1 to 100 characters.")); return null; }
        var type = current?.Type ?? r.Type ?? "";
        if (current is null && type is not ("confidential" or "public")) { fail(Problem.Invalid("type", "confidential or public.")); return null; }
        if (current is not null && r.Type is not null && r.Type != current.Type) { fail(Problem.Invalid("type", "A client's type cannot change.")); return null; }
        var uris = r.RedirectUris?.ToList() ?? (current is null ? [] : [.. Uris(current)]);
        if (uris.Count is 0 or > 20) { fail(Problem.Invalid("redirectUris", "1 to 20 redirect URIs.")); return null; }
        var allowHttp = (await CompatAuthSettings.LoadAsync(db, ct)).AllowHttpRedirects;
        foreach (var uri in uris)
            if (OAuthRedirects.Invalid(uri, allowHttp) is { } why) { fail(Problem.Invalid("redirectUris", $"{uri}: {why}")); return null; }
        var scopes = r.Scopes is null ? current?.Scopes ?? "" : OAuthScopes.Parse(string.Join(' ', r.Scopes)) is { Count: > 0 } set ? OAuthScopes.Format(set) : null;
        if (string.IsNullOrEmpty(scopes)) { fail(Problem.Invalid("scopes", "One or more of READ, WRITE, ADMIN, SYSTEM_ADMIN.")); return null; }
        var pkce = r.RequirePkce ?? current?.RequirePkce ?? true;
        if (!pkce && type == "public") { fail(Problem.Invalid("requirePkce", "A public client always uses PKCE.")); return null; }
        return (name, type, uris.Distinct(StringComparer.Ordinal).ToList(), scopes, pkce, r.Enabled ?? current?.Enabled ?? true);
    }

    public static string HashSecret(IPasswordHasher<KbUser> hasher, string secret) => hasher.HashPassword(new KbUser(), secret);
}

internal sealed class ListOAuthClientsHandler(KbDbContext db, IAccess access) : IActionHandler<ListOAuthClients, IReadOnlyList<OAuthClientDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListOAuthClients> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<OAuthClientDto>>> HandleAsync(ActionContext<ListOAuthClients> ctx)
    {
        var list = new List<OAuthClientDto>();
        foreach (var c in await db.OAuthClients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ctx.Cancel)) list.Add(await OAuthClientRules.DtoAsync(db, c, ctx.Cancel));
        return list;
    }
}

internal sealed class ReadOAuthClientHandler(KbDbContext db, IAccess access) : IActionHandler<ReadOAuthClient, OAuthClientDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadOAuthClient> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<OAuthClientDto>> HandleAsync(ActionContext<ReadOAuthClient> ctx) =>
        await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ctx.Action.Id, ctx.Cancel) is { } c ? await OAuthClientRules.DtoAsync(db, c, ctx.Cancel) : Problem.NotFound;
}

internal sealed class CreateOAuthClientHandler(KbDbContext db, IAccess access, IPasswordHasher<KbUser> hasher, TimeProvider time) : IActionHandler<CreateOAuthClient, OAuthClientCreatedDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateOAuthClient> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<OAuthClientCreatedDto>> HandleAsync(ActionContext<CreateOAuthClient> ctx)
    {
        Problem? problem = null;
        if (await OAuthClientRules.CheckAsync(db, ctx.Action.Request, null, p => problem = p, ctx.Cancel) is not { } f) return problem!;
        var secret = f.Type == "confidential" ? OAuthSecrets.NewClientSecret() : null;
        var client = new OAuthClient
        {
            ClientId = OAuthSecrets.NewClientId(), SecretHash = secret is null ? null : OAuthClientRules.HashSecret(hasher, secret), Name = f.Name, Type = f.Type,
            RedirectUrisJson = JsonSerializer.Serialize(f.Uris), Scopes = f.Scopes, RequirePkce = f.Pkce, Enabled = f.Enabled, CreatedBy = ctx.Actor.UserId!.Value, CreatedAt = time.GetUtcNow(),
        };
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(ctx.Cancel);
        // The secret is never in the audit entry, only that the client was made and how.
        ctx.Audit(AuditActions.OAuthClientCreate, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string>
            { ["clientId"] = client.ClientId, ["name"] = client.Name, ["type"] = client.Type, ["scopes"] = client.Scopes, ["redirectUris"] = string.Join(' ', f.Uris), ["requirePkce"] = client.RequirePkce.ToString(), ["ip"] = ctx.Actor.RemoteIp ?? "" });
        return new OAuthClientCreatedDto(secret, await OAuthClientRules.DtoAsync(db, client, ctx.Cancel));
    }
}

internal sealed class UpdateOAuthClientHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<UpdateOAuthClient, OAuthClientDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateOAuthClient> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<OAuthClientDto>> HandleAsync(ActionContext<UpdateOAuthClient> ctx)
    {
        if (await db.OAuthClients.FirstOrDefaultAsync(c => c.Id == ctx.Action.Id, ctx.Cancel) is not { } client) return Problem.NotFound;
        Problem? problem = null;
        if (await OAuthClientRules.CheckAsync(db, ctx.Action.Request, client, p => problem = p, ctx.Cancel) is not { } f) return problem!;
        var removed = client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Except(f.Scopes.Split(' ')).ToList();
        (client.Name, client.RedirectUrisJson, client.Scopes, client.RequirePkce, client.Enabled) = (f.Name, JsonSerializer.Serialize(f.Uris), f.Scopes, f.Pkce, f.Enabled);
        await db.SaveChangesAsync(ctx.Cancel);
        if (removed.Count > 0)
        {
            // Tokens that hold a scope the client no longer has are ended; their users authorize again (identity.md, Clients).
            var grants = db.OAuthGrants.Where(g => g.ClientId == client.Id).Select(g => g.Id);
            var families = await db.OAuthTokenFamilies.Where(x => grants.Contains(x.GrantId) && x.RevokedAt == null).ToListAsync(ctx.Cancel);
            var now = time.GetUtcNow();
            foreach (var family in families.Where(x => removed.Any(s => x.Scopes.Split(' ').Contains(s))))
                (family.RevokedAt, family.RevokeReason) = (now, "client scope removed");
            // A code not yet exchanged and a consent not yet answered would still turn into tokens with the removed scope.
            db.OAuthCodes.RemoveRange((await db.OAuthCodes.Where(c => grants.Contains(c.GrantId) && c.UsedAt == null).ToListAsync(ctx.Cancel)).Where(c => removed.Any(s => c.Scopes.Split(' ').Contains(s))));
            db.OAuthAuthorizeRequests.RemoveRange((await db.OAuthAuthorizeRequests.Where(r => r.ClientId == client.Id).ToListAsync(ctx.Cancel)).Where(r => removed.Any(s => r.Scopes.Split(' ').Contains(s))));
            await db.SaveChangesAsync(ctx.Cancel);
        }
        ctx.Audit(AuditActions.OAuthClientUpdate, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string>
            { ["clientId"] = client.ClientId, ["name"] = client.Name, ["scopes"] = client.Scopes, ["redirectUris"] = string.Join(' ', f.Uris), ["requirePkce"] = client.RequirePkce.ToString(), ["enabled"] = client.Enabled.ToString(), ["ip"] = ctx.Actor.RemoteIp ?? "" });
        return await OAuthClientRules.DtoAsync(db, client, ctx.Cancel);
    }
}

internal sealed class DeleteOAuthClientHandler(KbDbContext db, IAccess access) : IActionHandler<DeleteOAuthClient, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteOAuthClient> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteOAuthClient> ctx)
    {
        if (await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ctx.Action.Id, ctx.Cancel) is not { } client) return Problem.NotFound;
        // The database removes its requests, grants, codes and tokens with it.
        await db.OAuthClients.Where(c => c.Id == client.Id).ExecuteDeleteAsync(ctx.Cancel);
        ctx.Audit(AuditActions.OAuthClientDelete, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string> { ["clientId"] = client.ClientId, ["name"] = client.Name, ["ip"] = ctx.Actor.RemoteIp ?? "" });
        return new Unit();
    }
}

internal sealed class RotateOAuthClientSecretHandler(KbDbContext db, IAccess access, IPasswordHasher<KbUser> hasher, TimeProvider time) : IActionHandler<RotateOAuthClientSecret, OAuthClientCreatedDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RotateOAuthClientSecret> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<OAuthClientCreatedDto>> HandleAsync(ActionContext<RotateOAuthClientSecret> ctx)
    {
        if (await db.OAuthClients.FirstOrDefaultAsync(c => c.Id == ctx.Action.Id, ctx.Cancel) is not { } client) return Problem.NotFound;
        if (client.IsPublic) return Problem.Invalid("type", "A public client has no secret.");
        var secret = OAuthSecrets.NewClientSecret();
        (client.SecretHash, client.SecretRotatedAt) = (OAuthClientRules.HashSecret(hasher, secret), time.GetUtcNow());
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.OAuthClientSecretRotate, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string> { ["clientId"] = client.ClientId, ["ip"] = ctx.Actor.RemoteIp ?? "" });
        return new OAuthClientCreatedDto(secret, await OAuthClientRules.DtoAsync(db, client, ctx.Cancel));
    }
}

internal sealed class ReadCompatAuthSettingsHandler(KbDbContext db, IAccess access) : IActionHandler<ReadCompatAuthSettings, CompatAuthSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadCompatAuthSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<CompatAuthSettingsDto>> HandleAsync(ActionContext<ReadCompatAuthSettings> ctx)
    {
        var s = await CompatAuthSettings.LoadAsync(db, ctx.Cancel);
        return new CompatAuthSettingsDto(s.BasicPasswordEnabled, s.AllowHttpRedirects, s.CodeSeconds, s.AccessTokenSeconds, s.RefreshTokenDays, s.GrantMaxDays);
    }
}

internal sealed class SaveCompatAuthSettingsHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveCompatAuthSettings, CompatAuthSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveCompatAuthSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<CompatAuthSettingsDto>> HandleAsync(ActionContext<SaveCompatAuthSettings> ctx)
    {
        var d = ctx.Action.Settings;
        var s = new CompatAuthSettings(d.BasicPasswordEnabled, d.AllowHttpRedirects, d.CodeSeconds, d.AccessTokenSeconds, d.RefreshTokenDays, d.GrantMaxDays);
        if (s.Invalid() is { } why) return new Problem(400, why);
        var now = time.GetUtcNow();
        var values = new Dictionary<string, string?>
        {
            [RuntimeSettingKeys.CompatBasicPasswordEnabled] = s.BasicPasswordEnabled ? "true" : "false",
            [CompatAuthSettings.AllowHttpRedirectsKey] = s.AllowHttpRedirects ? "true" : "false",
            [CompatAuthSettings.CodeSecondsKey] = s.CodeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CompatAuthSettings.AccessTokenSecondsKey] = s.AccessTokenSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CompatAuthSettings.RefreshTokenDaysKey] = s.RefreshTokenDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CompatAuthSettings.GrantMaxDaysKey] = s.GrantMaxDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
        foreach (var (key, value) in values) await db.SetAsync(key, value, ctx.Actor.UserId, now, ctx.Cancel);
        ctx.Audit(AuditActions.SettingsChange, "setting", "Compat", data: values.ToDictionary(kv => kv.Key, kv => kv.Value ?? ""));
        return d;
    }
}
