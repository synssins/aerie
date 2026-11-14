using Kb.Core.Audit;
using Kb.Core.Spaces;
using Kb.Data;
using System.Text.RegularExpressions;

namespace Kb.Web;

/// <summary>What the space handlers share. No HTTP here: a refusal is a <see cref="Problem"/>.</summary>
internal static partial class SpaceSupport
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    internal static partial Regex SpaceKeyPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    internal static partial Regex NonKeyChars();

    /// <summary>404 for one with no role in the space, so it is not disclosed; 403 for one who has a role but does not manage the space.</summary>
    internal static async Task<Problem?> ManagesAsync(IAccess access, Actor actor, Guid spaceId, CancellationToken ct)
    {
        if (await access.RoleAsync(actor, spaceId, ct) is null) return Problem.NotFound;
        return await access.CanAsync(actor, spaceId, SpaceAction.ManageRolesSettingsSensitive, ct) ? null : Problem.Forbidden;
    }

    /// <summary>True when the update changes the name, the sensitive flag or the visibility: only an Admin changes those.</summary>
    internal static bool NeedsAdmin(Space s, UpdateSpace req) =>
        IsRename(s, req) || (req.IsSensitive is { } a && a != s.IsSensitive) || (req.Visibility is { } b && b != s.Visibility);

    internal static readonly Problem SensitiveStaysPrivate = Problem.Conflict("A sensitive space stays private.");

    private static bool IsRename(Space s, UpdateSpace req) => req.Name is { } n && !string.IsNullOrWhiteSpace(n) && n.Trim() != s.Name;

    /// <summary>
    /// Applies a space update and notes its audit entry. Owners change the description and space settings; who may change the name,
    /// the sensitive flag and the visibility is the caller's to check first (<see cref="NeedsAdmin"/>). Returns a problem, or null on success.
    /// </summary>
    internal static async Task<Problem?> UpdateSpaceAsync(Space s, UpdateSpace req, KbDbContext db, ActionContext ctx)
    {
        if (req.AgingDays is < 1 or > 3650 || req.RecycleDays is < 1 or > 3650) return Problem.Invalid("days", "1-3650.");
        if (req.Visibility is { } vis && !Enum.IsDefined(vis)) return Problem.Invalid("visibility", "Private, SignedIn or Anyone.");
        if ((req.IsSensitive ?? s.IsSensitive) && (req.Visibility ?? s.Visibility) != SpaceVisibility.Private) return SensitiveStaysPrivate;

        // Protection taken from a sensitive space: the flag cleared (every Admin is its Owner from then on).
        var cleared = s.IsSensitive && req.IsSensitive == false;
        if (IsRename(s, req)) s.Name = req.Name!.Trim();
        if (req.Description is { } d) s.Description = string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        if (req.CommentsEnabled is { } c) s.CommentsEnabled = c;
        if (req.AgingDays is { } ad) s.AgingDays = ad;
        if (req.AgingAudience is { } aa) s.AgingAudience = aa;
        if (req.RecycleDays is { } rd) s.RecycleDays = rd;
        if (req.Visibility is { } p) s.Visibility = p;
        var sensitiveChanged = req.IsSensitive is { } sens && sens != s.IsSensitive;
        if (req.IsSensitive is { } v) s.IsSensitive = v;
        await db.SaveChangesAsync();
        ctx.Audit(sensitiveChanged ? AuditActions.SpaceSensitiveSet : AuditActions.SpaceUpdate, "space", s.Id.ToString(), s.Id,
            new Dictionary<string, string> { ["isSensitive"] = s.IsSensitive.ToString(), ["visibility"] = s.Visibility.ToString() });
        // The Owners hear of it whoever did it and by whichever address, as they do of emergency access.
        if (cleared)
            await ctx.Services.GetRequiredService<Kb.Core.Notifications.INotifier>().NotifyAsync(
                await ctx.Services.GetRequiredService<Kb.Core.Access.IAccessData>().GetOwnerUserIdsAsync(s.Id, CancellationToken.None), Kb.Core.Notifications.NotificationKinds.SecurityAlert,
                $"{s.Name} is no longer a sensitive space",
                "An administrator changed it.", $"/spaces/{s.Id}", alsoEmail: true, CancellationToken.None);
        return null;
    }

    internal static SpaceDto ToDto(Space s, SpaceRole? role) => new(s.Id, s.Key, s.Name, s.Description, s.IsSensitive, s.Visibility.ToString(), role?.ToString(),
        s.CommentsEnabled, s.AgingDays, s.AgingAudience.ToString(), s.RecycleDays, s.CreatedAt);
}
