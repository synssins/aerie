using System.Net;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Web;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class SettingsTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<HttpClient> OwnerOfAsync(Guid spaceId)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            var u = new KbUser { UserName = "owner1", DisplayName = "Owner One", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await users.CreateAsync(u, "user-password-0123456789")).Succeeded);
            id = u.Id;
        }
        var admin = await _f.SignedInBreakGlassAsync();
        await admin.PutAsJsonAsync($"/api/v1/spaces/{spaceId}/roles", new { principalType = "User", principalId = id.ToString(), role = "Owner" }, Ct);
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "owner1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private async Task<(HttpClient Admin, SpaceDto Space)> SpaceAsync(bool sensitive = false)
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var s = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk", isSensitive = sensitive }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        return (admin, s!);
    }

    [Fact]
    public async Task Owner_UpdatesDescriptionAndSpaceSettings()
    {
        var (_, s) = await SpaceAsync();
        var owner = await OwnerOfAsync(s.Id);
        var r = await owner.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}",
            new { description = "Desk", commentsEnabled = false, agingDays = 90, agingAudience = "Supervisors", recycleDays = 30 }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<SpaceDto>(Ct);
        Assert.Equal("Desk", dto!.Description);
        Assert.False(dto.CommentsEnabled);
        Assert.Equal(90, dto.AgingDays);
        Assert.Equal("Supervisors", dto.AgingAudience);
        Assert.Equal(30, dto.RecycleDays);
    }

    [Fact]
    public async Task Owner_CannotSetSensitiveOrPublic()
    {
        var (_, s) = await SpaceAsync();
        var owner = await OwnerOfAsync(s.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { isSensitive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { isPublic = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task SpaceSettings_OutOfRange_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        var r = await admin.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { agingDays = 0 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Admin_ListsAndConfiguresSensitiveSpace_WithoutARole()
    {
        var (admin, s) = await SpaceAsync(sensitive: true);
        var all = await admin.GetFromJsonAsync<List<SpaceDto>>("/api/v1/admin/spaces", Ct);
        Assert.Contains(all!, x => x.Id == s.Id && x.IsSensitive);

        var r = await admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{s.Id}", new { name = "HR", isSensitive = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await r.Content.ReadFromJsonAsync<SpaceDto>(Ct))!.IsSensitive);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SpaceSensitiveSet, Ct)));
    }

    [Fact]
    public async Task AdminSpaces_NonAdmin_Returns403()
    {
        var (_, s) = await SpaceAsync();
        var owner = await OwnerOfAsync(s.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/admin/spaces", Ct)).StatusCode);
    }

    [Fact]
    public async Task UserSettings_DefaultsThenRoundTrip()
    {
        var c = await _f.SignedInBreakGlassAsync();
        var d = await c.GetFromJsonAsync<UserSettings>("/api/v1/me/settings", Ct);
        Assert.Equal("system", d!.Theme);
        Assert.Equal("boxed", d.Width);

        var put = await c.PutAsJsonAsync("/api/v1/me/settings", new
        {
            theme = "dark", width = "full", startPage = "last", codeLanguage = "powershell",
            notify = new Dictionary<string, object> { ["access.decision"] = new { inApp = true, email = false } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = await c.GetFromJsonAsync<UserSettings>("/api/v1/me/settings", Ct);
        Assert.Equal("dark", got!.Theme);
        Assert.Equal("powershell", got.CodeLanguage);
        Assert.False(got.Notify["access.decision"].Email);
    }

    [Fact]
    public async Task UserSettings_InvalidTheme_Returns400()
    {
        var c = await _f.SignedInBreakGlassAsync();
        var r = await c.PutAsJsonAsync("/api/v1/me/settings", new { theme = "neon", width = "boxed", startPage = "last", codeLanguage = "plaintext" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
