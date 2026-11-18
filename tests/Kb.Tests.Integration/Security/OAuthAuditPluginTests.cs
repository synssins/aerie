using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Tests.Integration.DataCenter;
using Kb.Web.DataCenter;
using Kb.Web.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The Low findings of the code review of 2026-11-17 about OAuth, the audit log, storage places and plugins: L19 (an OAuth client's
/// actions name the client), L20 (the loopback port, and codes after a scope is removed), L22 (the place files are in keeps its
/// location), L24 (one person cannot make the core turn a plugin off). L23 is in StartupLoadingTests.
/// </summary>
public sealed class OAuthAuditPluginTests
{
    // ---- L19 ----

    [Fact]
    public async Task WhatAnOAuthClientDoes_NamesTheClientInTheAuditLog()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var ravi = await UserAsync(f, "ravi");
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = ravi.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var app = await OAuthKit.ClientAsync(admin);
        var client = OAuthKit.Bearer(f, (await OAuthKit.TokensAsync(f, app, "WRITE", "ravi")).GetProperty("access_token").GetString()!);
        var page = (await (await client.PostJsonAsync("/rest/api/content", new { type = "page", title = "By the app", space = new { key = "OPS" }, body = new { storage = new { value = "<p>x</p>", representation = "storage" } } })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/rest/api/content/{page}", Ct)).StatusCode);
        await f.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.Where(a => a.Action == AuditActions.PageDelete).OrderByDescending(a => a.Sequence).FirstAsync(Ct);
            var data = JsonDocument.Parse(entry.DataJson).RootElement;
            Assert.Equal(ravi, entry.ActorId);
            Assert.Equal(app.ClientId, data.GetProperty("clientId").GetString());
            Assert.True(Guid.TryParse(data.GetProperty("oauthTokenId").GetString(), out _));
        });
    }

    // ---- L20 ----

    [Fact]
    public void LoopbackRedirect_APortIsDigits()
    {
        Assert.True(OAuthRedirects.Matches("http://localhost/cb", "http://localhost:8123/cb")); // any port on loopback, as before
        Assert.False(OAuthRedirects.Matches("http://localhost/cb", "http://localhost:80#/cb"));
        Assert.False(OAuthRedirects.Matches("http://127.0.0.1/cb", "http://127.0.0.1:x/cb"));
    }

    [Fact]
    public async Task RemovingAScopeFromAClient_EndsCodesNotYetExchanged()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        var app = await OAuthKit.ClientAsync(admin);
        var code = await OAuthKit.CodeAsync(f, app, "WRITE", "ravi");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{app.Id}", new { name = "Desk app", scopes = new[] { "READ" } }, Ct)).StatusCode);
        await OAuthKit.PostTokenAsync(f, app, new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = OAuthKit.Cb, ["code_verifier"] = OAuthKit.Verifier }, HttpStatusCode.BadRequest);
    }

    // ---- L22 ----

    [Fact]
    public async Task ThePlaceFilesAreIn_KeepsItsLocation()
    {
        var path = KbAppFactory.NewDbPath();
        await using var f = new KbAppFactory(path, settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = path + ".files" });
        var admin = await f.SignedInBreakGlassAsync();
        var active = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/storage", Ct)).GetProperty("places").EnumerateArray().Single();
        var id = active.GetProperty("id").GetString();
        var elsewhere = Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.place");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/admin/storage/places/{id}", new { name = "Web server folder", settings = new { path = elsewhere } }, Ct)).StatusCode);
        // Its name can change, and a place that is not in use can be pointed anywhere.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/admin/storage/places/{id}", new { name = "Main folder", settings = new { path = path + ".files" } }, Ct)).StatusCode);
        var spare = (await (await admin.PostAsJsonAsync("/api/v1/admin/storage/places", new { name = "Spare", kind = "folder", settings = new { path = elsewhere } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/admin/storage/places/{spare}", new { name = "Spare", settings = new { path = elsewhere + "2" } }, Ct)).StatusCode);
    }

    // ---- L24 ----

    [Fact]
    public async Task OnePersonsFailures_DoNotTurnAPluginOff_AnAdminsDo()
    {
        var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        f = await f.RestartAsync();
        await using var _ = f;
        PluginFailures As(Guid? user) => new(f.Services.GetRequiredService<IServiceScopeFactory>(), f.Services.GetRequiredService<PluginRegistry>(), TimeProvider.System, NullLogger<PluginFailures>.Instance,
            new HttpContextAccessor { HttpContext = user is { } id ? new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test")) } : null });
        async Task<PluginState> StateAsync() => await f.WithDbValueAsync(db => db.PluginStatuses.AsNoTracking().Where(s => s.PluginId == "kb.test.probe").Select(s => s.State).SingleAsync(Ct));

        // One person sends the plugin something it chokes on, twenty times.
        var mallory = As(await UserAsync(f, "mallory"));
        for (var i = 0; i < 20; i++) mallory.Report("kb.test.probe", "page.save hook", new InvalidOperationException("bad input"));
        await mallory.Pending;
        Assert.Equal(PluginState.Enabled, await StateAsync());

        // The same plugin failing for an Admin five times is turned off, as before.
        var boss = As(await f.WithDbValueAsync(db => db.Users.Where(u => u.IsBreakGlass).Select(u => u.Id).SingleAsync(Ct)));
        for (var i = 0; i < PluginFailures.Limit; i++) boss.Report("kb.test.probe", "page.save hook", new InvalidOperationException("broken"));
        await boss.Pending;
        Assert.Equal(PluginState.DisabledByCore, await StateAsync());
    }
}
