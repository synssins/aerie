using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class ApiTokenTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Tokens:RequestsPerMinute"] = "100" });
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"reset the widget"}]}]}""";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static async Task<Guid> SpaceAsync(HttpClient admin, string name, bool sensitive = false) =>
        (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name, isSensitive = sensitive }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;

    private static async Task<Guid> PageAsync(HttpClient c, Guid space, string title = "Widget reset")
    {
        var r = await c.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = Doc }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static object Scope(Guid? space, string access) => new { spaceId = space, access };

    private static Task<HttpResponseMessage> CreateAsync(HttpClient c, object[] scopes, string kind = "Personal", int? days = 30, string name = "Agent", Guid? accountId = null) =>
        c.PostAsJsonAsync("/api/v1/tokens", new { name, kind, scopes, days, accountId }, Ct);

    private async Task<(HttpClient Client, JsonElement Info, string Text)> TokenAsync(HttpClient owner, object[] scopes, string kind = "Personal", KbAppFactory? on = null)
    {
        var r = await CreateAsync(owner, scopes, kind);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var text = body.GetProperty("token").GetString()!;
        return (Bearer(text, on), body.GetProperty("info"), text);
    }

    private HttpClient Bearer(string token, KbAppFactory? on = null)
    {
        var c = (on ?? _f).NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, Guid? space = null, string role = "Contributor")
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var u = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(u, "user-password-0123456789")).Succeeded);
            id = u.Id;
        }
        if (space is { } s)
        {
            var admin = await _f.SignedInBreakGlassAsync();
            await admin.PutAsJsonAsync($"/api/v1/spaces/{s}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    [Fact]
    public async Task Create_ReturnsTheTokenOnce_StoresOnlyItsHash()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var (client, info, text) = await TokenAsync(admin, [Scope(null, "Read")]);

        Assert.StartsWith("kbt_", text);
        Assert.Equal(text[..12], info.GetProperty("prefix").GetString());
        Assert.Equal("Active", info.GetProperty("state").GetString());
        var list = await admin.GetStringAsync("/api/v1/tokens", Ct);
        Assert.Contains(info.GetProperty("id").GetString()!, list);
        Assert.DoesNotContain(text, list);
        Assert.DoesNotContain(text, await admin.GetStringAsync("/api/v1/admin/tokens", Ct));
        await _f.WithDbAsync(async db =>
        {
            var row = await db.ApiTokens.SingleAsync(Ct);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), row.Hash);
            Assert.DoesNotContain(text[12..], JsonSerializer.Serialize(row));
            Assert.DoesNotContain(await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct), d => d.Contains(text[12..]));
        });

        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);
        Assert.Equal("breakglass", me!.UserName);
    }

    [Fact]
    public async Task Token_ScopedRead_CannotWrite()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin, "Desk");
        var page = await PageAsync(admin, space);
        var (token, _, _) = await TokenAsync(admin, [Scope(space, "Read")]);

        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        Assert.Contains("Widget reset", await token.GetStringAsync("/api/v1/search?q=widget", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "New", contentJson = Doc }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Token_ScopedToOneSpace_SeesNothingOfAnother()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var a = await SpaceAsync(admin, "Alpha");
        var b = await SpaceAsync(admin, "Bravo");
        await PageAsync(admin, a, "Alpha widget");
        var other = await PageAsync(admin, b, "Bravo widget");
        var (token, _, _) = await TokenAsync(admin, [Scope(a, "Write")]);

        var spaces = await token.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct);
        Assert.Equal([a], spaces!.Select(s => s.Id));
        Assert.Equal("Contributor", spaces![0].MyRole);
        var search = await token.GetStringAsync("/api/v1/search?q=widget", Ct);
        Assert.Contains("Alpha widget", search);
        Assert.DoesNotContain("Bravo widget", search);
        Assert.DoesNotContain("Bravo widget", await token.GetStringAsync("/api/v1/home", Ct));
        foreach (var path in new[] { $"/spaces/{b}", $"/spaces/{b}/tree", $"/pages/{other}", $"/pages/{other}/markdown", $"/pages/{other}/versions", $"/pages/{other}/export?format=md" })
            Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync("/api/v1" + path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.PostAsJsonAsync($"/api/v1/spaces/{b}/pages", new { kind = "Page", title = "New", contentJson = Doc }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.PutAsJsonAsync($"/api/v1/pages/{other}", new { title = "x", contentJson = Doc, baseVersion = "1.0" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Token_Write_ActsAsItsOwner_CappedAtContributor()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin, "Desk");
        var (token, _, _) = await TokenAsync(admin, [Scope(null, "Write")]);

        var page = await PageAsync(token, space, "By token");
        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct);
        Assert.Equal("Break-glass administrator", read.GetProperty("updatedBy").GetString());
        // The owner is an Admin and so Owner of the space; the token is not.
        Assert.Equal("Contributor", (await token.GetFromJsonAsync<SpaceDto>($"/api/v1/spaces/{space}", Ct))!.MyRole);
    }

    [Fact]
    public async Task Token_OwnerLosesSpaceRole_TokenLosesAccess()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin, "Desk");
        var page = await PageAsync(admin, space);
        var (user, userId) = await UserAsync("tech", space);
        var (token, _, _) = await TokenAsync(user, [Scope(null, "Write")]);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);

        var roles = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{space}/roles", Ct);
        var assignment = roles.EnumerateArray().Single(r => r.GetProperty("principalId").GetString() == userId.ToString()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/spaces/{space}/roles/{assignment}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Token_RevokedOnOtherInstance_RejectedImmediately()
    {
        await using var second = new KbAppFactory(_f.DbPath);
        var admin = await _f.SignedInBreakGlassAsync();
        var (onSecond, info, _) = await TokenAsync(admin, [Scope(null, "Read")], on: second);
        Assert.Equal(HttpStatusCode.OK, (await onSecond.GetAsync("/api/v1/me", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tokens/{info.GetProperty("id").GetString()}", Ct)).StatusCode);

        var r = await onSecond.GetAsync("/api/v1/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("Bearer", r.Headers.WwwAuthenticate.ToString());
        Assert.Contains("\"Revoked\"", await admin.GetStringAsync("/api/v1/tokens", Ct));
    }

    [Fact]
    public async Task Token_Expired_OrMalformed_OrOwnerDeactivated_Rejected()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var (user, userId) = await UserAsync("tech");
        var (expired, _, text) = await TokenAsync(admin, [Scope(null, "Read")]);
        var (orphan, _, _) = await TokenAsync(user, [Scope(null, "Read")]);
        await _f.WithDbAsync(async db =>
        {
            (await db.ApiTokens.SingleAsync(t => t.OwnerUserId != userId, Ct)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await db.Users.SingleAsync(u => u.Id == userId, Ct)).DeactivatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await orphan.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(text[..^1] + (text[^1] == 'a' ? 'b' : 'a')).GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer("kbt_not-a-token").GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Create_NeedsNameScopesAndAnExpiryWithinTheAdminMaximum()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin, "Hidden", sensitive: true); // the Admin has no role in it
        async Task<string> FieldAsync(Task<HttpResponseMessage> call)
        {
            var r = await call;
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateObject().Single().Name;
        }

        Assert.Equal("days", await FieldAsync(CreateAsync(admin, [Scope(null, "Read")], days: null)));
        Assert.Equal("days", await FieldAsync(CreateAsync(admin, [Scope(null, "Read")], days: 366)));
        Assert.Equal("name", await FieldAsync(CreateAsync(admin, [Scope(null, "Read")], name: " ")));
        Assert.Equal("scopes", await FieldAsync(CreateAsync(admin, [])));
        Assert.Equal("scopes", await FieldAsync(CreateAsync(admin, [Scope(space, "Read")])));
        Assert.Equal("scopes", await FieldAsync(CreateAsync(admin, [Scope(Guid.NewGuid(), "Read")])));

        Assert.Equal(365, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/tokens/settings", Ct)).GetProperty("maxDays").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/tokens/settings", new { maxDays = 7 }, Ct)).StatusCode);
        Assert.Equal("days", await FieldAsync(CreateAsync(admin, [Scope(null, "Read")], days: 8)));
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(admin, [Scope(null, "Read")], days: 7)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/tokens/settings", new { maxDays = 0 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Token_OfAnAdmin_HasNoAdminPower()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var hidden = await SpaceAsync(admin, "Hidden", sensitive: true);
        var (token, info, _) = await TokenAsync(admin, [Scope(null, "Write")]);

        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/admin/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/admin/storage", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/v1/spaces", new { name = "Mine" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(token, [Scope(null, "Write")])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/tokens", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync($"/api/v1/tokens/{info.GetProperty("id").GetString()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync($"/api/v1/spaces/{hidden}/emergency-access", new { reason = "let me in please" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsync("/api/v1/auth/signout", null, Ct)).StatusCode);
    }

    /// <summary>
    /// A route is closed to tokens unless it is on the list. Adding a route to the app leaves it closed;
    /// opening one means adding it here too, where a reviewer sees it.
    /// </summary>
    [Fact]
    public async Task EveryRoute_IsClosedToTokens_UnlessListed()
    {
        string[] open =
        [
            "GET /api/v1/me", "GET /api/v1/spaces", "GET /api/v1/spaces/{id:guid}", "GET /api/v1/spaces/{id:guid}/tree", "GET /api/v1/home", "GET /api/v1/search",
            "GET /api/v1/pages/{id:guid}", "GET /api/v1/pages/{id:guid}/diff", "GET /api/v1/pages/{id:guid}/markdown", "GET /api/v1/pages/{id:guid}/versions",
            "GET /api/v1/pages/{id:guid}/versions/{version}", "GET /api/v1/pages/{id:guid}/export", "GET /api/v1/pages/{id:guid}/export/code/{n:int}",
            "GET /api/v1/pages/{id:guid}/export/table/{n:int}", "GET /api/v1/images/{id:guid}", "GET /api/v1/attachments/{id:guid}",
            "GET /api/v1/brand", "GET /api/v1/brand/logo/{variant}",
            "POST /api/v1/spaces/{id:guid}/pages", "PUT /api/v1/pages/{id:guid}", "POST /api/v1/pages/{id:guid}/move", "DELETE /api/v1/pages/{id:guid}",
            "POST /api/v1/pages/{id:guid}/versions/{version}/restore", "POST /api/v1/pages/{id:guid}/attachments", "DELETE /api/v1/attachments/{id:guid}",
            "POST /api/v1/spaces/{id:guid}/images", "POST /api/v1/spaces/{id:guid}/images/import",
            "POST /api/v1/inventory/sync",
        ];
        var admin = await _f.SignedInBreakGlassAsync();
        var (token, _, _) = await TokenAsync(admin, [Scope(null, "Write")]);
        var routes = _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Template: e.RoutePattern.RawText!)))
            .Distinct().ToList();
        Assert.True(routes.Count > 60, "the route list was not read");
        Assert.Empty(open.Except(routes.Select(r => $"{r.Method} {r.Template}"))); // every listed route exists

        var closed = routes.Where(r => !open.Contains($"{r.Method} {r.Template}")).ToList();
        Assert.Contains(("GET", "/api/v1/admin/users"), closed);
        // This class allows a token 100 requests a minute, and there are more closed routes than that: a fresh token every 90.
        for (var i = 0; i < closed.Count; i++)
        {
            var (method, template) = closed[i];
            if (i > 0 && i % 90 == 0) (token, _, _) = await TokenAsync(admin, [Scope(null, "Write")]);
            var path = System.Text.RegularExpressions.Regex.Replace(template, @"\{[a-zA-Z]+:guid\}", Guid.NewGuid().ToString());
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", "1");
            using var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
            var r = await token.SendAsync(req, Ct);
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{method} {template} answered {(int)r.StatusCode} to a token");
        }
    }

    [Fact]
    public async Task ServiceToken_AdminOnly_ActsFromItsScopesAlone()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var desk = await SpaceAsync(admin, "Desk");
        var ops = await SpaceAsync(admin, "Ops");
        var hidden = await SpaceAsync(admin, "Hidden", sensitive: true);
        var (user, _) = await UserAsync("tech", desk, "Owner");
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(user, [Scope(desk, "Read")], kind: "Service")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(admin, [Scope(hidden, "Read")], kind: "Service")).StatusCode);

        var r = await CreateAsync(admin, [Scope(desk, "Write"), Scope(ops, "Read")], kind: "Service", name: "Service desk");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = Bearer(body.GetProperty("token").GetString()!);

        var spaces = await token.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct);
        Assert.Equal([("Desk", "Contributor"), ("Ops", "Reader")], spaces!.Select(s => (s.Name, s.MyRole!)));
        var page = await PageAsync(token, desk, "Ticket fix");
        Assert.Equal("Service desk", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct)).GetProperty("updatedBy").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync($"/api/v1/spaces/{ops}/pages", new { kind = "Page", title = "No", contentJson = Doc }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/v1/spaces/{hidden}", Ct)).StatusCode);

        // The account cannot sign in, shows as a service account, and a second token can be made for it.
        var account = body.GetProperty("info").GetProperty("ownerId").GetGuid();
        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", Ct);
        Assert.Equal("Service", users!.Single(u => u.Id == account).Source);
        var again = await CreateAsync(admin, [Scope(null, "Read")], kind: "Service", name: "ignored", accountId: account);
        Assert.Equal(account, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("info").GetProperty("ownerId").GetGuid());
        var global = Bearer((await (await CreateAsync(admin, [Scope(null, "Read")], kind: "Service", name: "Reader")).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!);
        Assert.Equal(["Desk", "Ops"], (await global.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct))!.Select(s => s.Name)); // global never reaches a sensitive space
    }

    [Fact]
    public async Task Tokens_AreListedAndRevokedByTheirOwner_AndByAdmins()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var (user, _) = await UserAsync("tech");
        var (other, _) = await UserAsync("other");
        var (_, info, _) = await TokenAsync(user, [Scope(null, "Read")]);
        var id = info.GetProperty("id").GetString();

        Assert.Equal("[]", await other.GetStringAsync("/api/v1/tokens", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/tokens/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/admin/tokens", Ct)).StatusCode);
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/tokens", Ct);
        Assert.Equal("tech", all.EnumerateArray().Single().GetProperty("owner").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tokens/{id}", Ct)).StatusCode);
        Assert.Equal("Revoked", (await user.GetFromJsonAsync<JsonElement>("/api/v1/tokens", Ct)).EnumerateArray().Single().GetProperty("state").GetString());
    }

    [Fact]
    public async Task Use_Create_Revoke_AndRefusal_AreAudited()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var (token, info, text) = await TokenAsync(admin, [Scope(null, "Read")]);
        var id = info.GetProperty("id").GetString()!;
        await token.GetAsync("/api/v1/search?q=secret-words", Ct);
        Assert.NotEqual(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/tokens", Ct))[0].GetProperty("lastUsedAt").ValueKind);
        await admin.DeleteAsync($"/api/v1/tokens/{id}", Ct);
        await token.GetAsync("/api/v1/me", Ct);                              // revoked: refused
        await Bearer("kbt_" + new string('a', 43) + "_aaaaaa").GetAsync("/api/v1/me", Ct); // never existed: not written, so the log cannot be flooded

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action.StartsWith("token.")).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(["token.create", "token.use", "token.revoke", "token.refused"], entries.Select(e => e.Action));
            Assert.All(entries, e => { Assert.Equal(id, e.TargetId); Assert.DoesNotContain(text[12..], e.DataJson); });
            var use = JsonSerializer.Deserialize<Dictionary<string, string>>(entries[1].DataJson)!;
            Assert.Equal(("GET", "/api/v1/search"), (use["method"], use["route"]));
            Assert.DoesNotContain("secret-words", entries[1].DataJson);
            Assert.Equal("revoked", JsonSerializer.Deserialize<Dictionary<string, string>>(entries[3].DataJson)!["reason"]);
        });
    }

    [Fact]
    public async Task Token_OverItsRateLimit_Gets429_OthersDoNot()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var (busy, _, _) = await TokenAsync(admin, [Scope(null, "Read")]);
        var (calm, _, _) = await TokenAsync(admin, [Scope(null, "Read")]);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 105; i++) codes.Add((await busy.GetAsync("/api/v1/me", Ct)).StatusCode);

        Assert.Equal(100, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.OK, (await calm.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me", Ct)).StatusCode);
    }
}
