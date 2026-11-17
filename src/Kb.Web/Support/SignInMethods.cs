using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Data;
using Kb.Web.Actions;
using Kb.Web.Pipeline;
using Kb.Web.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kb.Web;

/// <summary>A sign-in method on this server: the plugin that registered it, its id there, the text of its button, and its type.</summary>
public sealed record SignInMethodInfo(string PluginId, string Id, string Name, Type Method)
{
    /// <summary>How the API names it: "{plugin}/{id}".</summary>
    public string Key => $"{PluginId}/{Id}";
}

/// <summary>
/// The sign-in methods of the loaded plugins, and the settings the app keeps for each (client id, client secret, new accounts).
/// A method shows on the sign-in screen once its client id is set.
/// </summary>
public sealed class SignInMethods(IRegistrations registrations, PluginSettingsStore settings)
{
    public const string Root = "/api/v1/auth/methods";

    public IReadOnlyList<SignInMethodInfo> All =>
        [.. registrations.All<SignInMethodReg>().Select(r => new SignInMethodInfo(r.PluginId, r.Value.Id, r.Value.Name, r.Value.Method)).DistinctBy(m => m.Key)];

    public SignInMethodInfo? Find(string pluginId, string id) => All.FirstOrDefault(m => m.PluginId == pluginId && m.Id == id);

    public IReadOnlyList<SignInMethodInfo> Ready => [.. All.Where(m => ClientId(m) is not null)];

    public string? ClientId(SignInMethodInfo m) => settings.Get(m.PluginId, SignInSettings.ClientId(m.Id))?.Trim() is { Length: > 0 } v ? v : null;

    public Task<string?> ClientSecretAsync(SignInMethodInfo m, CancellationToken ct) => settings.GetSecretAsync(m.PluginId, SignInSettings.ClientSecret(m.Id), ct);

    public bool NewAccounts(SignInMethodInfo m) => settings.Get(m.PluginId, SignInSettings.NewAccounts(m.Id)) == "true";

    /// <summary>
    /// The redirect address to register with the provider: this site as the browser reached it, then the method's callback. The same
    /// text is sent in the authorization request and in the token request.
    /// </summary>
    public static string RedirectAddress(HttpRequest request, SignInMethodInfo m)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(m);
        return $"{request.Scheme}://{request.Host}{request.PathBase}{Root}/{m.PluginId}/{m.Id}/callback";
    }

    /// <summary>RFC 7636, section 4.2: BASE64URL(SHA256(ASCII(code_verifier))).</summary>
    public static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Where to go after signing in: a path on this site only ("/spaces/x?y"), never an address elsewhere ("//host", "/\host", "https://...").
    /// Anything else is "/".
    /// </summary>
    public static string LocalPath(string? path) =>
        path is { Length: > 0 and <= 2048 } && path[0] == '/' && (path.Length == 1 || path[1] is not ('/' or '\\'))
        && !path.Any(c => c == '\\' || char.IsControl(c)) && Uri.TryCreate(path, UriKind.Relative, out _) ? path : "/";
}

/// <summary>Where a provider's endpoints are, from its discovery document.</summary>
public sealed record OidcEndpoints(string Authorization, string Token, string Keys);

/// <summary>
/// Discovery documents and signing keys, kept for an hour. Read with the HTTP client of the plugin that registered the method, so the
/// provider's hosts must be in that plugin's outbound hosts. Keys are read again (once a minute at most) when a token is signed with
/// a key that is not known yet: the provider rotated its keys.
/// </summary>
public sealed class OidcCache(IHttpClientFactory http, TimeProvider time)
{
    public const int MaxDocumentBytes = 512 * 1024;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(1), KeysAgain = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, OidcEndpoints Value)> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IList<SecurityKey> Value)> _keys = new(StringComparer.Ordinal);

    public async Task<OidcEndpoints> EndpointsAsync(string pluginId, OidcProvider p, CancellationToken ct)
    {
        var address = p.DiscoveryAddress ?? p.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var key = pluginId + " " + address;
        if (_endpoints.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < Keep) return hit.Value;
        using var doc = await GetJsonAsync(pluginId, address, ct);
        var root = doc.RootElement;
        string Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new OidcException($"discovery has no {name}");
        // OpenID Connect Discovery 1.0, section 4.3: the issuer in the document is exactly the one asked for.
        if (Field("issuer") != p.Issuer) throw new OidcException("discovery names another issuer");
        var found = new OidcEndpoints(Field("authorization_endpoint"), Field("token_endpoint"), Field("jwks_uri"));
        if (!SignInFlow.IsHttps(found.Authorization) || !SignInFlow.IsHttps(found.Token) || !SignInFlow.IsHttps(found.Keys)) throw new OidcException("an endpoint is not https");
        _endpoints[key] = (time.GetUtcNow(), found);
        return found;
    }

    /// <summary>The signing keys; <paramref name="again"/> reads them from the provider unless they were read in the last minute.</summary>
    public async Task<(IList<SecurityKey> Keys, bool Fresh)> KeysAsync(string pluginId, string address, bool again, CancellationToken ct)
    {
        var key = pluginId + " " + address;
        var now = time.GetUtcNow();
        if (_keys.TryGetValue(key, out var hit) && (again ? now - hit.At < KeysAgain : now - hit.At < Keep)) return (hit.Value, false);
        using var doc = await GetJsonAsync(pluginId, address, ct);
        var keys = new JsonWebKeySet(doc.RootElement.GetRawText()).GetSigningKeys();
        _keys[key] = (now, keys);
        return (keys, true);
    }

    public HttpClient Client(string pluginId) => http.CreateClient(PluginHttp.ClientName(pluginId));

    private async Task<JsonDocument> GetJsonAsync(string pluginId, string address, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client(pluginId).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new OidcException($"{address} answered {(int)response.StatusCode}");
        return await ReadJsonAsync(response, ct);
    }

    /// <summary>A JSON answer of at most <see cref="MaxDocumentBytes"/>.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + n > MaxDocumentBytes) throw new OidcException("an answer is too large");
            copy.Write(buffer, 0, n);
        }
        try { return JsonDocument.Parse(copy.ToArray()); }
        catch (JsonException) { throw new OidcException("an answer is not JSON"); }
    }
}

/// <summary>A sign-in that cannot go on. The reason goes to the audit log; it never holds a code or a token.</summary>
public sealed class OidcException(string reason) : Exception(reason);

/// <summary>
/// One sign-in with a sign-in method: the authorization code flow of OpenID Connect Core 1.0 with PKCE (S256), state and nonce, run by
/// the app. What the browser carries between the start and the callback (state, nonce, code verifier, return path, redirect address)
/// is in one encrypted cookie that lasts ten minutes and is read once. The plugin's code runs as the plugin: its provider description,
/// and its check on the validated claims. The plugin sees no code and no token.
/// </summary>
internal sealed class SignInFlow(SignInMethods methods, OidcCache cache, IDataProtectionProvider protection, IHttpContextAccessor http, IServiceScopeFactory scopes,
    IPluginFailures failures, KbDbContext db, UserManager<KbUser> users, SignInManager<KbUser> signIn, CodeSignIn codes, IAuditLog audit, TimeProvider time,
    ILogger<SignInFlow> log)
{
    public const string StateCookie = "kb.signin", MessageCookie = "kb.signin.message";
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(10), PluginTimeLimit = TimeSpan.FromSeconds(10);
    public static readonly IReadOnlyList<string> Algorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384,
         SecurityAlgorithms.RsaSsaPssSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512];
    private const string Generic = "The sign-in did not work. Try again.";

    private sealed record State(string Method, string Value, string Nonce, string Verifier, string ReturnTo, string Redirect);

    private HttpContext Http => http.HttpContext!;
    private string Ip => Http.Connection.RemoteIpAddress?.ToString() ?? "";
    private ITimeLimitedDataProtector Protector => protection.CreateProtector("kb.signin").ToTimeLimitedDataProtector();

    public static bool IsHttps(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>The address of the provider's authorization endpoint for a new sign-in, or "/" with a message when it cannot start.</summary>
    public async Task<string> StartAsync(SignInMethodInfo m, string clientId, string? returnTo, CancellationToken ct)
    {
        try
        {
            var provider = await ProviderAsync(m, ct);
            var endpoints = await cache.EndpointsAsync(m.PluginId, provider, ct);
            var state = new State(m.Key, Random(), Random(), Random(), SignInMethods.LocalPath(returnTo), SignInMethods.RedirectAddress(Http.Request, m));
            Http.Response.Cookies.Append(StateCookie, Protector.Protect(JsonSerializer.Serialize(state), Lasts), CookieOptions(Lasts));
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInMethodStart, null, "signInMethod", m.Key, Data: new Dictionary<string, string> { ["method"] = m.Key, ["ip"] = Ip }), ct);
            return QueryHelpers.AddQueryString(endpoints.Authorization, new Dictionary<string, string?>
            {
                ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = state.Redirect, ["scope"] = string.Join(' ', provider.Scopes),
                ["state"] = state.Value, ["nonce"] = state.Nonce, ["code_challenge"] = SignInMethods.Challenge(state.Verifier), ["code_challenge_method"] = "S256",
            });
        }
        catch (Exception e) when (e is OidcException or HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return await RefuseAsync(m, "could not start: " + e.Message, "This sign-in method is not working right now. Ask an Administrator.", null, ct);
        }
    }

    /// <summary>The provider sent the browser back: check everything, then sign the person in. The answer is where the browser goes next.</summary>
    public async Task<string> FinishAsync(SignInMethodInfo m, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var state = ReadState();
        Http.Response.Cookies.Delete(StateCookie, CookieOptions(TimeSpan.Zero));
        if (state is null) return await RefuseAsync(m, "no sign-in in progress", Generic, null, ct);
        if (state.Method != m.Key) return await RefuseAsync(m, "started with another method", Generic, null, ct);
        if (!FixedEquals(query.GetValueOrDefault("state"), state.Value)) return await RefuseAsync(m, "wrong state", Generic, null, ct);
        if (query.GetValueOrDefault("error") is { } error)
            return await RefuseAsync(m, "provider answered " + new string([.. error.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(64)]), Generic, null, ct);
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code) return await RefuseAsync(m, "no code", Generic, null, ct);
        if (methods.ClientId(m) is not { } clientId) return await RefuseAsync(m, "no client id", Generic, null, ct);

        SignInClaims claims;
        try
        {
            var provider = await ProviderAsync(m, ct);
            var endpoints = await cache.EndpointsAsync(m.PluginId, provider, ct);
            var idToken = await ExchangeAsync(m, endpoints, clientId, code, state, ct);
            claims = await ValidateAsync(m, provider, endpoints, clientId, idToken, state.Nonce, ct);
        }
        catch (Exception e) when (e is OidcException or HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return await RefuseAsync(m, e.Message, Generic, null, ct);
        }

        var refusal = await CheckAsync(m, claims, ct);
        if (refusal is not null) return await RefuseAsync(m, "refused by the plugin", refusal, claims, ct);

        var (user, problem, message) = await AccountAsync(m, claims, ct);
        if (user is null) return await RefuseAsync(m, problem!, message!, claims, ct);

        if (await codes.NeededAsync(user, ct)) await codes.PendAsync(user); // the sign-in screen asks for the code next
        else
        {
            await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: "external");
            await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = m.Key, ["ip"] = Ip }), ct);
        }
        return state.ReturnTo;
    }

    /// <summary>The message a refused sign-in left for the sign-in screen, read once, and the code step a sign-in is waiting for.</summary>
    public async Task<SignInOutcome> OutcomeAsync()
    {
        string? message = null;
        if (Http.Request.Cookies.TryGetValue(MessageCookie, out var protectedText))
        {
            try { message = Protector.Unprotect(protectedText); }
            catch (CryptographicException) { }
            Http.Response.Cookies.Delete(MessageCookie, CookieOptions(TimeSpan.Zero));
        }
        var pending = await codes.PendingAsync();
        return new SignInOutcome(message, pending is null ? null : pending.TwoFactorEnabled ? "code" : "enroll");
    }

    private State? ReadState()
    {
        if (!Http.Request.Cookies.TryGetValue(StateCookie, out var text)) return null;
        try { return JsonSerializer.Deserialize<State>(Protector.Unprotect(text)); }
        catch (Exception e) when (e is CryptographicException or JsonException) { return null; } // expired, altered, or from another server's keys
    }

    private CookieOptions CookieOptions(TimeSpan lasts) => new()
    {
        // Lax: the provider sends the browser back with a top-level GET, which carries it; a request from another site's page does not.
        HttpOnly = true, Secure = Http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = SignInMethods.Root, MaxAge = lasts, IsEssential = true,
    };

    private async Task<string> RefuseAsync(SignInMethodInfo m, string reason, string message, SignInClaims? claims, CancellationToken ct)
    {
        var data = new Dictionary<string, string> { ["method"] = m.Key, ["reason"] = reason, ["ip"] = Ip };
        if (claims is not null) (data["issuer"], data["subject"]) = (claims.Issuer, claims.Subject);
        await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, null, "signInMethod", m.Key, Data: data), CancellationToken.None);
        Http.Response.Cookies.Append(MessageCookie, Protector.Protect(message, TimeSpan.FromMinutes(5)), CookieOptions(TimeSpan.FromMinutes(5)));
        return "/";
    }

    // ---- the provider

    private async Task<OidcProvider> ProviderAsync(SignInMethodInfo m, CancellationToken ct)
    {
        var (ok, p) = await AsPluginAsync(m, (method, _) => Task.FromResult(method.Provider), ct);
        if (!ok || p is null) throw new OidcException("the plugin gave no provider");
        var problem = !IsHttps(p.Issuer) || p.Issuer.Length > 400 || p.Issuer.Contains('?', StringComparison.Ordinal) || p.Issuer.Contains('#', StringComparison.Ordinal) ? "the issuer is not an https address"
            : p.DiscoveryAddress is { } d && !IsHttps(d) ? "the discovery address is not https"
            : p.Scopes is null || !p.Scopes.Contains("openid") || p.Scopes.Any(s => string.IsNullOrEmpty(s) || s.Any(c => c <= ' ')) ? "the scopes are not openid and others"
            : new[] { p.SubjectClaim, p.EmailClaim, p.EmailVerifiedClaim, p.NameClaim }.Any(string.IsNullOrWhiteSpace) ? "a claim name is empty"
            : null;
        if (problem is null) return p;
        failures.Report(m.PluginId, "sign-in method", new InvalidDataException(problem));
        throw new OidcException("the plugin's provider is wrong: " + problem);
    }

    private async Task<string> ExchangeAsync(SignInMethodInfo m, OidcEndpoints endpoints, string clientId, string code, State state, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = state.Redirect, ["code_verifier"] = state.Verifier };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // RFC 6749, section 2.3.1: client_secret_basic, each part form-encoded. Without a secret the client is public and names itself.
        if (await methods.ClientSecretAsync(m, ct) is { Length: > 0 } secret)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(secret))));
        else form["client_id"] = clientId;
        request.Content = new FormUrlEncodedContent(form);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await cache.Client(m.PluginId).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        // The answer's body is not logged: it may hold tokens.
        if (!response.IsSuccessStatusCode) throw new OidcException($"the token request was refused ({(int)response.StatusCode})");
        using var doc = await OidcCache.ReadJsonAsync(response, stop.Token);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id_token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()! : throw new OidcException("the token answer has no ID token");
    }

    /// <summary>OpenID Connect Core 1.0, section 3.1.3.7: signature (asymmetric only), issuer, audience, authorized party, expiry, issue time, nonce.</summary>
    private async Task<SignInClaims> ValidateAsync(SignInMethodInfo m, OidcProvider p, OidcEndpoints endpoints, string clientId, string idToken, string nonce, CancellationToken ct)
    {
        var handler = new JsonWebTokenHandler();
        TokenValidationResult result = null!;
        foreach (var again in new[] { false, true })
        {
            var (keys, fresh) = await cache.KeysAsync(m.PluginId, endpoints.Keys, again, ct);
            result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = p.Issuer, ValidAudience = clientId, IssuerSigningKeys = keys, ValidAlgorithms = Algorithms, RequireSignedTokens = true,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(2), RequireAudience = true,
            });
            if (result.IsValid || again || fresh || result.Exception is not (SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException)) break;
        }
        if (!result.IsValid)
            throw new OidcException(result.Exception switch
            {
                SecurityTokenExpiredException => "the ID token has expired",
                SecurityTokenNoExpirationException => "the ID token has no expiry",
                SecurityTokenInvalidAudienceException => "the ID token is for another audience",
                SecurityTokenInvalidIssuerException => "the ID token is from another issuer",
                SecurityTokenInvalidAlgorithmException => "the ID token's algorithm is not allowed",
                SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "the ID token's signature is wrong",
                _ => "the ID token is not valid",
            });

        var token = (JsonWebToken)result.SecurityToken;
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        var all = payload.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
        string? Text(string claim) => all.TryGetValue(claim, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (!FixedEquals(Text("nonce"), nonce)) throw new OidcException("the ID token's nonce is wrong");
        if (!all.ContainsKey("iat")) throw new OidcException("the ID token has no issue time");
        var audiences = all.TryGetValue("aud", out var aud) && aud.ValueKind == JsonValueKind.Array ? aud.GetArrayLength() : 1;
        if ((audiences > 1 || Text("azp") is not null) && Text("azp") != clientId) throw new OidcException("the ID token's authorized party is another client");
        if (Text(p.SubjectClaim) is not { Length: > 0 and <= 450 } subject) throw new OidcException("the ID token has no subject");

        var verified = all.TryGetValue(p.EmailVerifiedClaim, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true");
        var email = Text(p.EmailClaim) is { Length: > 0 and <= 256 } e && e.Contains('@', StringComparison.Ordinal) ? e : null;
        var name = Text(p.NameClaim) is { Length: > 0 } n ? n.Trim()[..Math.Min(n.Trim().Length, 200)] : null;
        return new SignInClaims(p.Issuer, subject, email, verified && email is not null, name, all);
    }

    private async Task<string?> CheckAsync(SignInMethodInfo m, SignInClaims claims, CancellationToken ct)
    {
        var (ok, text) = await AsPluginAsync(m, (method, t) => method.CheckAsync(claims, t), ct);
        if (!ok) return Generic;
        return text is null ? null : string.IsNullOrWhiteSpace(text) ? Generic : text.Trim()[..Math.Min(text.Trim().Length, 200)];
    }

    /// <summary>Runs the method's code in a scope of its own, as its plugin, within the time limit. A failure is logged and counted against the plugin.</summary>
    private async Task<(bool Ok, T? Value)> AsPluginAsync<T>(SignInMethodInfo m, Func<ISignInMethod, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(Actor.Plugin(m.PluginId, ActorSources.SignIn));
            var run = call((ISignInMethod)ActivatorUtilities.CreateInstance(scope.ServiceProvider, m.Method), stop.Token);
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return (true, await run.WaitAsync(PluginTimeLimit, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (e is TimeoutException) await stop.CancelAsync();
            log.LogWarning(e, "The sign-in method {Method} failed.", m.Key);
            failures.Report(m.PluginId, "sign-in method", e);
            return (false, default);
        }
    }

    // ---- the account

    /// <summary>
    /// The account of the person the provider vouches for: the one linked to issuer plus subject; else, on the first sign-in, the one
    /// account with the email address, when the provider verified it; else a new one, when the method's setting allows it.
    /// </summary>
    private async Task<(KbUser? User, string? Reason, string? Message)> AccountAsync(SignInMethodInfo m, SignInClaims c, CancellationToken ct)
    {
        const string Off = "This account is turned off. Ask an Administrator.";
        var linked = await db.Users.FirstOrDefaultAsync(u => u.ExternalIssuer == c.Issuer && u.ExternalId == c.Subject, ct);
        if (linked is not null) return linked.DeactivatedAt is null ? (linked, null, null) : (null, "deactivated", Off);

        if (!c.EmailVerified) return (null, "email not verified", "The provider has not verified your email address, so it cannot be matched to an account here.");
        var normalized = users.NormalizeEmail(c.Email);
        var same = await db.Users.Where(u => u.NormalizedEmail == normalized && u.DeletedAt == null && !u.IsBreakGlass && u.Source != UserSource.Service).Take(2).ToListAsync(ct);
        if (same.Count > 1) return (null, "several accounts have the email", "More than one account here has your email address. Ask an Administrator.");
        if (same.Count == 1)
        {
            var u = same[0];
            if (u.ExternalIssuer is not null) return (null, "the account is linked to another sign-in", "Your account here is linked to another sign-in. Ask an Administrator.");
            if (u.DeactivatedAt is not null) return (null, "deactivated", Off);
            (u.ExternalIssuer, u.ExternalId) = (c.Issuer, c.Subject);
            if (!await SaveAsync(ct)) return (null, "the link could not be saved", Generic);
            await audit.AppendAsync(new AuditEvent(AuditActions.UserSignInLink, u.Id, "user", u.Id.ToString(),
                Data: new Dictionary<string, string> { ["method"] = m.Key, ["issuer"] = c.Issuer, ["subject"] = c.Subject }), ct);
            return (u, null, null);
        }

        if (!methods.NewAccounts(m)) return (null, "no account", "There is no account here for you. Ask an Administrator for one.");
        var made = new KbUser
        {
            UserName = await FreeNameAsync(c.Email!), DisplayName = c.Name ?? c.Email!, Email = c.Email, EmailConfirmed = true, Source = UserSource.SignInMethod,
            ExternalIssuer = c.Issuer, ExternalId = c.Subject, CreatedAt = time.GetUtcNow(), LockoutEnabled = true,
        };
        // Never an Admin, in no group and no space: an Admin gives it what it needs.
        var created = await users.CreateAsync(made);
        if (!created.Succeeded) return (null, "the account could not be made: " + string.Join(" ", created.Errors.Select(e => e.Code)), Generic);
        await audit.AppendAsync(new AuditEvent(AuditActions.UserSignInCreate, made.Id, "user", made.Id.ToString(),
            Data: new Dictionary<string, string> { ["method"] = m.Key, ["issuer"] = c.Issuer, ["subject"] = c.Subject, ["userName"] = made.UserName }), ct);
        return (made, null, null);
    }

    private async Task<bool> SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { return false; } // the same link was made a moment ago by another sign-in
    }

    /// <summary>The email address as the user name when it is free and allowed; otherwise "user-" and 8 random hex digits.</summary>
    private async Task<string> FreeNameAsync(string email)
    {
        var allowed = users.Options.User.AllowedUserNameCharacters;
        if (email.Length <= 256 && email.All(ch => allowed.Contains(ch, StringComparison.Ordinal)) && await users.FindByNameAsync(email) is null) return email;
        return "user-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    }

    // ---- small parts

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static bool FixedEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
