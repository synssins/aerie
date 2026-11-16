using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>What the OAuth 2.0 tests share: registering a client, a user's consent, and the token endpoint.</summary>
internal static class OAuthKit
{
    public const string Cb = "https://app.example/cb";
    public const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    public sealed record App(string ClientId, string? Secret, Guid Id);

    public static async Task<App> ClientAsync(HttpClient admin, string type = "confidential", string[]? scopes = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/admin/oauth/clients", new { name = "Desk app", type, redirectUris = new[] { Cb }, scopes = scopes ?? ["READ", "WRITE"] }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new App(j.GetProperty("client").GetProperty("clientId").GetString()!, j.GetProperty("clientSecret").GetString(), j.GetProperty("client").GetProperty("id").GetGuid());
    }

    /// <summary>The user signs in, consents, and the browser comes back with a code.</summary>
    public static async Task<string> CodeAsync(KbAppFactory f, App app, string scope, string user)
    {
        if (await UserIdAsync(f, user) is null) await UserAsync(f, user);
        var b = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = user, password = Password }, Ct)).StatusCode);
        var r = await b.GetAsync($"/rest/oauth2/latest/authorize?response_type=code&client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(Cb)}&scope={Uri.EscapeDataString(scope)}&code_challenge={OAuthSecrets.S256(Verifier)}&code_challenge_method=S256", Ct);
        var location = r.Headers.Location!.ToString();
        if (location.StartsWith("/oauth/consent/", StringComparison.Ordinal))
        {
            var decided = await b.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{location["/oauth/consent/".Length..]}/decision", new { allow = true }, Ct);
            location = (await decided.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("redirectTo").GetString()!;
        }
        return QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
    }

    public static async Task<JsonElement> PostTokenAsync(KbAppFactory f, App app, Dictionary<string, string> form, HttpStatusCode expected = HttpStatusCode.OK)
    {
        form["client_id"] = app.ClientId;
        if (app.Secret is not null) form["client_secret"] = app.Secret;
        var r = await f.NewClient().PostAsync("/rest/oauth2/latest/token", new FormUrlEncodedContent(form), Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.StatusCode == expected, $"expected {(int)expected}, got {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Tokens for this user and scope: authorize, consent, exchange.</summary>
    public static async Task<JsonElement> TokensAsync(KbAppFactory f, App app, string scope = "READ", string user = "ravi") =>
        await PostTokenAsync(f, app, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = await CodeAsync(f, app, scope, user), ["redirect_uri"] = Cb, ["code_verifier"] = Verifier,
        });

    public static HttpClient Bearer(KbAppFactory f, string token)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}
