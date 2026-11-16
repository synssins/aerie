using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>
/// The scope each operation of the compatible surface needs (contracts/behaviour/identity.md, Scopes), read from the pinned contract that is
/// built into the app: <c>GET</c> and <c>HEAD</c> need READ, other methods WRITE, and an operation marked <c>x-aerie-scope</c> what the mark
/// says. A route that is not in the contract has no scope and is refused (default deny); the attachment download follows the method rule.
/// </summary>
public sealed partial class DcScopes
{
    public const string Read = "READ", Write = "WRITE", Admin = "ADMIN";
    private readonly Dictionary<string, string> _table = new(StringComparer.Ordinal);
    private readonly string _context;

    public DcScopes(IOptions<Kb.Core.Config.KbOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _context = options.Value.DataCenter.ContextPath;
        using var stream = typeof(DcScopes).Assembly.GetManifestResourceStream("Kb.Contracts.datacenter-openapi.yaml")
            ?? throw new InvalidOperationException("The Data Center compatible contract is not built into the app.");
        using var reader = new StreamReader(stream);
        string? path = null, op = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("components:", StringComparison.Ordinal)) break;
            if (PathLine().Match(line) is { Success: true } p) (path, op) = (p.Groups[1].Value, null);
            else if (line.Length > 0 && line[0] != ' ') (path, op) = (null, null);
            else if (path is not null && MethodLine().Match(line) is { Success: true } m)
            {
                var method = m.Groups[1].Value.ToUpperInvariant();
                op = $"{method} {path}";
                _table[op] = method == "GET" ? Read : Write;
            }
            else if (op is not null && ScopeLine().Match(line) is { Success: true } s) _table[op] = s.Groups[1].Value;
        }
    }

    [GeneratedRegex(@"^  (/(?:rest/(?:api|oauth2)|\.well-known)[^\s:]*):\s*$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|post|put|delete|patch):\s*$")]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"^      x-aerie-scope:\s*([A-Z_]+)\s*$")]
    private static partial Regex ScopeLine();

    [GeneratedRegex(@"\{([a-zA-Z]+)(?::[^}]+)?\}")]
    private static partial Regex Parameter();

    /// <summary>True when the contract has this operation ("METHOD /path").</summary>
    public bool Knows(string operation) => _table.ContainsKey(operation);

    /// <summary>The scope this method on this endpoint needs, or null when it has none (and is refused).</summary>
    public string? Required(string method, Endpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (endpoint is not RouteEndpoint route) return null;
        var get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (route.Metadata.GetMetadata<DcDownloadRoute>() is not null) return get ? Read : null;
        var raw = route.RoutePattern.RawText ?? "";
        if (_context.Length > 0 && raw.StartsWith(_context, StringComparison.Ordinal)) raw = raw[_context.Length..];
        var path = Parameter().Replace(raw, "{$1}");
        return _table.GetValueOrDefault($"{(get ? "GET" : method.ToUpperInvariant())} {path}");
    }

    /// <summary>
    /// The scopes the caller holds, already expanded by the implications in identity.md: null for every scope (a password or the session
    /// cookie). A personal access token with only Read scopes holds READ; any Write scope holds everything, and its role decides.
    /// </summary>
    public static IReadOnlySet<string>? Granted(HttpContext http)
    {
        if (ApiTokenAuth.Current(http) is { } token)
            return token.Scopes().Any(s => s.Access == TokenAccess.Write) ? null : new HashSet<string>(StringComparer.Ordinal) { Read };
        // An OAuth token holds what its scopes imply (identity.md, Scopes): WRITE implies READ, ADMIN implies WRITE.
        if (ApiTokenAuth.OAuthCurrent(http) is { } oauth) return OAuthScopes.Expand(oauth.Token.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return null;
    }

    /// <summary>
    /// The check between credential resolution and the operation's own permission check. A refusal is 403; for a Bearer token it carries
    /// <c>WWW-Authenticate: Bearer error="insufficient_scope"</c> (RFC 6750 section 3) and is audited as token.use.denied.
    /// </summary>
    internal static async Task<bool> AllowedAsync(HttpContext http)
    {
        var required = http.RequestServices.GetRequiredService<DcScopes>().Required(http.Request.Method, http.GetEndpoint());
        var granted = Granted(http);
        if (required is not null && (granted is null || granted.Contains(required))) return true;
        if (required is not null && ApiTokenAuth.OAuthCurrent(http) is { } oauth)
        {
            if (ApiTokenAuth.HasBearer(http.Request)) http.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{required}\"";
            await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.TokenUseDenied, oauth.Grant.UserId, "oauth.token", oauth.Token.Id.ToString(),
                Data: new Dictionary<string, string> { ["reason"] = "scope", ["needed"] = required, ["prefix"] = oauth.Token.Prefix, ["clientId"] = oauth.Client.ClientId, ["ip"] = BasicThrottle.AddressOf(http) }), http.RequestAborted);
        }
        if (required is not null && ApiTokenAuth.Current(http) is { } token)
        {
            if (ApiTokenAuth.HasBearer(http.Request)) http.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{required}\"";
            await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.TokenUseDenied, token.OwnerUserId, "token", token.Id.ToString(),
                Data: new Dictionary<string, string> { ["reason"] = "scope", ["needed"] = required, ["prefix"] = token.Prefix, ["ip"] = BasicThrottle.AddressOf(http) }), http.RequestAborted);
        }
        await DcRest.Fail(403, required is null ? "This operation is not open on this surface." : $"This token's scope does not cover {required}.").ExecuteAsync(http);
        return false;
    }
}
