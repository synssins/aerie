using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Notifications;
using Kb.Core.Spaces;
using Microsoft.Extensions.Options;

namespace Kb.Core.Access;

public enum AccessRequestStatus { Pending, Approved, Denied, Cancelled }

public sealed class AccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpaceId { get; set; }
    public Guid RequesterId { get; set; }
    public SpaceRole RequestedRole { get; set; }
    public string? Reason { get; set; }
    public AccessRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public interface IAccessData
{
    Task AddRequestAsync(AccessRequest r, CancellationToken ct);
    Task<AccessRequest?> GetRequestAsync(Guid id, CancellationToken ct);
    Task SaveRequestAsync(AccessRequest r, CancellationToken ct);
    Task AddGrantAsync(SpaceAccessGrant g, CancellationToken ct);
    Task AddAssignmentAsync(SpaceRoleAssignment a, CancellationToken ct);
    /// <summary>User ids holding Owner on the space through a direct user assignment.</summary>
    Task<IReadOnlyList<Guid>> GetOwnerUserIdsAsync(Guid spaceId, CancellationToken ct);
}

public sealed class AccessDeniedException(string message) : Exception(message);
public sealed class NotFoundException(string message) : Exception(message);

public sealed class AccessService(
    IAccessData data,
    ISpaceAccessData spaces,
    ISpacePermissionService perms,
    INotifier notifier,
    IAuditLog audit,
    IOptions<KbOptions> options,
    TimeProvider time)
{
    public async Task<AccessRequest> RequestAsync(Guid spaceId, Guid requesterId, SpaceRole role, string? reason, CancellationToken ct = default)
    {
        var space = await spaces.GetSpaceAsync(spaceId, ct) ?? throw new NotFoundException("Space not found.");
        var requester = await spaces.GetPrincipalAsync(requesterId, ct) ?? throw new NotFoundException("User not found.");
        var r = new AccessRequest { SpaceId = spaceId, RequesterId = requesterId, RequestedRole = role, Reason = reason, Status = AccessRequestStatus.Pending, CreatedAt = time.GetUtcNow() };
        await data.AddRequestAsync(r, ct);
        await audit.AppendAsync(new(AuditActions.AccessRequest, requesterId, "space", spaceId.ToString(), spaceId, new Dictionary<string, string> { ["role"] = role.ToString() }), ct);

        var owners = await data.GetOwnerUserIdsAsync(spaceId, ct);
        var adminToSensitive = space.IsSensitive && requester.IsAdmin;
        await notifier.NotifyAsync(owners, NotificationKinds.AccessRequest,
            adminToSensitive ? $"An administrator requested access to sensitive space {space.Name}" : $"Access requested to {space.Name}",
            reason, $"/access-requests/{r.Id}", alsoEmail: adminToSensitive, ct);
        return r;
    }

    /// <summary>Only an Owner signed in to the app can approve (no email-link approval, by design).</summary>
    public async Task DecideAsync(Guid requestId, Guid deciderId, bool approve, CancellationToken ct = default)
    {
        var r = await data.GetRequestAsync(requestId, ct) ?? throw new NotFoundException("Request not found.");
        if (!await perms.CanAsync(deciderId, r.SpaceId, SpaceAction.ApproveAccessRequests, ct))
            throw new AccessDeniedException("Only a space Owner can decide access requests.");
        if (r.Status != AccessRequestStatus.Pending) throw new InvalidOperationException("Request already decided.");

        r.Status = approve ? AccessRequestStatus.Approved : AccessRequestStatus.Denied;
        r.DecidedBy = deciderId;
        r.DecidedAt = time.GetUtcNow();
        await data.SaveRequestAsync(r, ct);
        if (approve)
            await data.AddAssignmentAsync(new SpaceRoleAssignment { SpaceId = r.SpaceId, PrincipalType = PrincipalType.User, PrincipalId = r.RequesterId.ToString("D"), Role = r.RequestedRole }, ct);

        await audit.AppendAsync(new(AuditActions.AccessDecide, deciderId, "accessRequest", r.Id.ToString(), r.SpaceId,
            new Dictionary<string, string> { ["approved"] = approve.ToString(), ["requester"] = r.RequesterId.ToString() }), ct);
        await notifier.NotifyAsync([r.RequesterId], NotificationKinds.AccessDecision,
            approve ? "Your access request was approved" : "Your access request was denied", null, $"/spaces/{r.SpaceId}", alsoEmail: false, ct);
    }

    /// <summary>Admin emergency access to a sensitive space: immediate, time-boxed, loudly alerted.</summary>
    public async Task<SpaceAccessGrant> OpenEmergencyAsync(Guid spaceId, Guid adminId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        var p = await spaces.GetPrincipalAsync(adminId, ct);
        if (p is not { IsAdmin: true }) throw new AccessDeniedException("Only administrators can use emergency access.");
        var space = await spaces.GetSpaceAsync(spaceId, ct) ?? throw new NotFoundException("Space not found.");
        if (!space.IsSensitive) throw new InvalidOperationException("Emergency access applies only to sensitive spaces.");

        var now = time.GetUtcNow();
        var g = new SpaceAccessGrant { SpaceId = spaceId, UserId = adminId, Role = SpaceRole.Reader, IsEmergency = true, GrantedAt = now, ExpiresAt = now + options.Value.Auth.EmergencyAccessDuration };
        await data.AddGrantAsync(g, ct);
        await audit.AppendAsync(new(AuditActions.EmergencyAccess, adminId, "space", spaceId.ToString(), spaceId,
            new Dictionary<string, string> { ["reason"] = reason, ["expires"] = g.ExpiresAt!.Value.ToString("O") }), ct);
        await notifier.NotifyAsync(await data.GetOwnerUserIdsAsync(spaceId, ct), NotificationKinds.SecurityAlert,
            $"Emergency administrator access opened on {space.Name}", $"Reason: {reason}. Expires {g.ExpiresAt:u}.", $"/spaces/{spaceId}", alsoEmail: true, ct);
        return g;
    }
}
