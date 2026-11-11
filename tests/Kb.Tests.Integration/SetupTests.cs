using System.Net;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Web;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration;

public sealed class SetupTests
{
    private const string Pw = "first-admin-password-123";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string? code = null) => new { userName = "admin", displayName = "First Admin", password = Pw, code };

    [Fact]
    public async Task Status_RequiresSetup_OnFreshInstall_AndCodeWhenRemote()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var s = await f.NewClient().GetFromJsonAsync<SetupStatus>("/api/v1/setup/status", Ct);
        Assert.True(s!.Required);
        Assert.True(s.CodeRequired);
    }

    [Fact]
    public async Task Setup_Remote_WithoutCode_Returns403()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var r = await f.NewClient().PostAsJsonAsync("/api/v1/setup", Body(), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Setup_Remote_WithCode_CreatesAdmin_SignsIn_AndLocksSetup()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var c = f.NewClient();
        await c.GetAsync("/api/v1/setup/status", Ct); // app started, code written
        var code = (await File.ReadAllTextAsync(f.SetupCodeFile, Ct)).Trim();

        var r = await c.PostAsJsonAsync("/api/v1/setup", Body(code), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var me = await c.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);
        Assert.True(me!.IsAdmin);
        Assert.False(me.IsBreakGlass);
        Assert.False(File.Exists(f.SetupCodeFile));

        var s = await c.GetFromJsonAsync<SetupStatus>("/api/v1/setup/status", Ct);
        Assert.False(s!.Required);
        var again = await f.NewClient().PostAsJsonAsync("/api/v1/setup", Body(code), Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SetupAdminCreate, Ct)));
    }

    [Fact]
    public async Task Setup_Loopback_NeedsNoCode()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), loopback: true);
        var c = f.NewClient();
        var s = await c.GetFromJsonAsync<SetupStatus>("/api/v1/setup/status", Ct);
        Assert.False(s!.CodeRequired);
        var r = await c.PostAsJsonAsync("/api/v1/setup", Body(), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Setup_ShortPassword_Returns400()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), loopback: true);
        var r = await f.NewClient().PostAsJsonAsync("/api/v1/setup", new { userName = "admin", displayName = "A", password = "short" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task InstallerCredentials_CreateAdmin_AtStartup()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), breakGlass: false, settings: new Dictionary<string, string>
        {
            ["Kb:Setup:AdminUserName"] = "chris",
            ["Kb--SetupAdminPassword"] = Pw,
        });
        var c = f.NewClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "chris", password = Pw }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await c.GetFromJsonAsync<SetupStatus>("/api/v1/setup/status", Ct);
        Assert.False(s!.Required);
    }

    [Fact]
    public async Task BreakGlass_NotCreated_WhenSecretMissing()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), breakGlass: false);
        await f.NewClient().GetAsync("/health/live", Ct);
        await f.WithDbAsync(async db => Assert.False(await db.Users.AnyAsync(u => u.IsBreakGlass, Ct)));
    }
}
