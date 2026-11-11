using System.Net;
using System.Net.Http.Json;
using Kb.Web;

namespace Kb.Tests.Integration;

public sealed class AdminTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task Users_ListsAccounts_ForAdmins()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await admin.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = "tech1", displayName = "Tech One" }, Ct);
        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", Ct);
        Assert.Contains(users!, u => u.UserName == "breakglass" && u.IsBreakGlass && u.IsAdmin);
        Assert.Contains(users!, u => u.UserName == "tech1" && !u.IsAdmin && u.DisplayName == "Tech One");
    }

    [Fact]
    public async Task Users_Anonymous_Returns401()
    {
        var r = await _f.NewClient().GetAsync("/api/v1/admin/users", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task AuditLog_ListsNewestFirst_WithActorName()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct);
        var entries = await admin.GetFromJsonAsync<List<AuditEntryDto>>("/api/v1/admin/audit?take=5", Ct);
        Assert.Equal("space.create", entries![0].Action);
        Assert.Equal("Break-glass administrator", entries[0].Actor);
        Assert.True(entries.Count <= 5);
    }
}
