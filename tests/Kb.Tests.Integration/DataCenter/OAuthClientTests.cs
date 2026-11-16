using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 4: registering OAuth clients and the compatible sign-in settings (identity.md, Clients, Redirect URI rules, Tokens).</summary>
public sealed class OAuthClientTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private Task<HttpResponseMessage> CreateAsync(object body) => _admin.PostAsJsonAsync("/api/v1/admin/oauth/clients", body, Ct);

    private static object Confidential(params string[] uris) =>
        new { name = "Desk app", type = "confidential", redirectUris = uris.Length == 0 ? ["https://app.example/cb"] : uris, scopes = new[] { "READ", "WRITE" } };

    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.StatusCode == expected, $"expected {(int)expected}, got {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // auth.client.register: a confidential client gets a client id and a secret, stored only as a hash.
    [Fact]
    public async Task Register_Confidential_GivesIdAndSecretOnce()
    {
        var made = await Json(await CreateAsync(Confidential()), HttpStatusCode.Created);
        var secret = made.GetProperty("clientSecret").GetString()!;
        var client = made.GetProperty("client");
        Assert.Matches("^[A-Za-z0-9]{64}$", secret);
        Assert.Matches("^[A-Za-z0-9]{32}$", client.GetProperty("clientId").GetString()!);
        Assert.Equal(["READ", "WRITE"], client.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.True(client.GetProperty("requirePkce").GetBoolean());
        Assert.True(client.GetProperty("enabled").GetBoolean());
        Assert.Equal("confidential", client.GetProperty("type").GetString());
        await _f.WithDbAsync(async db =>
        {
            var row = await db.OAuthClients.SingleAsync(Ct);
            Assert.NotEqual(secret, row.SecretHash);
            var hasher = _f.Services.GetRequiredService<IPasswordHasher<KbUser>>();
            Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new KbUser(), row.SecretHash!, secret));
            var audit = await db.AuditEntries.Where(a => a.Action == AuditActions.OAuthClientCreate).SingleAsync(Ct);
            Assert.Contains(row.ClientId, audit.DataJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, string.Join('|', await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct)), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Register_Public_HasNoSecret_AndAlwaysPkce()
    {
        var made = await Json(await CreateAsync(new { name = "CLI", type = "public", redirectUris = new[] { "http://127.0.0.1/cb" }, scopes = new[] { "READ" } }), HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, made.GetProperty("clientSecret").ValueKind);
        await Json(await CreateAsync(new { name = "CLI", type = "public", redirectUris = new[] { "http://127.0.0.1/cb" }, scopes = new[] { "READ" }, requirePkce = false }), HttpStatusCode.BadRequest);
        // A confidential client may turn it off, for an older integration.
        Assert.False((await Json(await CreateAsync(new { name = "Old", type = "confidential", redirectUris = new[] { "https://old.example/cb" }, scopes = new[] { "READ" }, requirePkce = false }), HttpStatusCode.Created))
            .GetProperty("client").GetProperty("requirePkce").GetBoolean());
    }

    // auth.client.register.non_admin
    [Fact]
    public async Task OnlyAdmins_Register()
    {
        await UserAsync(_f, "ravi");
        var ravi = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await ravi.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ravi", password = Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ravi.PostAsJsonAsync("/api/v1/admin/oauth/clients", Confidential(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ravi.GetAsync("/api/v1/admin/oauth/clients", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ravi.GetAsync("/api/v1/admin/auth/compat", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/admin/oauth/clients", Ct)).StatusCode);
    }

    // auth.client.redirect.rejects, and what is accepted.
    [Theory]
    [InlineData("http://example.com/cb")]
    [InlineData("https://app.example/cb#frag")]
    [InlineData("https://*.example/cb")]
    [InlineData("https://app.example/*")]
    [InlineData("/cb")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("")]
    public async Task RedirectUris_AreRefused(string uri) => await Json(await CreateAsync(Confidential(uri)), HttpStatusCode.BadRequest);

    [Theory]
    [InlineData("https://app.example/cb?x=1")]
    [InlineData("http://127.0.0.1/cb")]
    [InlineData("http://localhost:8080/cb")]
    [InlineData("http://[::1]/cb")]
    [InlineData("com.example.app:/callback")]
    public async Task RedirectUris_AreAccepted(string uri) => await Json(await CreateAsync(Confidential(uri)), HttpStatusCode.Created);

    [Fact]
    public async Task Scopes_AreTheFourAndAtLeastOne()
    {
        foreach (var scopes in new[] { Array.Empty<string>(), ["READ_ALL"], ["JSM_KB"], ["read"] })
            await Json(await CreateAsync(new { name = "S", type = "confidential", redirectUris = new[] { "https://app.example/cb" }, scopes }), HttpStatusCode.BadRequest);
        await Json(await CreateAsync(new { name = "", type = "confidential", redirectUris = new[] { "https://app.example/cb" }, scopes = new[] { "READ" } }), HttpStatusCode.BadRequest);
        await Json(await CreateAsync(new { name = "S", type = "other", redirectUris = new[] { "https://app.example/cb" }, scopes = new[] { "READ" } }), HttpStatusCode.BadRequest);
        Assert.Equal(["READ", "ADMIN", "SYSTEM_ADMIN"], (await Json(await CreateAsync(new { name = "S", type = "confidential", redirectUris = new[] { "https://app.example/cb" }, scopes = new[] { "SYSTEM_ADMIN", "READ", "ADMIN" } }), HttpStatusCode.Created))
            .GetProperty("client").GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    // auth.client.secret_not_readable
    [Fact]
    public async Task TheSecret_IsInNoReadOrList()
    {
        var made = await Json(await CreateAsync(Confidential()), HttpStatusCode.Created);
        var (secret, id) = (made.GetProperty("clientSecret").GetString()!, made.GetProperty("client").GetProperty("id").GetString());
        foreach (var path in new[] { "/api/v1/admin/oauth/clients", $"/api/v1/admin/oauth/clients/{id}" })
        {
            var text = await (await _admin.GetAsync(path, Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("ecret\"", text.Replace("secretRotatedAt", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rotate_GivesANewSecret_OnlyForConfidential()
    {
        var made = await Json(await CreateAsync(Confidential()), HttpStatusCode.Created);
        var id = made.GetProperty("client").GetProperty("id").GetString();
        var rotated = await Json(await _admin.PostAsync($"/api/v1/admin/oauth/clients/{id}/secret", null, Ct), HttpStatusCode.OK);
        Assert.NotEqual(made.GetProperty("clientSecret").GetString(), rotated.GetProperty("clientSecret").GetString());
        Assert.NotEqual(JsonValueKind.Null, rotated.GetProperty("client").GetProperty("secretRotatedAt").ValueKind);
        var pub = (await Json(await CreateAsync(new { name = "CLI", type = "public", redirectUris = new[] { "http://127.0.0.1/cb" }, scopes = new[] { "READ" } }), HttpStatusCode.Created)).GetProperty("client").GetProperty("id").GetString();
        await Json(await _admin.PostAsync($"/api/v1/admin/oauth/clients/{pub}/secret", null, Ct), HttpStatusCode.BadRequest);
        await Json(await _admin.PostAsync($"/api/v1/admin/oauth/clients/{Guid.NewGuid()}/secret", null, Ct), HttpStatusCode.NotFound);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthClientSecretRotate, Ct)));
    }

    /// <summary>Edits keep the type; removing a scope ends the tokens that hold it; disabling is a field.</summary>
    [Fact]
    public async Task Update_KeepsTheType_AndRemovingAScopeEndsItsTokens()
    {
        var id = (await Json(await CreateAsync(Confidential()), HttpStatusCode.Created)).GetProperty("client").GetProperty("id").GetGuid();
        var ravi = await UserAsync(_f, "ravi");
        var (readFamily, writeFamily) = (Guid.NewGuid(), Guid.NewGuid());
        await _f.WithDbAsync(async db =>
        {
            var grant = new OAuthGrant { UserId = ravi, ClientId = id, Scopes = "READ WRITE", CreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow };
            db.AddRange(grant, new OAuthTokenFamily { Id = readFamily, GrantId = grant.Id, Scopes = "READ" }, new OAuthTokenFamily { Id = writeFamily, GrantId = grant.Id, Scopes = "READ WRITE" });
            await db.SaveChangesAsync(Ct);
        });
        await Json(await _admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{id}", new { type = "public" }, Ct), HttpStatusCode.BadRequest);
        var updated = await Json(await _admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{id}", new { name = "Renamed", scopes = new[] { "READ" }, enabled = false }, Ct), HttpStatusCode.OK);
        Assert.Equal(("Renamed", false, 1), (updated.GetProperty("name").GetString(), updated.GetProperty("enabled").GetBoolean(), updated.GetProperty("activeGrants").GetInt32()));
        Assert.Equal(["https://app.example/cb"], updated.GetProperty("redirectUris").EnumerateArray().Select(s => s.GetString())); // left out, so kept
        await _f.WithDbAsync(async db =>
        {
            Assert.Null((await db.OAuthTokenFamilies.SingleAsync(f => f.Id == readFamily, Ct)).RevokedAt);
            Assert.NotNull((await db.OAuthTokenFamilies.SingleAsync(f => f.Id == writeFamily, Ct)).RevokedAt);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthClientUpdate, Ct));
        });
    }

    [Fact]
    public async Task Delete_RemovesTheClientAndItsGrants()
    {
        var id = (await Json(await CreateAsync(Confidential()), HttpStatusCode.Created)).GetProperty("client").GetProperty("id").GetGuid();
        var ravi = await UserAsync(_f, "ravi");
        await _f.WithDbAsync(async db =>
        {
            db.OAuthGrants.Add(new OAuthGrant { UserId = ravi, ClientId = id, Scopes = "READ" });
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/oauth/clients/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/v1/admin/oauth/clients/{id}", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.OAuthGrants.AnyAsync(Ct));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthClientDelete, Ct));
        });
    }

    [Fact]
    public async Task CompatSettings_ReadSaveAndRanges()
    {
        var d = await Json(await _admin.GetAsync("/api/v1/admin/auth/compat", Ct), HttpStatusCode.OK);
        Assert.Equal((true, false, 300, 3600, 90, JsonValueKind.Null), (d.GetProperty("basicPasswordEnabled").GetBoolean(), d.GetProperty("allowHttpRedirects").GetBoolean(),
            d.GetProperty("codeSeconds").GetInt32(), d.GetProperty("accessTokenSeconds").GetInt32(), d.GetProperty("refreshTokenDays").GetInt32(), d.GetProperty("grantMaxDays").ValueKind));
        foreach (var bad in new object[]
                 {
                     new { basicPasswordEnabled = true, allowHttpRedirects = false, codeSeconds = 601, accessTokenSeconds = 3600, refreshTokenDays = 90 },
                     new { basicPasswordEnabled = true, allowHttpRedirects = false, codeSeconds = 300, accessTokenSeconds = 200, refreshTokenDays = 90 },
                     new { basicPasswordEnabled = true, allowHttpRedirects = false, codeSeconds = 300, accessTokenSeconds = 3600, refreshTokenDays = 0 },
                     new { basicPasswordEnabled = true, allowHttpRedirects = false, codeSeconds = 300, accessTokenSeconds = 3600, refreshTokenDays = 90, grantMaxDays = 731 },
                 })
            await Json(await _admin.PutAsJsonAsync("/api/v1/admin/auth/compat", bad, Ct), HttpStatusCode.BadRequest);
        var saved = await Json(await _admin.PutAsJsonAsync("/api/v1/admin/auth/compat",
            new { basicPasswordEnabled = false, allowHttpRedirects = true, codeSeconds = 120, accessTokenSeconds = 600, refreshTokenDays = 30, grantMaxDays = 365 }, Ct), HttpStatusCode.OK);
        Assert.Equal(365, saved.GetProperty("grantMaxDays").GetInt32());
        var read = await Json(await _admin.GetAsync("/api/v1/admin/auth/compat", Ct), HttpStatusCode.OK);
        Assert.Equal((false, true, 120, 600, 30, 365), (read.GetProperty("basicPasswordEnabled").GetBoolean(), read.GetProperty("allowHttpRedirects").GetBoolean(),
            read.GetProperty("codeSeconds").GetInt32(), read.GetProperty("accessTokenSeconds").GetInt32(), read.GetProperty("refreshTokenDays").GetInt32(), read.GetProperty("grantMaxDays").GetInt32()));
        // The settings take effect: password Basic is off, and a plain http redirect URI can be registered on this test install.
        await UserAsync(_f, "ravi");
        await (await Basic(_f, "ravi").GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await Json(await CreateAsync(Confidential("http://example.com/cb")), HttpStatusCode.Created);
        // Clearing the absolute limit is saving it empty.
        await Json(await _admin.PutAsJsonAsync("/api/v1/admin/auth/compat",
            new { basicPasswordEnabled = true, allowHttpRedirects = false, codeSeconds = 300, accessTokenSeconds = 3600, refreshTokenDays = 90, grantMaxDays = (int?)null }, Ct), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, (await Json(await _admin.GetAsync("/api/v1/admin/auth/compat", Ct), HttpStatusCode.OK)).GetProperty("grantMaxDays").ValueKind);
    }
}
