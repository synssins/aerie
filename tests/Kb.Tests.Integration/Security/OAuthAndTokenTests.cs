using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Tests.Integration.DataCenter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings M8 and M11 of the code review of 2026-11-17: nobody can use up a registered client's limit at the token endpoint by naming its
/// public id, and a service token that names a space ends when the space becomes sensitive.
/// </summary>
public sealed class OAuthAndTokenTests
{
    /// <summary>The connection's address, taken from a header only this test host reads: TestServer has none.</summary>
    private sealed class AddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) =>
            {
                if (ctx.Request.Headers["X-Test-Address"].ToString() is { Length: > 0 } a) ctx.Connection.RemoteIpAddress = IPAddress.Parse(a);
                return n(ctx);
            });
            next(app);
        };
    }

    // ---- M8 ----

    [Theory]
    [InlineData("/rest/oauth2/latest/token")]
    [InlineData("/rest/oauth2/latest/revoke")]
    public async Task NamingAClient_DoesNotUseUpItsLimit(string endpoint)
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:OAuthTokenRequestsPerMinute"] = "3" },
            services: s => s.AddSingleton<IStartupFilter, AddressFromHeader>());
        var admin = await f.SignedInBreakGlassAsync();
        var app = await OAuthKit.ClientAsync(admin);
        var code = await OAuthKit.CodeAsync(f, app, "READ", "ravi");

        // Someone else, from another address, names the client three times without its secret: all the client's limit allows.
        var other = f.NewClient();
        other.DefaultRequestHeaders.Add("X-Test-Address", "198.51.100.7");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = "wrong", ["grant_type"] = "refresh_token", ["token"] = "x" }), Ct)).StatusCode);
        // They are limited themselves, by their address.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await other.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = app.ClientId }), Ct)).StatusCode);

        // The client itself, from its own address, is still served.
        var mine = f.NewClient();
        mine.DefaultRequestHeaders.Add("X-Test-Address", "203.0.113.9");
        var form = endpoint.EndsWith("/token", StringComparison.Ordinal)
            ? new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = OAuthKit.Cb, ["code_verifier"] = OAuthKit.Verifier }
            : new Dictionary<string, string> { ["token"] = "not-a-token" };
        (form["client_id"], form["client_secret"]) = (app.ClientId, app.Secret!);
        Assert.Equal(HttpStatusCode.OK, (await mine.PostAsync(endpoint, new FormUrlEncodedContent(form), Ct)).StatusCode);
    }

    /// <summary>The client's own limit still holds once it has shown its secret, whatever address it calls from.</summary>
    [Fact]
    public async Task AClient_ThatShowedItsSecret_IsStillLimited()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:OAuthTokenRequestsPerMinute"] = "3" },
            services: s => s.AddSingleton<IStartupFilter, AddressFromHeader>());
        var app = await OAuthKit.ClientAsync(await f.SignedInBreakGlassAsync());
        async Task<HttpStatusCode> RevokeFromAsync(string address)
        {
            var c = f.NewClient();
            c.DefaultRequestHeaders.Add("X-Test-Address", address);
            return (await c.PostAsync("/rest/oauth2/latest/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = app.Secret!, ["token"] = "x" }), Ct)).StatusCode;
        }
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await RevokeFromAsync("203.0.113." + (i + 1)));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RevokeFromAsync("203.0.113.200"));
    }

    // ---- M11 ----

    [Fact]
    public async Task ServiceToken_ThatNamesASpace_EndsWhenTheSpaceBecomesSensitive()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        async Task<Guid> SpaceAsync(string name) => (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var (payroll, desk) = (await SpaceAsync("Payroll"), await SpaceAsync("Desk"));
        async Task<(HttpClient Client, Guid Id)> ServiceTokenAsync(string name, params Guid[] spaces)
        {
            var j = await (await admin.PostAsJsonAsync("/api/v1/tokens", new { name, kind = "Service", scopes = spaces.Select(s => new { spaceId = s, access = "Write" }), days = 30 }, Ct)).JsonAsync(HttpStatusCode.Created);
            var c = f.NewClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", j.GetProperty("token").GetString());
            return (c, j.GetProperty("info").GetProperty("id").GetGuid());
        }
        var (both, bothId) = await ServiceTokenAsync("Sync both", payroll, desk);
        var (deskOnly, _) = await ServiceTokenAsync("Sync desk", desk);
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"/api/v1/spaces/{payroll}", Ct)).StatusCode);

        (await admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{payroll}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();

        // The token that names the space is revoked: it reaches nothing any more, the other space included.
        Assert.Equal(HttpStatusCode.Unauthorized, (await both.GetAsync($"/api/v1/spaces/{payroll}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await both.GetAsync($"/api/v1/spaces/{desk}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deskOnly.GetAsync($"/api/v1/spaces/{desk}", Ct)).StatusCode);
        await f.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.TokenRevoke, Ct);
            Assert.Equal((bothId.ToString(), payroll), (entry.TargetId, entry.SpaceId!.Value));
            Assert.Contains("the space became sensitive", entry.DataJson, StringComparison.Ordinal);
        });
    }
}
