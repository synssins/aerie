using System.Text.Json.Nodes;

namespace Kb.Web.DataCenter;

/// <summary>The user object of the surface, wherever one appears.</summary>
public static class DcUserJson
{
    public const string AvatarPath = "/images/avatar/default.svg";

    private static JsonObject Picture() => new() { ["path"] = AvatarPath, ["width"] = 48, ["height"] = 48, ["isDefault"] = true };

    public static JsonObject Of(DcUser u, DcLinks links, Expand expand)
    {
        ArgumentNullException.ThrowIfNull(u);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(expand);
        var key = u.Id.ToString("N");
        // An account that no longer exists keeps its key, so old versions still name someone (users.md).
        var o = new JsonObject
        {
            ["type"] = u.Deleted ? "unknown" : "known",
            ["username"] = u.Deleted ? key : u.UserName,
            ["userKey"] = key,
            ["displayName"] = u.Deleted ? key : u.DisplayName,
            ["profilePicture"] = Picture(),
        };
        if (expand.Has("status")) o["status"] = u.Deactivated || u.Deleted ? "deactivated" : "current";
        o["_links"] = new JsonObject { ["self"] = links.Abs("/rest/api/user?key=" + key) };
        if (!expand.Has("status")) o["_expandable"] = new JsonObject { ["status"] = "" };
        return o;
    }

    public static JsonObject Anonymous(DcLinks links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return new JsonObject
        {
            ["type"] = "anonymous",
            ["displayName"] = "Anonymous",
            ["profilePicture"] = Picture(),
            ["_links"] = new JsonObject { ["self"] = links.Abs("/rest/api/user/anonymous") },
        };
    }

    /// <summary>The picture every user shows until aerie has profile pictures.</summary>
    public const string AvatarSvg = """<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><rect width="48" height="48" fill="#8a94a6"/><circle cx="24" cy="19" r="9" fill="#fff"/><path d="M8 44c2-9 8-13 16-13s14 4 16 13z" fill="#fff"/></svg>""";
}
