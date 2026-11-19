namespace Kb.Web;

/// <summary>When the caller's session was opened (Program.cs, OnValidatePrincipal).</summary>
internal static class SessionAge
{
    public const string Key = "kb.session.start";

    /// <summary>The start written into a session cookie, or null for a cookie that has none.</summary>
    public static DateTimeOffset? Read(Microsoft.AspNetCore.Authentication.AuthenticationProperties properties) =>
        properties.Items.TryGetValue(Key, out var text) && long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>
    /// For the sign-in the app does again for the person who is already signed in on this request (their security stamp changed and
    /// this session is to stay open): the new cookie keeps the start of the session it replaces.
    /// </summary>
    public static Microsoft.AspNetCore.Authentication.AuthenticationProperties Continued(HttpContext http)
    {
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        if (http.Items[Key] is DateTimeOffset start) Write(properties, start);
        return properties;
    }

    public static void Write(Microsoft.AspNetCore.Authentication.AuthenticationProperties properties, DateTimeOffset start) =>
        properties.Items[Key] = start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when the request is made with a session that was opened no longer ago than this. A token has no session.</summary>
    public static bool OpenedWithin(HttpContext? http, TimeSpan age, DateTimeOffset now) => http?.Items[Key] is DateTimeOffset start && now - start <= age;
}

/// <summary>
/// The session cookie is SameSite=Lax, which stops other sites. It does not stop a page on a sibling host of the same domain from sending
/// a write with the visitor's cookie. So a write by a cookie caller is refused when the browser says it did not come from this site's
/// own pages. That is one rule for every route the app serves: its own API, plugin routes, and the compatible surface (whose XSRF
/// header is asked for as well). A caller with an Authorization header carries no cookie risk. The OAuth token and revoke endpoints
/// are left out: they are called by applications from anywhere and act on the client's credentials, never on the session cookie.
/// </summary>
internal static class CrossSiteGuard
{
    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        var r = http.Request;
        var write = !(HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method) || HttpMethods.IsOptions(r.Method));
        if (write && http.User.Identity?.IsAuthenticated == true && r.Headers.Authorization.Count == 0 && FromElsewhere(r)
            && http.GetEndpoint()?.Metadata.GetMetadata<Kb.Web.DataCenter.DcOAuthRoute>() is null)
        {
            const string why = "This request did not come from this site's own pages.";
            await (Kb.Web.DataCenter.DcRest.IsUnderRoot(http) ? Kb.Web.DataCenter.DcRest.Fail(403, why) : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: why)).ExecuteAsync(http);
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
