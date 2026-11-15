using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>Marks a route of the Data Center compatible surface (contracts/datacenter/openapi.yaml).</summary>
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

    /// <summary>True for a request under the surface's root, wherever the context prefix puts it.</summary>
    public static bool IsUnderRoot(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Path.StartsWithSegments(http.RequestServices.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter.ContextPath + Root);
    }

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

    /// <summary>
    /// Runs for every request under the root, after authentication. Unknown paths answer 404, refused Basic credentials 401 (never served as
    /// nobody), a multipart or form write without the XSRF header 403, and any error without a body gets the RestError body.
    /// </summary>
    public static async Task GuardAsync(HttpContext http, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        if (!IsUnderRoot(http))
        {
            await next(http);
            return;
        }
        var res = http.Response;
        if (HasBasic(http.Request) && http.User.Identity?.IsAuthenticated != true)
        {
            res.Headers.WWWAuthenticate = "Basic realm=\"aerie\"";
            await Fail(401, "The user name or password is not valid.").ExecuteAsync(http);
            return;
        }
        // A route of another kind matched (the UI's fallback page), or none did: this path is not part of the surface.
        // (A method the path does not take matched the framework's 405 endpoint, which is not a route: it answers 405 below.)
        var endpoint = http.GetEndpoint();
        if (endpoint is null || endpoint is RouteEndpoint && endpoint.Metadata.GetMetadata<DcRoute>() is null)
        {
            await Fail(404, "No such resource.").ExecuteAsync(http);
            return;
        }
        if (!XsrfOk(http.Request))
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

    /// <summary>A write that carries a form or multipart body needs X-Atlassian-Token: no-check (or nocheck). JSON writes do not.</summary>
    public static bool XsrfOk(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsDelete(request.Method))) return true;
        var type = request.ContentType ?? "";
        if (!type.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) return true;
        return request.Headers["X-Atlassian-Token"].Any(v => string.Equals(v, "no-check", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "nocheck", StringComparison.OrdinalIgnoreCase));
    }
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

    public bool Has(string name) => _paths.Any(p => p == name || p.StartsWith(name + ".", StringComparison.Ordinal));

    /// <summary>What is asked for inside one field: "body.storage" in this is "storage" in the result for "body".</summary>
    public Expand Inside(string name) => new([.. _paths.Where(p => p.StartsWith(name + ".", StringComparison.Ordinal)).Select(p => p[(name.Length + 1)..])]);
}
