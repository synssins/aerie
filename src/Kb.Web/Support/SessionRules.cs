namespace Kb.Web;

/// <summary>When the caller's session was opened (Program.cs, OnValidatePrincipal).</summary>
internal static class SessionAge
{
    public const string Key = "kb.session.start";

    /// <summary>True when the request is made with a session that was opened no longer ago than this. A token has no session.</summary>
    public static bool OpenedWithin(HttpContext? http, TimeSpan age, DateTimeOffset now) => http?.Items[Key] is DateTimeOffset start && now - start <= age;
}

/// <summary>
/// The session cookie is SameSite=Lax, which stops other sites. It does not stop a page on a sibling host of the same domain from sending
/// a write with the visitor's cookie. So a write to the API by a cookie caller is refused when the browser says it did not come from
/// this site's own pages. A caller with an Authorization header carries no cookie risk, and the compatible surface has its own header rule.
/// </summary>
internal static class CrossSiteGuard
{
    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        var r = http.Request;
        var write = !(HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method) || HttpMethods.IsOptions(r.Method));
        if (write && r.Path.StartsWithSegments("/api") && http.User.Identity?.IsAuthenticated == true && r.Headers.Authorization.Count == 0 && FromElsewhere(r))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This request did not come from this site's own pages.").ExecuteAsync(http);
            return;
        }
        await next(http);
    }

    /// <summary>
    /// What the browser says about where the request was made. Sec-Fetch-Site is believed first: the browser sets it and a page cannot.
    /// Without it (an old browser), an Origin header must name this host; its scheme is not compared, because a proxy may end https.
    /// A request with neither header is not from a browser's page and is let through.
    /// </summary>
    private static bool FromElsewhere(HttpRequest r)
    {
        if (r.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } site) return site is not ("same-origin" or "none");
        if (r.Headers.Origin.ToString() is { Length: > 0 } origin)
            return !Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, r.Host.Value, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
