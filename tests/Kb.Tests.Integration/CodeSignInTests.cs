using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// One-time codes at sign-in: after the password, a person who must use codes and has none sets them up (no session until done);
/// later sign-ins take a code or a single-use recovery code. Admins always; everyone when the setting says so; anyone by choice.
/// </summary>
public sealed class CodeSignInTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _breakGlass = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _breakGlass = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> NewUserAsync(string name, bool admin = false)
    {
        await using var scope = _f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = name, DisplayName = name, IsAdmin = admin, CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private static async Task<(HttpStatusCode Status, string? Step)> PasswordAsync(HttpClient c, string name, string password = Password)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password }, Ct);
        return (r.StatusCode, r.StatusCode == HttpStatusCode.Conflict ? (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("step").GetString() : null);
    }

    private static async Task<string> KeyAsync(HttpClient c)
    {
        var r = await c.PostAsync("/api/v1/auth/codes/key", null, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("otpauth://totp/", body.GetProperty("uri").GetString(), StringComparison.Ordinal);
        return body.GetProperty("key").GetString()!;
    }

    /// <summary>Sets up codes for an Admin on a fresh client and returns the key and the recovery codes.</summary>
    private async Task<(string Key, string[] Recovery)> EnrollAsync(string name)
    {
        var c = _f.NewClient();
        Assert.Equal((HttpStatusCode.Conflict, "enroll"), await PasswordAsync(c, name));
        var key = await KeyAsync(c);
        var r = await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (key, [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!)]);
    }

    [Fact]
    public async Task Admin_FirstSignIn_SetsUpCodes_BeforeAnySession()
    {
        await NewUserAsync("ada", admin: true);
        var c = _f.NewClient();
        Assert.Equal((HttpStatusCode.Conflict, "enroll"), await PasswordAsync(c, "ada"));
        // The password alone opens nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal("[]", await c.GetStringAsync("/api/v1/spaces", Ct)); // answered as to someone not signed in

        var key = await KeyAsync(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = "000000" == Codes.Totp(key) ? "111111" : "000000" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);

        var r = await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(10, body.GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal("ada", body.GetProperty("me").GetProperty("userName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.CodesEnroll, Ct)));
    }

    [Fact]
    public async Task Enrolled_SignIn_TakesCode_OrRecoveryCode_OnlyOnce()
    {
        await NewUserAsync("bea", admin: true);
        var (key, recovery) = await EnrollAsync("bea");

        var c = _f.NewClient();
        Assert.Equal((HttpStatusCode.Conflict, "code"), await PasswordAsync(c, "bea"));
        // Setting up again is refused once codes are set up.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/local/code", new { code = Codes.Totp(key) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);

        var d = _f.NewClient();
        await PasswordAsync(d, "bea");
        Assert.Equal(HttpStatusCode.OK, (await d.PostAsJsonAsync("/api/v1/auth/local/code", new { code = recovery[0] }, Ct)).StatusCode);

        var e = _f.NewClient();
        await PasswordAsync(e, "bea");
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.PostAsJsonAsync("/api/v1/auth/local/code", new { code = recovery[0] }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Code_WithoutPassword_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/code", new { code = "123456" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().PostAsync("/api/v1/auth/codes/key", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().PostAsJsonAsync("/api/v1/auth/codes", new { code = "123456" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongCodes_LockTheAccount(bool recovery)
    {
        var name = recovery ? "cal" : "cy";
        await NewUserAsync(name, admin: true);
        var (key, _) = await EnrollAsync(name);
        var wrong = recovery ? "AAAAA-BBBBB" : (Codes.Totp(key) == "000000" ? "111111" : "000000");
        var c = _f.NewClient();
        await PasswordAsync(c, name);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/local/code", new { code = wrong }, Ct)).StatusCode);
        // Locked: the right code no longer works, and neither does the password.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/local/code", new { code = Codes.Totp(key) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(_f.NewClient(), name)).Status);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.CountAsync(a => a.Action == AuditActions.CodeFailed, Ct) >= 5));
    }

    [Fact]
    public async Task WrongSetupCodes_LockTheAccount()
    {
        await NewUserAsync("dan", admin: true);
        var c = _f.NewClient();
        await PasswordAsync(c, "dan");
        var key = await KeyAsync(c);
        var wrong = Codes.Totp(key) == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = wrong }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).StatusCode);
    }

    [Fact]
    public async Task NonAdmin_SignsInWithPassword_UntilEveryoneMustUseCodes()
    {
        await NewUserAsync("eve");
        Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(_f.NewClient(), "eve")).Status);
        Assert.Equal("admins", (await _breakGlass.GetFromJsonAsync<JsonElement>("/api/v1/admin/settings/codes", Ct)).GetProperty("requiredFor").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _breakGlass.PutAsJsonAsync("/api/v1/admin/settings/codes", new { requiredFor = "nobody" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PutAsJsonAsync("/api/v1/admin/settings/codes", new { requiredFor = "everyone" }, Ct)).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "enroll"), await PasswordAsync(_f.NewClient(), "eve"));
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SettingsChange && a.TargetId == RuntimeSettingKeys.CodesRequiredFor, Ct)));
    }

    [Fact]
    public async Task OnlyAnAdmin_ChangesTheSetting()
    {
        await NewUserAsync("fay");
        var c = _f.NewClient();
        await PasswordAsync(c, "fay");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/v1/admin/settings/codes", new { requiredFor = "everyone" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anyone_TurnsOnCodes_ForTheirOwnAccount_AndOff()
    {
        await NewUserAsync("gus");
        var c = _f.NewClient();
        await PasswordAsync(c, "gus");
        var mine = await c.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct);
        Assert.False(mine.GetProperty("on").GetBoolean());
        Assert.False(mine.GetProperty("required").GetBoolean());

        var key = await KeyAsync(c);
        var r = await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // The session that turned codes on stays signed in.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct)).GetProperty("on").GetBoolean());
        Assert.Equal((HttpStatusCode.Conflict, "code"), await PasswordAsync(_f.NewClient(), "gus"));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/me/codes", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(_f.NewClient(), "gus")).Status);
    }

    [Fact]
    public async Task Admin_CannotTurnOffCodes()
    {
        await NewUserAsync("hal", admin: true);
        await EnrollAsync("hal");
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await Codes.SignInAsync(_f, c, "hal", Password)).StatusCode);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct)).GetProperty("required").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync("/api/v1/me/codes", Ct)).StatusCode);
    }

    [Fact]
    public async Task AdminReset_MakesThePersonSetUpAgain_AndIsAudited()
    {
        var id = await NewUserAsync("ivy", admin: true);
        var (key, _) = await EnrollAsync("ivy");
        var session = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await Codes.SignInAsync(_f, session, "ivy", Password)).StatusCode);

        await NewUserAsync("jon");
        var jon = _f.NewClient();
        await PasswordAsync(jon, "jon");
        Assert.Equal(HttpStatusCode.Forbidden, (await jon.PostAsync($"/api/v1/admin/users/{id}/codes/reset", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PostAsync($"/api/v1/admin/users/{id}/codes/reset", null, Ct)).StatusCode);
        // Their sessions end, and the old key no longer works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "enroll"), await PasswordAsync(_f.NewClient(), "ivy"));
        Assert.Equal(HttpStatusCode.NotFound, (await _breakGlass.PostAsync($"/api/v1/admin/users/{Guid.NewGuid()}/codes/reset", null, Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.UserCodesReset && a.TargetId == id.ToString(), Ct)));
        _ = key;
    }

    [Fact]
    public async Task ExemptAccount_NeverUsesCodes_AndCannotTurnThemOn()
    {
        var id = await NewUserAsync("demo");
        Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PutAsJsonAsync($"/api/v1/admin/users/{id}/codes/exempt", new { exempt = true }, Ct)).StatusCode);
        Assert.True((await _breakGlass.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct)).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).GetProperty("codesExempt").GetBoolean());
        await _breakGlass.PutAsJsonAsync("/api/v1/admin/settings/codes", new { requiredFor = "everyone" }, Ct);

        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(c, "demo")).Status);
        var mine = await c.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct);
        Assert.False(mine.GetProperty("required").GetBoolean());
        Assert.False(mine.GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = "123456" }, Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.UserCodesExempt && a.TargetId == id.ToString(), Ct)));

        // Taken back: the setting applies again.
        Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PutAsJsonAsync($"/api/v1/admin/users/{id}/codes/exempt", new { exempt = false }, Ct)).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "enroll"), await PasswordAsync(_f.NewClient(), "demo"));
    }

    [Fact]
    public async Task Exempt_IsRefusedForAnAdmin_AndOnlyAnAdminSetsIt()
    {
        var admin = await NewUserAsync("lea", admin: true);
        Assert.Equal(HttpStatusCode.Conflict, (await _breakGlass.PutAsJsonAsync($"/api/v1/admin/users/{admin}/codes/exempt", new { exempt = true }, Ct)).StatusCode);
        var id = await NewUserAsync("max");
        var c = _f.NewClient();
        await PasswordAsync(c, "max");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"/api/v1/admin/users/{id}/codes/exempt", new { exempt = true }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Exempting_ClearsCodes_ThePersonHad()
    {
        var id = await NewUserAsync("ned");
        var c = _f.NewClient();
        await PasswordAsync(c, "ned");
        var key = await KeyAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PutAsJsonAsync($"/api/v1/admin/users/{id}/codes/exempt", new { exempt = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(_f.NewClient(), "ned")).Status);
    }

    [Fact]
    public async Task BreakGlass_NeedsNoCode()
    {
        Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(_f.NewClient(), "breakglass", KbAppFactory.BreakGlassPassword)).Status);
        var me = await _breakGlass.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct);
        Assert.False(me.GetProperty("required").GetBoolean());
        Assert.False(me.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task TokenCaller_CannotTouchCodes()
    {
        await NewUserAsync("kim");
        var c = _f.NewClient();
        await PasswordAsync(c, "kim");
        var r = await c.PostAsJsonAsync("/api/v1/tokens", new { name = "t", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Write" } }, days = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var made = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var t = _f.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        t.DefaultRequestHeaders.Authorization = new("Bearer", made.GetProperty("token").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await t.PostAsync("/api/v1/auth/codes/key", null, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, (await t.DeleteAsync("/api/v1/me/codes", Ct)).StatusCode);
    }
}
