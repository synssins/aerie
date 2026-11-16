using System.Globalization;
using System.Net;
using System.Text;
using Kb.Web.Actions;

namespace Kb.Web.DataCenter;

/// <summary>Reading and answering the token and revoke endpoints (identity.md, The token endpoint; RFC 6749 sections 2.3.1, 5).</summary>
public static class OAuthHttp
{
    /// <summary>
    /// The parameters, from the query string and the form body with the body winning name by name, and the client from an
    /// <c>Authorization: Basic</c> header (id and secret form-urlencoded, RFC 6749 section 2.3.1). <c>BadBasic</c> when that header cannot be read.
    /// </summary>
    public static async Task<(Dictionary<string, string[]> Parameters, (string Id, string Secret)? Basic, bool BadBasic, bool BadBody)> ReadAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var p = http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v ?? "").ToArray(), StringComparer.Ordinal);
        var badBody = false;
        if (http.Request.HasFormContentType)
        {
            try
            {
                foreach (var (key, values) in await http.Request.ReadFormAsync(http.RequestAborted)) p[key] = [.. values.Select(v => v ?? "")];
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { badBody = true; }
        }
        (string, string)? basic = null;
        var bad = false;
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = text.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) bad = true;
                else basic = (WebUtility.UrlDecode(text[..colon]), WebUtility.UrlDecode(text[(colon + 1)..]));
            }
            catch (FormatException) { bad = true; }
        }
        return (p, basic, bad, badBody);
    }

    /// <summary>The answer: JSON or empty, never cached, with <c>WWW-Authenticate: Basic</c> when a Basic client was refused, and Retry-After past a limit.</summary>
    public static IResult Write(HttpContext http, OAuthEndpointResult r)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(r);
        var h = http.Response.Headers;
        h.CacheControl = "no-store";
        h.Pragma = "no-cache";
        if (r.BasicChallenge) h.WWWAuthenticate = "Basic realm=\"oauth\"";
        if (r.RetryAfter is { } wait) h.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
        return r.Body is null ? Results.StatusCode(r.Status) : Results.Json(r.Body, statusCode: r.Status);
    }
}
