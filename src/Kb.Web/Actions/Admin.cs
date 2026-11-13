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

/// <summary>Makes a new link with which a local account's password is set. Core only: the answer carries the token.</summary>
[Action("user.password.link")]
public sealed record NewPasswordLink : IAction<InvitedUser>
{
    public required Guid UserId { get; init; }
}

/// <summary>
/// Deletes a local account. Core only. The account is closed at once (sessions, API tokens, sign-in) and holds nothing afterwards;
/// its row stays, so history still names the person.
/// </summary>
[Action("user.delete")]
public sealed record DeleteUser : IAction<Unit>
{
    public required Guid UserId { get; init; }
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
        var users = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null).OrderBy(u => u.DisplayName)
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
        // Only a permission that exists now can be given. Taking one back works for any stored grant: its plugin may be off or gone.
        if (granted && !PluginPermissions.Global(plugins).Contains(permission)) return Problem.Invalid("permission", "Not a global permission of a loaded plugin.");
        if (!await db.Users.AnyAsync(u => u.Id == user, ctx.Cancel)) return Problem.NotFound;

        var row = await db.UserPermissions.FirstOrDefaultAsync(p => p.UserId == user && p.Permission == permission, ctx.Cancel);
        if (!granted && row is null && !PluginPermissions.Global(plugins).Contains(permission)) return Problem.Invalid("permission", "Not a global permission of a loaded plugin.");
        if (granted && row is null) db.UserPermissions.Add(new Kb.Core.Plugins.UserPermission { UserId = user, Permission = permission });
        if (!granted && row is not null) db.UserPermissions.Remove(row);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) when (granted) { db.ChangeTracker.Clear(); } // given by another request at the same moment: twice is once
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
        if (string.IsNullOrWhiteSpace(req.DisplayName)) return Problem.Invalid("displayName", "Required.");
        if (req.UserName.Trim().Length > 64 || req.DisplayName.Trim().Length > 100) return Problem.Invalid("userName", "Too long.");
        var u = new KbUser { UserName = req.UserName.Trim(), DisplayName = req.DisplayName.Trim(), Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(), Source = UserSource.Local, IsAdmin = req.IsAdmin, CreatedAt = time.GetUtcNow(), LockoutEnabled = true };
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

internal sealed class NewPasswordLinkHandler(KbDbContext db, UserManager<KbUser> users, IAccess access) : IActionHandler<NewPasswordLink, InvitedUser>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<NewPasswordLink> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.UserId;
        return (_user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<InvitedUser>> HandleAsync(ActionContext<NewPasswordLink> ctx)
    {
        var u = _user!;
        // The break-glass password is the server's secret, and an account of a directory has its password there.
        if (u.IsBreakGlass) return Problem.Conflict("The break-glass account has no password link.");
        if (u.Source != UserSource.Local || u.DeactivatedAt is not null) return Problem.Conflict("Only an active local user has a password.");
        var token = await users.GeneratePasswordResetTokenAsync(u);
        ctx.Audit(AuditActions.UserPasswordLink, "user", u.Id.ToString());
        return new InvitedUser(u.Id, token);
    }
}

internal sealed class DeleteUserHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<DeleteUser, Unit>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteUser> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.UserId;
        return (_user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteUser> ctx)
    {
        var u = _user!;
        if (u.Id == ctx.Actor.UserId) return Problem.Conflict("You can't delete your own account.");
        if (u.IsBreakGlass) return Problem.Conflict("The break-glass account can't be deleted.");
        if (u.Source != UserSource.Local) return Problem.Conflict("Only local users can be deleted.");
        // Without an Administrator besides the break-glass account the app would ask to be set up again.
        if (u.IsAdmin && u.DeactivatedAt is null && !await db.Users.AnyAsync(x => x.Id != u.Id && x.IsAdmin && !x.IsBreakGlass && x.DeactivatedAt == null))
            return Problem.Conflict("The last administrator can't be deleted.");

        var (now, name, id) = (time.GetUtcNow(), u.UserName ?? "", u.Id.ToString("D"));
        // The account is closed: nothing to sign in with, a new stamp (every open session ends at its next request), no rights.
        // The row and the display name stay, so pages and audit entries still name their author.
        (u.DeletedAt, u.DeactivatedAt) = (now, u.DeactivatedAt ?? now);
        (u.PasswordHash, u.SecurityStamp, u.IsAdmin, u.CanAnnounce) = (null, Guid.NewGuid().ToString("N"), false, false);
        (u.Email, u.NormalizedEmail, u.PhoneNumber) = (null, null, null);
        // Its user name is free for a new account; this one keeps a name nobody can type at the sign-in.
        u.UserName = $"deleted-{u.Id:N}";
        u.NormalizedUserName = u.UserName.ToUpperInvariant();
        // What the account held goes with it. API tokens are revoked, not removed: their use is in the audit log.
        db.SpaceRoleAssignments.RemoveRange(await db.SpaceRoleAssignments.Where(a => a.PrincipalType == PrincipalType.User && a.PrincipalId == id).ToListAsync());
        db.SpaceAccessGrants.RemoveRange(await db.SpaceAccessGrants.Where(g => g.UserId == u.Id).ToListAsync());
        db.AccessRequests.RemoveRange(await db.AccessRequests.Where(r => r.RequesterId == u.Id && r.Status == Kb.Core.Access.AccessRequestStatus.Pending).ToListAsync());
        db.UserPermissions.RemoveRange(await db.UserPermissions.Where(p => p.UserId == u.Id).ToListAsync());
        db.KbGroupMembers.RemoveRange(await db.KbGroupMembers.Where(m => m.UserId == u.Id).ToListAsync());
        db.PageDrafts.RemoveRange(await db.PageDrafts.Where(d => d.UserId == u.Id).ToListAsync());
        db.Notifications.RemoveRange(await db.Notifications.Where(n => n.UserId == u.Id).ToListAsync());
        foreach (var token in await db.ApiTokens.Where(t => t.OwnerUserId == u.Id && t.RevokedAt == null).ToListAsync()) token.RevokedAt = now;
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.UserDelete, "user", u.Id.ToString(), data: new Dictionary<string, string> { ["userName"] = name });
        return new Unit();
    }
}
