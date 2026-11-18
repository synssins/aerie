using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Kb.Core.Audit;
using Kb.Plugins.Actions;
using Kb.Tests.Integration.DataCenter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings H1 to H3 of the code review of 2026-11-17: a write on the compatible surface is refused before its body is read when nobody is
/// signed in, upload parts and nested markup have a bound, and a forwarded address is believed only from a proxy named in configuration.
/// </summary>
public sealed class BoundsAndProxyTests
{
    private const string XsrfHeader = Kb.Web.DataCenter.DcRest.XsrfHeader;

    private static async Task<(KbAppFactory F, HttpClient Cora, string Page)> SiteAsync()
    {
        var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var id = await UserAsync(f, "cora");
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = id.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var cora = Basic(f, "cora");
        var page = (await (await cora.PostJsonAsync("/rest/api/content", new { type = "page", title = "Home", space = new { key = "OPS" }, body = new { storage = new { value = "<p>x</p>", representation = "storage" } } }))
            .JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;
        return (f, cora, page);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, string path, HttpContent body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        req.Headers.Add(XsrfHeader, "no-check");
        return c.SendAsync(req, Ct);
    }

    // ---- H1 ----

    /// <summary>
    /// The answer to a caller who is not signed in does not depend on the body: it is 401 for a body the route would have answered 415 or
    /// 400 to once it had read it.
    /// </summary>
    [Fact]
    public async Task Surface_WriteByNobody_Is401_WhateverTheBody()
    {
        var (f, _, page) = await SiteAsync();
        await using var _ = f;
        var nobody = f.NewClient();
        await (await UploadAsync(nobody, $"/rest/api/content/{page}/child/attachment", new StringContent("not multipart", Encoding.UTF8, "text/plain"))).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await nobody.PostAsync("/rest/api/content", new StringContent("{ not json", Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await nobody.PostAsync($"/rest/api/content/{page}/label", new StringContent("nothing", Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await nobody.PutAsync("/rest/api/space/OPS", new StringContent("[]", Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        // Reading stays open where the space lets it be: only the write is refused here.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await nobody.GetAsync("/rest/api/user/current", Ct)).StatusCode);
    }

    [Fact]
    public async Task Upload_TextPartOver64Kb_Is400_AndFileOverTheLimit_Is413()
    {
        var (f, cora, page) = await SiteAsync();
        await using var _ = f;
        using var longComment = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("bytes")), "file", "a.txt" },
            { new StringContent(new string('c', 64 * 1024 + 1)), "comment" },
        };
        await (await UploadAsync(cora, $"/rest/api/content/{page}/child/attachment", longComment)).ErrorAsync(HttpStatusCode.BadRequest);

        using var fits = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("bytes")), "file", "b.txt" },
            { new StringContent(new string('c', 64 * 1024)), "comment" },
        };
        await (await UploadAsync(cora, $"/rest/api/content/{page}/child/attachment", fits)).JsonAsync(HttpStatusCode.OK);

        // One byte over the default limit of 25 MB, with a part after it that is never read.
        using var large = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[Kb.Web.UploadLimits.DefaultAttachmentMb * 1_048_576 + 1]), "file", "large.bin" },
            { new StringContent("after"), "comment" },
        };
        await (await UploadAsync(cora, $"/rest/api/content/{page}/child/attachment", large)).ErrorAsync(HttpStatusCode.RequestEntityTooLarge);
    }

    // ---- H2 ----

    private static string Nested(string tag, int depth) => string.Concat(Enumerable.Repeat($"<{tag}>", depth)) + "x" + string.Concat(Enumerable.Repeat($"</{tag}>", depth));

    /// <summary>The parser calls itself once per nested element. Past the bound the answer is 400; the process is still there afterwards.</summary>
    [Theory]
    [InlineData(150)]
    [InlineData(50_000)]
    public async Task Convert_DeeplyNestedMarkup_Is400(int depth)
    {
        var (f, cora, page) = await SiteAsync();
        await using var _ = f;
        await (await cora.PostJsonAsync("/rest/api/contentbody/convert/view", new { representation = "storage", value = Nested("b", depth) })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await cora.PutAsJsonAsync($"/rest/api/content/{page}", new { type = "page", title = "Home", version = new { number = 2 }, body = new { storage = new { value = Nested("b", depth), representation = "storage" } } }, Ct))
            .ErrorAsync(HttpStatusCode.BadRequest);
        // At the bound it still converts.
        await (await cora.PostJsonAsync("/rest/api/contentbody/convert/view", new { representation = "storage", value = Nested("b", Kb.Web.DataCenter.StorageFormat.MaxDepth) })).JsonAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Convert_BodyOverFiveMillionCharacters_Is400()
    {
        var (f, cora, _) = await SiteAsync();
        await using var __ = f;
        var value = "<p>" + new string('x', 5_000_000) + "</p>";
        await (await cora.PostJsonAsync("/rest/api/contentbody/convert/view", new { representation = "storage", value })).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_DeeplyNestedQuery_Is400()
    {
        var (f, cora, _) = await SiteAsync();
        await using var __ = f;
        string Query(int depth) => "/rest/api/content/search?cql=" + Uri.EscapeDataString(new string('(', depth) + "type = page" + new string(')', depth));
        await (await cora.GetAsync(Query(60), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await cora.GetAsync("/rest/api/content/search?cql=" + Uri.EscapeDataString("type = page AND " + string.Concat(Enumerable.Repeat("NOT ", 60)) + "title = x"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await cora.GetJsonAsync(Query(Kb.Web.DataCenter.Cql.MaxDepth));
    }

    // ---- H3 ----

    /// <summary>The connection's address, which TestServer does not have.</summary>
    private sealed class From(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse(address); return n(ctx); });
            next(app);
        };
    }

    private static KbAppFactory Site(string connectsFrom, params string[] trusted)
    {
        // The framework's own switch is on, as it was in the image: it must not bring the old behaviour back.
        var settings = new Dictionary<string, string> { ["ForwardedHeaders_Enabled"] = "true" };
        for (var i = 0; i < trusted.Length; i++) settings[$"Kb:Proxy:Trusted:{i}"] = trusted[i];
        return new KbAppFactory(KbAppFactory.NewDbPath(), settings: settings, services: s => s.AddSingleton<IStartupFilter>(new From(connectsFrom)));
    }

    /// <summary>The address a failed sign-in was audited with, when the request carried this X-Forwarded-For.</summary>
    private static async Task<string> AuditedAddressAsync(KbAppFactory f, string forwardedFor)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/local/signin") { Content = JsonContent.Create(new { userName = "nobody-" + Guid.NewGuid().ToString("N"), password = "wrong" }) };
        req.Headers.Add("X-Forwarded-For", forwardedFor);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().SendAsync(req, Ct)).StatusCode);
        string json = "";
        await f.WithDbAsync(async db => json = (await db.AuditEntries.Where(a => a.Action == AuditActions.SignInFailed).OrderByDescending(a => a.Sequence).FirstAsync(Ct)).DataJson);
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("ip").GetString()!;
    }

    [Fact]
    public async Task ForwardedFor_FromACallerThatIsNotANamedProxy_IsIgnored()
    {
        await using var none = Site("203.0.113.9");
        Assert.Equal("203.0.113.9", await AuditedAddressAsync(none, "198.51.100.7"));
        await using var other = Site("203.0.113.9", "10.0.0.0/8", "192.0.2.1");
        Assert.Equal("203.0.113.9", await AuditedAddressAsync(other, "198.51.100.7"));
    }

    [Fact]
    public async Task ForwardedFor_FromANamedProxy_IsTheClient_AndTheCallersOwnEntryIsNot()
    {
        await using var network = Site("203.0.113.9", "203.0.113.0/24");
        Assert.Equal("198.51.100.7", await AuditedAddressAsync(network, "198.51.100.7"));
        // What the caller wrote into the header comes before what the proxy added: it is not taken.
        Assert.Equal("198.51.100.7", await AuditedAddressAsync(network, "127.0.0.1, 198.51.100.7"));
        await using var single = Site("203.0.113.9", "203.0.113.9");
        Assert.Equal("198.51.100.7", await AuditedAddressAsync(single, "198.51.100.7"));
        // A chain of named proxies: the first address that is not one of them is the client.
        await using var chain = Site("203.0.113.9", "203.0.113.9", "192.0.2.1");
        Assert.Equal("198.51.100.7", await AuditedAddressAsync(chain, "10.9.9.9, 198.51.100.7, 192.0.2.1"));
    }

    [Fact]
    public async Task Proxy_AnEntryThatIsNoAddress_StopsTheStart()
    {
        await using var f = Site("203.0.113.9", "the-proxy");
        await Assert.ThrowsAnyAsync<Exception>(() => f.NewClient().GetAsync("/health/live", Ct));
    }

    /// <summary>A proxy on the same machine connects from loopback for every caller: a forwarded request never skips the setup code.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Setup_FromLoopbackWithAForwardedAddress_NeedsTheCode(bool proxyNamed)
    {
        await using var f = proxyNamed ? Site("127.0.0.1", "127.0.0.1") : Site("127.0.0.1");
        var body = new { userName = "admin", displayName = "First Admin", password = "first-admin-password-123" };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.NewClient().SendAsync(req, Ct)).StatusCode);
        var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/setup/status");
        status.Headers.Add("X-Forwarded-For", "127.0.0.1");
        Assert.True((await (await f.NewClient().SendAsync(status, Ct)).Content.ReadFromJsonAsync<SetupStatus>(Ct))!.CodeRequired);
        // Asked directly on the server, with no proxy in between, setup still needs no code.
        Assert.Equal(HttpStatusCode.Conflict, (await f.NewClient().PostAsJsonAsync("/api/v1/setup", body, Ct)).StatusCode);
    }
}
