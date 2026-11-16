using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 2: the scope check after credential resolution and before the user's own permissions (identity.md, Scopes).</summary>
public sealed class DcScopeTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _admin.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check");
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private HttpClient Bearer(string token)
    {
        var c = _f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>A token made through aerie's own token page by a person signed in with a password.</summary>
    private static async Task<string> TokenAsync(HttpClient owner, string access, Guid? space = null) =>
        (await (await owner.PostAsJsonAsync("/api/v1/tokens", new { name = "S " + Guid.NewGuid().ToString("N")[..6], kind = "Personal", scopes = new[] { new { spaceId = space, access } }, days = 30 }, Ct))
            .JsonAsync(HttpStatusCode.Created)).GetProperty("token").GetString()!;

    private async Task<HttpClient> SessionAsync(string name)
    {
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).StatusCode);
        return c;
    }

    private async Task<(Guid Id, string Key)> SpaceAsync(string key)
    {
        var id = long.Parse((await (await _admin.PostJsonAsync("/rest/api/space", new { key, name = key })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetRawText(), System.Globalization.CultureInfo.InvariantCulture);
        return (await _f.WithDbValueAsync(db => db.DcSpaces.Where(s => s.Id == id).Select(s => s.SpaceId).SingleAsync(Ct)), key);
    }

    private async Task<string> PageAsync(string space, string title) =>
        (await (await _admin.PostJsonAsync("/rest/api/content", new { type = "page", title, space = new { key = space }, body = new { storage = new { value = "<p>needle " + title + "</p>", representation = "storage" } } }))
            .JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;

    private async Task RoleAsync(Guid space, Guid user, string role) =>
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = user.ToString(), role }, Ct)).EnsureSuccessStatusCode();

    /// <summary>Default deny: every routed operation of the surface has a scope, read from the contract with the method rule.</summary>
    [Fact]
    public void EveryRoute_HasAScope_FromTheContract()
    {
        var routes = _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<DcRoute>() is not null || e.Metadata.GetMetadata<DcDownloadRoute>() is not null).ToList();
        Assert.NotEmpty(routes);
        var scopes = _f.Services.GetRequiredService<DcScopes>();
        foreach (var e in routes)
            foreach (var method in e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods)
                Assert.Equal(method is "GET" or "HEAD" ? "READ" : "WRITE", scopes.Required(method, e));
        // Every contract operation of /rest/api is in the table the check uses.
        Assert.All(DcContractTests.ContractOperations().Where(o => o.Contains("/rest/api", StringComparison.Ordinal)), o => Assert.True(scopes.Knows(o), o));
    }

    // auth.pat.read_only.write_refused: 403 with insufficient_scope, nothing changes, audited.
    [Fact]
    public async Task ReadToken_CannotWrite()
    {
        await SpaceAsync("OPS");
        var r = await Bearer(await TokenAsync(_admin, "Read")).PostJsonAsync("/rest/api/content", new { type = "page", title = "No", space = new { key = "OPS" } });
        await r.ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Bearer error=\"insufficient_scope\", scope=\"WRITE\"", r.Headers.WwwAuthenticate.ToString());
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.Pages.AnyAsync(p => p.Title == "No", Ct));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.TokenUseDenied && a.DataJson.Contains("scope"), Ct));
        });
    }

    // auth.pat.space_limited.hidden: another space's page is 404 and is not in search.
    [Fact]
    public async Task SpaceLimitedToken_DoesNotSeeOtherSpaces_InReadsOrSearch()
    {
        var (a, _) = await SpaceAsync("AAA");
        await SpaceAsync("BBB");
        var inA = await PageAsync("AAA", "alpha");
        var inB = await PageAsync("BBB", "beta");
        var t = Bearer(await TokenAsync(_admin, "Write", a));
        Assert.Equal(HttpStatusCode.OK, (await t.GetAsync($"/rest/api/content/{inA}", Ct)).StatusCode);
        await (await t.GetAsync($"/rest/api/content/{inB}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        foreach (var path in new[] { "/rest/api/search?cql=type%3Dpage", "/rest/api/content/search?cql=type%3Dpage", "/rest/api/content?type=page", "/rest/api/space" })
        {
            var text = (await t.GetJsonAsync(path)).GetRawText();
            Assert.DoesNotContain("BBB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"" + inB + "\"", text, StringComparison.Ordinal);
        }
    }

    // auth.scope.permission_still_applies: a Write token of a Reader cannot edit.
    [Fact]
    public async Task WriteToken_OfAReader_CannotEdit()
    {
        var (id, _) = await SpaceAsync("RDR");
        var page = await PageAsync("RDR", "read me");
        var ravi = await UserAsync(_f, "ravi");
        await RoleAsync(id, ravi, "Reader");
        var t = Bearer(await TokenAsync(await SessionAsync("ravi"), "Write"));
        Assert.Equal(HttpStatusCode.OK, (await t.GetAsync($"/rest/api/content/{page}", Ct)).StatusCode);
        var r = await t.PutAsJsonAsync($"/rest/api/content/{page}", new { type = "page", title = "changed", version = new { number = 2 } }, Ct);
        await r.ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Empty(r.Headers.WwwAuthenticate); // the scope passed; the role refused
    }

    // Passwords and cookies pass every scope; the role decides.
    [Fact]
    public async Task PasswordsAndCookies_PassEveryScope()
    {
        await UserAsync(_f, "ravi");
        Assert.Equal(HttpStatusCode.OK, (await Basic(_f, "ravi").GetAsync("/rest/api/space", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostJsonAsync("/rest/api/space", new { key = "CK", name = "Cookie" })).StatusCode);
    }
}
