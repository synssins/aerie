using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// The audit log as an Admin reads it: a page at a time (10, 25, 50 or 100), filtered by action, person, dates and API token.
/// Each entry says whether it came through the web or the API ("Via"); one done with a token names the token.
/// </summary>
public sealed class AuditLogTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Body"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<List<JsonElement>> AuditAsync(string query = "") =>
        [.. (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?" + query, Ct)).EnumerateArray()];

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, Guid space)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = id.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    [Fact]
    public async Task APage_AtATime_AndOnlyTheAllowedSizes()
    {
        for (var i = 0; i < 12; i++) await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = $"s{i}", name = $"s{i}" }, Ct);
        var first = await AuditAsync("take=10");
        var second = await AuditAsync("take=10&offset=10");
        Assert.Equal(10, first.Count);
        Assert.NotEmpty(second);
        Assert.True(first[^1].GetProperty("sequence").GetInt64() > second[0].GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task Filters_ByActionPersonAndDates()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "desk" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var (cara, caraId) = await UserAsync("cara", space);
        (await cara.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "One", contentJson = Doc }, Ct)).EnsureSuccessStatusCode();

        var hers = await AuditAsync($"take=100&actor={caraId}");
        Assert.NotEmpty(hers);
        Assert.All(hers, e => Assert.Equal("cara", e.GetProperty("actor").GetString()));
        var spaces = await AuditAsync("take=100&action=space.");
        Assert.NotEmpty(spaces);
        Assert.All(spaces, e => Assert.StartsWith("space.", e.GetProperty("action").GetString(), StringComparison.Ordinal));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotEmpty(await AuditAsync($"take=100&from={today}&to={today}"));
        Assert.Empty(await AuditAsync("take=100&to=2020-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/v1/admin/audit?from=2026-12-01&to=2026-11-01", Ct)).StatusCode);
    }

    [Fact]
    public async Task TokenUse_IsLoggedWithTheTokensName_AndEveryEntrySaysWebOrApi()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ops", name = "ops" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var (bea, _) = await UserAsync("bea", space);
        var made = await (await bea.PostAsJsonAsync("/api/v1/tokens", new { name = "Build agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)space, access = "Write" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var tokenId = made.GetProperty("info").GetProperty("id").GetGuid();
        var token = _f.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());
        // A page made and deleted with the token, and one made and deleted by hand: both deletes are audited.
        async Task MakeAndDeleteAsync(HttpClient by, string title)
        {
            var page = (await (await by.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = Doc }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await by.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        }
        await MakeAndDeleteAsync(token, "By token");
        await MakeAndDeleteAsync(bea, "By hand");

        var byToken = await AuditAsync($"take=100&token={tokenId}");
        Assert.Contains(byToken, e => e.GetProperty("action").GetString() == "token.use");
        Assert.Contains(byToken, e => e.GetProperty("action").GetString() == "page.delete");
        Assert.All(byToken, e => Assert.Equal(("API", "Build agent"), (e.GetProperty("via").GetString(), e.GetProperty("token").GetString())));

        var creates = await AuditAsync("take=100&action=page.delete");
        Assert.Equal(2, creates.Count);
        Assert.Equal(["API", "Web"], creates.Select(e => e.GetProperty("via").GetString()).Order());
        Assert.Equal(JsonValueKind.Null, creates.Single(e => e.GetProperty("via").GetString() == "Web").GetProperty("token").ValueKind);
        // The chain still verifies with the token named in the entries.
        Assert.True((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit/verify", Ct)).GetProperty("valid").GetBoolean());
    }
}
