using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Core only: how API tokens are made, listed and revoked is not part of the plugin contract, so these actions and their results are here
// and not in the contract assembly. No plugin can hook them: none can refuse a revoke or see a new token's text.

public sealed record ApiTokenDto(Guid Id, string Name, TokenKind Kind, string Prefix, IReadOnlyList<TokenScope> Scopes, Guid OwnerId, string Owner,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, string State);
/// <summary>The only answer that ever carries a token's text.</summary>
public sealed record ApiTokenCreatedDto(string Token, ApiTokenDto Info);
public sealed record TokenSettingsDto(int MaxDays);

[Action("token.create")]
public sealed record CreateToken : IAction<ApiTokenCreatedDto>
{
    public string? Name { get; init; }
    public required TokenKind Kind { get; init; }
    public IReadOnlyList<TokenScope>? Scopes { get; init; }
    public int? Days { get; init; }
    /// <summary>A service token only: the service account it is for. Without it a new account is made.</summary>
    public Guid? AccountId { get; init; }
}

/// <summary>The caller's own personal tokens.</summary>
[Action("token.list", Read = true)]
public sealed record ListMyTokens : IAction<IReadOnlyList<ApiTokenDto>>;

[Action("token.revoke")]
public sealed record RevokeToken : IAction<Unit>
{
    public required Guid TokenId { get; init; }
}

[Action("token.list.all", Read = true)]
public sealed record ListAllTokens : IAction<IReadOnlyList<ApiTokenDto>>;

[Action("token.settings.read", Read = true)]
public sealed record ReadTokenSettings : IAction<TokenSettingsDto>;

[Action("token.settings.save")]
public sealed record SaveTokenSettings : IAction<TokenSettingsDto>
{
    public required int MaxDays { get; init; }
}

internal sealed class CreateTokenHandler(KbDbContext db, IAccess access, UserManager<KbUser> users, TimeProvider time) : IActionHandler<CreateToken, ApiTokenCreatedDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateToken> ctx)
    {
        // Personal: anyone, for themselves. Service: Admins only, because its scopes are its access.
        if (ctx.Action.Kind == TokenKind.Service && !await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // a token names who made it
    }

    public async Task<Outcome<ApiTokenCreatedDto>> HandleAsync(ActionContext<CreateToken> ctx)
    {
        var (req, me) = (ctx.Action, ctx.Actor.UserId!.Value);
        var service = req.Kind == TokenKind.Service;
        if (!Enum.IsDefined(req.Kind)) return Problem.Invalid("kind", "Personal or Service.");
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Problem.Invalid("name", "1 to 100 characters.");
        var max = await ApiTokenAuth.MaxDaysAsync(db, ctx.Cancel);
        if (req.Days is not { } days || days < 1 || days > max) return Problem.Invalid("days", $"1 to {max}.");
        var scopes = (req.Scopes ?? []).Distinct().ToList();
        if (scopes.Count is 0 or > 50 || scopes.Any(s => !Enum.IsDefined(s.Access))) return Problem.Invalid("scopes", "At least one.");
        foreach (var spaceId in scopes.Select(s => s.SpaceId).OfType<Guid>().Distinct())
        {
            // A space the maker cannot see is answered like one that does not exist. A service token is made only by an Owner of the space it names.
            var allowed = service ? await access.CanAsync(ctx.Actor, spaceId, SpaceAction.ManageRolesSettingsSensitive, ctx.Cancel) : await access.RoleAsync(ctx.Actor, spaceId, ctx.Cancel) is not null;
            if (!allowed) return Problem.Invalid("scopes", "Unknown space.");
        }

        var now = time.GetUtcNow();
        var owner = me;
        if (service && req.AccountId is { } accountId)
        {
            if (!await db.Users.AnyAsync(u => u.Id == accountId && u.Source == UserSource.Service && u.DeactivatedAt == null, ctx.Cancel)) return Problem.Invalid("accountId", "Unknown account.");
            owner = accountId;
        }
        else if (service)
        {
            // The account has no password, so it cannot sign in. Pages the token changes carry its name.
            var account = new KbUser { UserName = "svc-" + Guid.NewGuid().ToString("N")[..16], DisplayName = name, Source = UserSource.Service, CreatedAt = now, LockoutEnabled = true };
            var made = await users.CreateAsync(account);
            if (!made.Succeeded) return Problem.Invalid("name", made.Errors.First().Description);
            owner = account.Id;
        }

        var text = TokenFormat.Generate();
        var token = new ApiToken
        {
            OwnerUserId = owner, Kind = req.Kind, Name = name, Hash = TokenFormat.Hash(text), Prefix = text[..TokenFormat.PrefixShown],
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(scopes), CreatedBy = me, CreatedAt = now, ExpiresAt = now.AddDays(days),
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
        // The text is not in the entry: it is in the answer, once, and nowhere else.
        ctx.Audit(AuditActions.TokenCreate, "token", token.Id.ToString(), data: new Dictionary<string, string>
        {
            ["name"] = name, ["kind"] = req.Kind.ToString(), ["days"] = days.ToString(CultureInfo.InvariantCulture),
            ["scopes"] = string.Join(", ", scopes.Select(s => $"{s.SpaceId?.ToString() ?? "all"}:{s.Access}")),
        });
        return new ApiTokenCreatedDto(text, (await TokenDtos.ListAsync(db, [token], now, CancellationToken.None))[0]);
    }
}

internal sealed class ListMyTokensHandler(KbDbContext db, TimeProvider time) : IActionHandler<ListMyTokens, IReadOnlyList<ApiTokenDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListMyTokens> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<IReadOnlyList<ApiTokenDto>>> HandleAsync(ActionContext<ListMyTokens> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var mine = await db.ApiTokens.AsNoTracking().Where(t => t.OwnerUserId == me && t.Kind == TokenKind.Personal).ToListAsync(ctx.Cancel);
        return Outcome.List(await TokenDtos.ListAsync(db, mine, time.GetUtcNow(), ctx.Cancel));
    }
}

internal sealed class RevokeTokenHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<RevokeToken, Unit>
{
    private ApiToken? _token;

    /// <summary>404 for a token that is not the caller's, unless the caller is an Admin.</summary>
    public async Task<Problem?> AuthorizeAsync(ActionContext<RevokeToken> ctx)
    {
        _token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == ctx.Action.TokenId, ctx.Cancel);
        var mine = _token is { Kind: TokenKind.Personal } && _token.OwnerUserId == ctx.Actor.UserId;
        return _token is null || !(mine || await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RevokeToken> ctx)
    {
        var token = _token!;
        if (token.RevokedAt is null)
        {
            token.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
            ctx.Audit(AuditActions.TokenRevoke, "token", token.Id.ToString(), data: new Dictionary<string, string> { ["name"] = token.Name });
        }
        return new Unit();
    }
}

internal sealed class ListAllTokensHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<ListAllTokens, IReadOnlyList<ApiTokenDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListAllTokens> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<ApiTokenDto>>> HandleAsync(ActionContext<ListAllTokens> ctx) =>
        Outcome.List(await TokenDtos.ListAsync(db, await db.ApiTokens.AsNoTracking().ToListAsync(ctx.Cancel), time.GetUtcNow(), ctx.Cancel));
}

internal sealed class ReadTokenSettingsHandler(KbDbContext db, IAccess access) : IActionHandler<ReadTokenSettings, TokenSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadTokenSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<TokenSettingsDto>> HandleAsync(ActionContext<ReadTokenSettings> ctx) => new TokenSettingsDto(await ApiTokenAuth.MaxDaysAsync(db, ctx.Cancel));
}

internal sealed class SaveTokenSettingsHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveTokenSettings, TokenSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveTokenSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<TokenSettingsDto>> HandleAsync(ActionContext<SaveTokenSettings> ctx)
    {
        var max = ctx.Action.MaxDays;
        if (max is < 1 or > 3650) return Problem.Invalid("maxDays", "1 to 3650.");
        var value = max.ToString(CultureInfo.InvariantCulture);
        await db.SetAsync(ApiTokenAuth.MaxDaysKey, value, ctx.Actor.UserId, time.GetUtcNow());
        ctx.Audit(AuditActions.SettingsChange, "setting", ApiTokenAuth.MaxDaysKey, data: new Dictionary<string, string> { ["value"] = value });
        return new TokenSettingsDto(max);
    }
}

internal static class TokenDtos
{
    /// <summary>Newest first, each with its owner's name and its state now. Never the token's text: only its hash is stored.</summary>
    public static async Task<List<ApiTokenDto>> ListAsync(KbDbContext db, IReadOnlyList<ApiToken> tokens, DateTimeOffset now, CancellationToken ct)
    {
        var ownerIds = tokens.Select(t => t.OwnerUserId).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. tokens.OrderByDescending(t => t.CreatedAt).Select(t => new ApiTokenDto(t.Id, t.Name, t.Kind, t.Prefix, t.Scopes(), t.OwnerUserId, owners.GetValueOrDefault(t.OwnerUserId, ""),
            t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt, t.RevokedAt is not null ? "Revoked" : t.ExpiresAt <= now ? "Expired" : "Active"))];
    }
}
