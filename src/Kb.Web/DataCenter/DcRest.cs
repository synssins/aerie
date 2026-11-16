using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>Marks the download of attachment bytes: not an operation of the contract, a link its attachments carry.</summary>
public sealed class DcDownloadRoute
{
    public static readonly DcDownloadRoute Marker = new();
}

/// <summary>Marks a route of the Data Center compatible surface (contracts/datacenter/openapi.yaml).</summary>
/// <summary>Marks the OAuth 2.0 endpoints under /rest/oauth2 (identity.md, OAuth 2.0).</summary>
public sealed class DcOAuthRoute
{
    public static readonly DcOAuthRoute Marker = new();
}

public sealed class DcRoute
{
    public static readonly DcRoute Marker = new();
}

/// <summary>
/// The rules every operation of the Data Center compatible surface shares (contracts/behaviour/common.md): the error body, paging,
/// expansion and links. Its routes are an adapter: they bind the request, send aerie's own actions and shape the answer.
/// </summary>
public static class DcRest
{
    public const string Root = "/rest/api";
    /// <summary>Where the bytes of an attachment are fetched (its _links.download), with the same credentials as the surface.</summary>
    public const string DownloadRoot = "/download/attachments";
    /// <summary>Reserved for the OAuth 2.0 surface (not built yet): nothing under it may fall through to the web app either.</summary>
    public const string OAuthRoot = "/rest/oauth2";

    /// <summary>True for a request under the surface's root, wherever the context prefix puts it.</summary>
    public static bool IsUnderRoot(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var context = http.RequestServices.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter.ContextPath;
        return http.Request.Path.StartsWithSegments(context + Root) || http.Request.Path.StartsWithSegments(context + DownloadRoot);
    }

    private static bool IsUnderOAuth(HttpContext http) =>
        http.Request.Path.StartsWithSegments(http.RequestServices.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter.ContextPath + OAuthRoot);

    /// <summary>True when the request matched a route of the surface or its attachment download.</summary>
    public static bool IsSurfaceRoute(HttpContext? http) =>
        http?.GetEndpoint()?.Metadata is { } m && (m.GetMetadata<DcRoute>() is not null || m.GetMetadata<DcDownloadRoute>() is not null);

    public static bool HasBasic(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

    public static string Reason(int status) => ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } r ? r : "Error";

    /// <summary>The RestError body.</summary>
    public static JsonObject Error(int status, string? message) => new()
    {
        ["statusCode"] = status,
        ["message"] = string.IsNullOrWhiteSpace(message) ? Reason(status) : message,
        ["reason"] = Reason(status),
    };

    /// <summary>
    /// A message object (common.md, Message objects; Q-0019): a stable key, the full text with every value filled in, and args always empty.
    /// Every message the surface answers with is made here.
    /// </summary>
    public static JsonObject Message(string key, string translation) => new() { ["key"] = key, ["args"] = new JsonArray(), ["translation"] = translation };

    public static IResult Fail(int status, string? message = null) => Results.Json(Error(status, message), statusCode: status);

    /// <summary>
    /// An action's problem as a RestError. A plugin's refusal (422) and a lock (423) are not statuses of this surface: they answer 403,
    /// "the caller may see the target but not do this to it".
    /// </summary>
    public static IResult Fail(Problem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var message = problem.Message ?? (problem.Fields is { Count: > 0 } f ? string.Join(" ", f.SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}"))) : null);
        return Fail(problem.Status is 422 or 423 ? 403 : problem.Status, message);
    }

    public static async Task<IResult> Rest<T>(this Task<Outcome<T>> outcome, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(ok);
        var o = await outcome;
        return o.Problem is { } p ? Fail(p) : ok(o.Value!);
    }

    public static IResult Json(JsonNode node, int status = 200) => Results.Json(node, statusCode: status);

    /// <summary>The JSON object a write carries; null (answer 400) when the body is missing, not JSON, or not an object.</summary>
    public static async Task<JsonObject?> BodyAsync(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { return await JsonNode.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static IResult BadBody => Fail(400, "The body must be a JSON object.");

    /// <summary>A string at a dotted path in a body (description.plain.value); null when absent or not a string.</summary>
    public static string? Text(JsonObject? body, string path)
    {
        JsonNode? node = body;
        foreach (var part in path.Split('.')) node = (node as JsonObject)?[part];
        return node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>A boolean at a top-level field of a body; null when absent or not a boolean.</summary>
    public static bool? Flag(JsonObject? body, string name) => body?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Every value of a repeatable query parameter, plus the comma-separated values of its list form.</summary>
    public static List<string> Values(HttpRequest request, string repeated, string? commaList = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var all = request.Query[repeated].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (commaList is not null)
            all.AddRange(request.Query[commaList].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        return all;
    }

    /// <summary>
    /// Runs for every request under the root, after authentication. Unknown paths answer 404, refused Basic credentials 401 (never served as
    /// nobody), a multipart or form write without the XSRF header 403, and any error without a body gets the RestError body.
    /// </summary>
    public static async Task GuardAsync(HttpContext http, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        // Under /rest/oauth2 only the OAuth endpoints answer; any other path is 404.
        if (IsUnderOAuth(http) && http.GetEndpoint()?.Metadata.GetMetadata<DcOAuthRoute>() is null)
        {
            await Fail(404, "No such resource.").ExecuteAsync(http);
            return;
        }
        if (!IsUnderRoot(http))
        {
            await next(http);
            return;
        }
        var res = http.Response;
        // An Authorization header aerie cannot use is 401, never anonymous (identity.md, The callers aerie must tell apart).
        if (http.Request.Headers.Authorization.Count > 0 && !HasBasic(http.Request) && !ApiTokenAuth.HasBearer(http.Request))
        {
            await Fail(401, "The Authorization header is not one aerie accepts.").ExecuteAsync(http);
            return;
        }
        if (HasBasic(http.Request) && http.User.Identity?.IsAuthenticated != true && http.Items[BasicAuthHandler.LockedItem] is int locked)
        {
            res.Headers.RetryAfter = locked.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Fail(429, "This account is locked after too many failed sign-ins; try again later.").ExecuteAsync(http);
            return;
        }
        if (HasBasic(http.Request) && http.User.Identity?.IsAuthenticated != true)
        {
            // No Basic challenge: a browser on aerie's own pages never shows a password dialog (identity.md, Failed Basic attempts).
            await Fail(401, "The user name or password is not valid.").ExecuteAsync(http);
            return;
        }
        // No route of the surface matched. A path of the surface asked with a method it does not take is 405; anything else
        // (no route, the UI's fallback page, or the framework's 405 for the UI's page) is not part of the surface: 404.
        if (http.GetEndpoint()?.Metadata.GetMetadata<DcRoute>() is null && http.GetEndpoint()?.Metadata.GetMetadata<DcDownloadRoute>() is null)
        {
            await (HasRouteFor(http) ? Fail(405, "This resource does not take " + http.Request.Method + ".") : Fail(404, "No such resource.")).ExecuteAsync(http);
            return;
        }
        // The caller's scope must cover the operation before the user's own permissions are asked (identity.md, Scopes).
        if (!await DcScopes.AllowedAsync(http)) return;
        if (!XsrfOk(http.Request) || (IsCookieCaller(http) && !IsSafe(http.Request.Method) && !HasXsrfHeader(http.Request)))
        {
            await Fail(403, "This request needs the X-Atlassian-Token header.").ExecuteAsync(http);
            return;
        }
        try { await next(http); }
        catch (Exception e) when (!res.HasStarted && !(e is OperationCanceledException && http.RequestAborted.IsCancellationRequested))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kb.DataCenter").LogError(e, "A Data Center compatible request failed.");
            res.Clear();
            await Fail(500, "The server could not complete the request.").ExecuteAsync(http);
            return;
        }
        if (!res.HasStarted && res.StatusCode >= 400 && res.ContentLength is null && string.IsNullOrEmpty(res.ContentType))
            await Fail(res.StatusCode).ExecuteAsync(http);
    }

    private static bool HasRouteFor(HttpContext http)
    {
        var path = http.Request.Path;
        return http.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<DcRoute>() is not null)
            .Any(e => new Microsoft.AspNetCore.Routing.Template.TemplateMatcher(Microsoft.AspNetCore.Routing.Template.TemplateParser.Parse(e.RoutePattern.RawText!), new RouteValueDictionary())
                .TryMatch(path, new RouteValueDictionary()));
    }

    /// <summary>A write that carries a form or multipart body needs X-Atlassian-Token: no-check (or nocheck). JSON writes do not.</summary>
    public static bool XsrfOk(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsDelete(request.Method))) return true;
        var type = request.ContentType ?? "";
        if (!type.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) return true;
        return HasXsrfHeader(request);
    }

    private static bool HasXsrfHeader(HttpRequest request) =>
        request.Headers["X-Atlassian-Token"].Any(v => string.Equals(v, "no-check", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "nocheck", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>
    /// Signed in by aerie's session cookie, which the browser sends on its own: every write then needs the XSRF header, whatever the body
    /// (identity.md, Sessions and aerie's own API; stricter than the form-only rule for other callers).
    /// </summary>
    private static bool IsCookieCaller(HttpContext http) => http.User.Identity?.IsAuthenticated == true && http.Request.Headers.Authorization.Count == 0;
}

/// <summary>Where the site is, for the links in an answer.</summary>
public sealed class DcLinks(HttpContext http, DataCenterOptions options)
{
    public HttpContext Http { get; } = http;
    public string Context { get; } = options.ContextPath;
    public string Base { get; } = options.BaseUrl.Length > 0 ? options.BaseUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}{options.ContextPath}";
    public int MaxLimit { get; } = options.MaxLimit;

    public static DcLinks Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new(http, http.RequestServices.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter);
    }

    /// <summary>The absolute URL of a REST path such as "/rest/api/user?key=...".</summary>
    public string Abs(string restPath) => Base + restPath;

    /// <summary>This request's path without the context prefix: what next and prev links are made of.</summary>
    public string RequestPath => (Http.Request.Path.Value ?? "")[Context.Length..];

    /// <summary>Adds base and context to a top-level object's links.</summary>
    public JsonObject Top(JsonObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o["_links"] is not JsonObject links) o["_links"] = links = [];
        links["base"] = Base;
        links["context"] = Context;
        return o;
    }

    /// <summary>One page of an offset-paged list, with self, next and prev links.</summary>
    public JsonObject Page(Paging paging, IEnumerable<JsonNode> items, bool more)
    {
        var results = new JsonArray([.. items]);
        var links = new JsonObject { ["self"] = Abs(RequestPath + Http.Request.QueryString) };
        if (more) links["next"] = WithStart(paging.Start + paging.Limit);
        if (paging.Start > 0) links["prev"] = WithStart(Math.Max(0, paging.Start - paging.Limit));
        return Top(new JsonObject { ["results"] = results, ["start"] = paging.Start, ["limit"] = paging.Limit, ["size"] = results.Count, ["_links"] = links });
    }

    /// <summary>
    /// A list built inside another object, answered on its own: its self, next and prev links are made again from this request, so they keep
    /// its query (expand and the rest), and it gets base and context. Whether a next page exists is what the list said.
    /// </summary>
    public JsonObject Repage(JsonObject list, Paging paging)
    {
        ArgumentNullException.ThrowIfNull(list);
        var more = list["_links"]?["next"] is not null;
        var links = new JsonObject { ["self"] = Abs(RequestPath + Http.Request.QueryString) };
        if (more) links["next"] = WithStart(paging.Start + paging.Limit);
        if (paging.Start > 0) links["prev"] = WithStart(Math.Max(0, paging.Start - paging.Limit));
        list["_links"] = links;
        return Top(list);
    }

    private string WithStart(int start)
    {
        var query = Http.Request.Query.Where(q => !q.Key.Equals("start", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v))).ToList();
        query.Add(new("start", start.ToString(CultureInfo.InvariantCulture)));
        return QueryHelpers.AddQueryString(RequestPath, query);
    }
}

/// <summary>Offset paging: start and limit, the limit cut to the server's ceiling.</summary>
public readonly record struct Paging(int Start, int Limit)
{
    /// <summary>Null when start or limit is not valid; the caller answers 400.</summary>
    public static Paging? Read(HttpRequest request, int defaultLimit, int maxLimit)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = 0;
        var limit = defaultLimit;
        if (request.Query.TryGetValue("start", out var s) && (!int.TryParse(s.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out start) || start < 0)) return null;
        if (request.Query.TryGetValue("limit", out var l) && (!int.TryParse(l.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out limit) || limit < 1)) return null;
        return new Paging(start, Math.Min(limit, maxLimit));
    }

    public static IResult Invalid => DcRest.Fail(400, "start must be 0 or more and limit 1 or more.");
}

/// <summary>The expand parameter: dotted field paths. A name nobody knows is ignored.</summary>
public sealed class Expand
{
    private readonly string[] _paths;

    private Expand(string[] paths) => _paths = paths;

    public static readonly Expand None = new([]);

    /// <summary>The paths asked for, or the defaults when the parameter is absent. Sending it replaces the defaults.</summary>
    public static Expand Of(HttpRequest request, params string[] defaults)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Query.TryGetValue("expand", out var v)
            ? new([.. v.SelectMany(x => (x ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))])
            : new(defaults);
    }

    /// <summary>The expand parameter as the route passed it on to an action.</summary>
    public static Expand Parse(string? raw) => new([.. (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    /// <summary>What a route passes on: the parameter as sent, or the defaults when it is absent.</summary>
    public static string Raw(HttpRequest request, string defaults = "")
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Query.TryGetValue("expand", out var v) ? string.Join(',', v.ToArray()) : defaults;
    }

    public bool Has(string name) => _paths.Any(p => p == name || p.StartsWith(name + ".", StringComparison.Ordinal));

    /// <summary>What is asked for inside one field: "body.storage" in this is "storage" in the result for "body".</summary>
    public Expand Inside(string name) => new([.. _paths.Where(p => p.StartsWith(name + ".", StringComparison.Ordinal)).Select(p => p[(name.Length + 1)..])]);
}
