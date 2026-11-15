using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kb.Web.Actions;

namespace Kb.Web.DataCenter;

/// <summary>The space and long-task objects of the surface (contracts/behaviour/spaces.md, common.md).</summary>
public static class DcSpaceJson
{
    /// <summary>A global key is returned in upper case; a personal key (~name) as stored (spaces.md, Space keys).</summary>
    public static string Key(DcSpaceView s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Personal ? s.Key : s.Key.ToUpperInvariant();
    }

    public static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static JsonObject Of(DcSpaceView s, DcLinks links, Expand expand, string iconPath, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(expand);
        var key = Key(s);
        var o = new JsonObject
        {
            ["id"] = s.DcId,
            ["key"] = key,
            ["name"] = s.Name,
            ["type"] = s.Personal ? "personal" : "global",
            ["status"] = s.Archived ? "archived" : "current",
        };
        var expandable = new JsonObject();
        void Field(string name, Func<JsonNode?> value)
        {
            if (expand.Has(name)) o[name] = value();
            else expandable[name] = "";
        }
        Field("description", () => Description(s.Description ?? "", expand.Inside("description")));
        Field("icon", () => new JsonObject { ["path"] = iconPath, ["width"] = 48, ["height"] = 48, ["isDefault"] = true });
        // Aerie spaces carry no labels of their own.
        Field("metadata", () => new JsonObject { ["labels"] = new JsonObject { ["results"] = new JsonArray(), ["start"] = 0, ["limit"] = 200, ["size"] = 0 } });
        if (s.Creator is { } creator) Field("creator", () => DcUserJson.Of(creator, links, expand.Inside("creator")));
        Field("creationDate", () => Date(s.CreatedAt));
        // Listed so clients see the names; aerie has neither in T1 and never expands them.
        expandable["retentionPolicy"] = "";
        expandable["permissions"] = "";
        var self = links.Abs("/rest/api/space/" + Uri.EscapeDataString(key));
        o["_links"] = alone
            ? new JsonObject { ["self"] = self, ["webui"] = "/s/" + s.Id, ["collection"] = "/rest/api/space" }
            : new JsonObject { ["self"] = self, ["webui"] = "/s/" + s.Id };
        o["_expandable"] = expandable;
        return o;
    }

    /// <summary>plain is the text as written; view is the same text as HTML. Without either named inside, both.</summary>
    private static JsonObject Description(string text, Expand inside)
    {
        var (plain, view) = (inside.Has("plain"), inside.Has("view"));
        if (!plain && !view) plain = view = true;
        var d = new JsonObject();
        if (plain) d["plain"] = new JsonObject { ["value"] = text, ["representation"] = "plain" };
        if (view) d["view"] = new JsonObject { ["value"] = WebUtility.HtmlEncode(text), ["representation"] = "view" };
        return d;
    }

    public static JsonObject Task(DcLongTaskView t, DcLinks links, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(links);
        var done = t.FinishedAt is not null;
        return new JsonObject
        {
            ["id"] = t.Id.ToString("N"),
            ["name"] = DcRest.Message("aerie.longtask." + t.Name, t.Name == "space.delete" ? $"Deleting space {t.Subject}" : $"{t.Name} {t.Subject}".Trim()),
            ["elapsedTime"] = (long)((t.FinishedAt ?? now) - t.StartedAt).TotalMilliseconds,
            ["percentageComplete"] = done ? 100 : 0,
            ["successful"] = done && t.Successful,
            ["messages"] = new JsonArray(),
            ["_links"] = new JsonObject { ["self"] = links.Abs("/rest/api/longtask/" + t.Id.ToString("N")) },
        };
    }

    public static JsonObject Submission(DcLongTaskView t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new JsonObject { ["id"] = t.Id.ToString("N"), ["links"] = new JsonObject { ["status"] = "/rest/api/longtask/" + t.Id.ToString("N") } };
    }
}
