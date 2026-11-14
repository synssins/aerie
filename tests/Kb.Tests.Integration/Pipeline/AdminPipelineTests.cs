using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The administration routes go through the action bus: each answers with its action's name, only an Admin gets past the handler, and a plugin can refuse a write.</summary>
public sealed class AdminPipelineTests : IAsyncLifetime
{
    private const string Admin = "/api/v1/admin";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync($"{Admin}/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private static Task<HttpResponseMessage> InviteAsync(HttpClient c, string userName, bool isAdmin = false) =>
        c.PostAsJsonAsync($"{Admin}/users/invite", new { userName, displayName = "Tech One", isAdmin }, Ct);

    /// <summary>Every route of the slice, sent well formed, with the name of its action.</summary>
    private static async Task<List<(string Name, HttpResponseMessage Answer)>> AllAsync(HttpClient c) =>
    [
        ("audit.verify", await c.GetAsync($"{Admin}/audit/verify", Ct)),
        ("audit.list", await c.GetAsync($"{Admin}/audit?take=5", Ct)),
        ("user.list", await c.GetAsync($"{Admin}/users", Ct)),
        ("user.invite", await InviteAsync(c, "tech1")),
        ("auth.local.disable", await c.PostAsync($"{Admin}/auth/local/disable", null, Ct)),
        ("settings.codes.set", await c.PutAsJsonAsync($"{Admin}/settings/codes", new { requiredFor = "everyone" }, Ct)),
    ];

    private async Task<HttpClient> UserAsync()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task EveryAdminRoute_AnswersWithItsActionName()
    {
        var answers = await AllAsync(_admin);

        Assert.All(answers, a => Assert.Equal(a.Name, Action(a.Answer)));
        // The answers are what they were. A refusal by the core (409: no external Admin has signed in) names the action too.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.NoContent], answers.Select(a => a.Answer.StatusCode));
        var verify = await answers[0].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(verify.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, verify.GetProperty("firstBrokenSequence").ValueKind);
        Assert.NotEmpty((await answers[1].Answer.Content.ReadFromJsonAsync<List<AuditEntryDto>>(Ct))!);
        Assert.Contains((await answers[2].Answer.Content.ReadFromJsonAsync<List<AdminUserDto>>(Ct))!, u => u.UserName == "breakglass");
        var invited = await answers[3].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["id", "setPasswordToken"], invited.EnumerateObject().Select(p => p.Name));
        Assert.NotEqual("", invited.GetProperty("setPasswordToken").GetString());
        Assert.Equal("external-admin-not-verified", (await answers[4].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EveryAdminRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var answers = await AllAsync(await UserAsync());

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.UserName == "tech1", Ct));
            Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == RuntimeSettingKeys.CodesRequiredFor, Ct));
        });
    }

    [Fact]
    public async Task DisableLocalSignIn_RefusedByHook_Is422_NothingStoredNoAuditEntry()
    {
        await SetAsync("Refuse", "auth.local.disable");

        var r = await _admin.PostAsync($"{Admin}/auth/local/disable", null, Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == RuntimeSettingKeys.LocalSignInDisabled, Ct));
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SettingsChange, Ct));
        });
    }

    /// <summary>An invite answers the token that sets the new account's password, so it is core-only: the probe's refusal does not reach it.</summary>
    [Fact]
    public async Task Invite_IsNotOpenToPlugins()
    {
        await SetAsync("Refuse", "user.invite");

        Assert.Equal(HttpStatusCode.OK, (await InviteAsync(_admin, "tech1")).StatusCode);
    }

    [Fact]
    public async Task Writes_AreAudited_AsTheCaller_WithoutTheToken()
    {
        var invited = await (await InviteAsync(_admin, "boss", isAdmin: true)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        (await _admin.PutAsJsonAsync($"{Admin}/settings/codes", new { requiredFor = "everyone" }, Ct)).EnsureSuccessStatusCode();
        var bad = await InviteAsync(_admin, " ");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True((await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("userName", out _));
        var again = await InviteAsync(_admin, "boss"); // the name is taken: the user manager's own messages, under "user"
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("user").GetArrayLength() > 0);

        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var invite = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.UserInvite, Ct);
            Assert.Equal((me, "user", invited.GetProperty("id").GetString(), """{"isAdmin":"True"}"""), (invite.ActorId, invite.TargetType, invite.TargetId, invite.DataJson));
            Assert.DoesNotContain(invited.GetProperty("setPasswordToken").GetString()!, invite.DataJson, StringComparison.Ordinal);
            var setting = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.SettingsChange, Ct);
            Assert.Equal((me, "setting", RuntimeSettingKeys.CodesRequiredFor, """{"value":"everyone"}"""), (setting.ActorId, setting.TargetType, setting.TargetId, setting.DataJson));
            var stored = await db.RuntimeSettings.SingleAsync(s => s.Key == RuntimeSettingKeys.CodesRequiredFor, Ct);
            Assert.Equal(("everyone", me), (stored.Value, stored.UpdatedBy));
            Assert.True((await db.Users.SingleAsync(u => u.UserName == "boss", Ct)).IsAdmin);
        });
    }
}
