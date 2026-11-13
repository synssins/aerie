using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>
/// Makes a local account without a password. Core only: the answer carries the token with which the password is set,
/// so no plugin may hook or follow this action.
/// </summary>
[Action("user.invite")]
public sealed record InviteUser : IAction<InvitedUser>
{
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsAdmin { get; init; }
}

/// <summary>The new account, and the token with which its password is set. The token is part of the answer; it must never reach a log.</summary>
public sealed record InvitedUser(Guid Id, string SetPasswordToken)
{
    public override string ToString() => $"InvitedUser {{ Id = {Id} }}";
}

// Every action here is an Admin's: AuthorizeAsync answers 403 to anyone else. An Admin is always a signed-in person, so ctx.Actor.UserId is set in HandleAsync.

internal sealed class VerifyAuditHandler(IAuditLog audit, IAccess access) : IActionHandler<VerifyAudit, AuditVerifyResult>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<VerifyAudit> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<AuditVerifyResult>> HandleAsync(ActionContext<VerifyAudit> ctx) => await audit.VerifyAsync(ctx.Cancel);
}

internal sealed class ListAuditHandler(KbDbContext db, IAccess access) : IActionHandler<ListAudit, IReadOnlyList<AuditEntryDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListAudit> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<AuditEntryDto>>> HandleAsync(ActionContext<ListAudit> ctx)
    {
        var n = Math.Clamp(ctx.Action.Take ?? 100, 1, 500);
        return Outcome.List(await (from a in db.AuditEntries.AsNoTracking()
                                   orderby a.Sequence descending
                                   join u in db.Users on a.ActorId equals u.Id into au
                                   from u in au.DefaultIfEmpty()
                                   select new AuditEntryDto(a.Sequence, a.At, a.Action, u == null ? null : u.DisplayName, a.TargetType, a.TargetId))
                                  .Take(n).ToListAsync(ctx.Cancel));
    }
}

internal sealed class ListUsersHandler(KbDbContext db, IAccess access, Kb.Core.Plugins.PluginRegistry plugins) : IActionHandler<ListUsers, IReadOnlyList<AdminUserDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListUsers> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<AdminUserDto>>> HandleAsync(ActionContext<ListUsers> ctx)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName)
            .Select(u => new AdminUserDto(u.Id, u.UserName!, u.DisplayName, u.Source.ToString(), u.IsAdmin, u.IsBreakGlass, u.CreatedAt, u.DeactivatedAt, u.CanAnnounce, null))
            .ToListAsync(ctx.Cancel);
        // The global plugin permissions each person holds: an Admin every one, anyone else those they were given.
        var (all, grants) = (PluginPermissions.Global(plugins), await PluginPermissions.GrantsAsync(db, plugins, ctx.Cancel));
        return Outcome.List<AdminUserDto>([.. users.Select(u => u with { Permissions = u.IsAdmin ? all : [.. grants[u.Id].Order(StringComparer.Ordinal)] })]);
    }
}

/// <summary>Gives a person a global permission of a plugin, or takes it back. Core only.</summary>
[Action("user.permissions.set")]
public sealed record SetUserPermission : IAction<Unit>
{
    public required Guid UserId { get; init; }
    /// <summary>"&lt;plugin id&gt;:&lt;key&gt;".</summary>
    public required string Permission { get; init; }
    public required bool Granted { get; init; }
}

internal sealed class SetUserPermissionHandler(KbDbContext db, IAccess access, Kb.Core.Plugins.PluginRegistry plugins) : IActionHandler<SetUserPermission, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SetUserPermission> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetUserPermission> ctx)
    {
        var (user, permission, granted) = (ctx.Action.UserId, ctx.Action.Permission, ctx.Action.Granted);
        if (!PluginPermissions.Global(plugins).Contains(permission)) return Problem.Invalid("permission", "Not a global permission of a loaded plugin.");
        if (!await db.Users.AnyAsync(u => u.Id == user, ctx.Cancel)) return Problem.NotFound;

        var row = await db.UserPermissions.FirstOrDefaultAsync(p => p.UserId == user && p.Permission == permission, ctx.Cancel);
        if (granted && row is null) db.UserPermissions.Add(new Kb.Core.Plugins.UserPermission { UserId = user, Permission = permission });
        if (!granted && row is not null) db.UserPermissions.Remove(row);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.UserPermissionsSet, "user", user.ToString(), data: new Dictionary<string, string> { ["permission"] = permission, ["granted"] = granted.ToString() });
        return new Unit();
    }
}

internal sealed class InviteUserHandler(UserManager<KbUser> users, IAccess access, TimeProvider time) : IActionHandler<InviteUser, InvitedUser>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<InviteUser> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<InvitedUser>> HandleAsync(ActionContext<InviteUser> ctx)
    {
        var req = ctx.Action;
        if (string.IsNullOrWhiteSpace(req.UserName)) return Problem.Invalid("userName", "Required.");
        var u = new KbUser { UserName = req.UserName.Trim(), DisplayName = req.DisplayName, Email = req.Email, Source = UserSource.Local, IsAdmin = req.IsAdmin, CreatedAt = time.GetUtcNow(), LockoutEnabled = true };
        var result = await users.CreateAsync(u);
        if (!result.Succeeded) return new Problem(400, Fields: new Dictionary<string, string[]> { ["user"] = [.. result.Errors.Select(e => e.Description)] });
        var token = await users.GeneratePasswordResetTokenAsync(u);
        ctx.Audit(AuditActions.UserInvite, "user", u.Id.ToString(), data: new Dictionary<string, string> { ["isAdmin"] = req.IsAdmin.ToString() });
        // shortcut: the set-password token is returned to the inviting admin; replace with an emailed one-time link once email is configured.
        return new InvitedUser(u.Id, token);
    }
}

internal sealed class DisableLocalSignInHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<DisableLocalSignIn, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<DisableLocalSignIn> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DisableLocalSignIn> ctx)
    {
        if (await db.GetAsync(RuntimeSettingKeys.ExternalAdminVerifiedAt, ctx.Cancel) is null)
            return Problem.Conflict(body: new { reason = "external-admin-not-verified" });
        await db.SetAsync(RuntimeSettingKeys.LocalSignInDisabled, "true", ctx.Actor.UserId, time.GetUtcNow());
        ctx.Audit(AuditActions.SettingsChange, "setting", RuntimeSettingKeys.LocalSignInDisabled, data: new Dictionary<string, string> { ["value"] = "true" });
        return new Unit();
    }
}

internal sealed class SetPublicSpaceHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SetPublicSpace, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SetPublicSpace> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetPublicSpace> ctx)
    {
        var enabled = ctx.Action.Enabled;
        await db.SetAsync(RuntimeSettingKeys.PublicSpaceEnabled, enabled ? "true" : "false", ctx.Actor.UserId, time.GetUtcNow());
        ctx.Audit(AuditActions.SettingsChange, "setting", RuntimeSettingKeys.PublicSpaceEnabled, data: new Dictionary<string, string> { ["value"] = enabled.ToString() });
        return new Unit();
    }
}
