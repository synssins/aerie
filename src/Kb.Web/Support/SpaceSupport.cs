using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Kb.Web;

public static partial class EndpointMappings
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex SpaceKeyPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonKeyChars();

    /// <summary>
    /// Applies a space update. Owners change the description and space settings; only admins change the name,
    /// the sensitive flag and the public flag. Returns an error result, or null on success.
    /// </summary>
    private static async Task<IResult?> UpdateSpaceAsync(Space s, UpdateSpaceRequest req, bool isAdmin, Guid me, KbDbContext db, IAuditLog audit)
    {
        var nameChange = req.Name is { } n && !string.IsNullOrWhiteSpace(n) && n.Trim() != s.Name;
        var adminOnly = nameChange || (req.IsSensitive is { } a && a != s.IsSensitive) || (req.IsPublic is { } b && b != s.IsPublic);
        if (adminOnly && !isAdmin) return Results.StatusCode(403);
        if (req.AgingDays is < 1 or > 3650 || req.RecycleDays is < 1 or > 3650)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["1-3650."] });

        if (nameChange) s.Name = req.Name!.Trim();
        if (req.Description is { } d) s.Description = string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        if (req.CommentsEnabled is { } c) s.CommentsEnabled = c;
        if (req.AgingDays is { } ad) s.AgingDays = ad;
        if (req.AgingAudience is { } aa) s.AgingAudience = aa;
        if (req.RecycleDays is { } rd) s.RecycleDays = rd;
        if (req.IsPublic is { } p) s.IsPublic = p;
        var sensitiveChanged = req.IsSensitive is { } sens && sens != s.IsSensitive;
        if (req.IsSensitive is { } v) s.IsSensitive = v;
        await db.SaveChangesAsync();
        await audit.AppendAsync(new AuditEvent(sensitiveChanged ? AuditActions.SpaceSensitiveSet : AuditActions.SpaceUpdate, me, "space", s.Id.ToString(), s.Id,
            new Dictionary<string, string> { ["isSensitive"] = s.IsSensitive.ToString(), ["isPublic"] = s.IsPublic.ToString() }));
        return null;
    }

    private static async Task<bool> IsAdminAsync(HttpContext http, KbDbContext db)
    {
        var id = http.User.UserId();
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.IsAdmin && u.DeactivatedAt == null);
    }

    private static SpaceDto ToDto(Space s, SpaceRole? role) => new(s.Id, s.Key, s.Name, s.Description, s.IsSensitive, s.IsPublic, role?.ToString(),
        s.CommentsEnabled, s.AgingDays, s.AgingAudience.ToString(), s.RecycleDays, s.CreatedAt);
}
