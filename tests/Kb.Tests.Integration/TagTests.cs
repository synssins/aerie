using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>A tag lists every page that has it, in the spaces the caller may read, and only those.</summary>
public sealed class TagTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Body"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string key) =>
        (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

    private async Task PageAsync(Guid space, string title, params string[] tags) =>
        (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = Doc, tags }, Ct)).EnsureSuccessStatusCode();

    private static async Task<List<string>> TaggedAsync(HttpClient c, string tag) =>
        [.. (await c.GetFromJsonAsync<JsonElement>($"/api/v1/search?tag={tag}&limit=50", Ct)).GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).Order()];

    [Fact]
    public async Task ATag_ListsEveryPageWithIt_InTheSpacesTheCallerMayRead()
    {
        var (desk, hr) = (await SpaceAsync("desk"), await SpaceAsync("hr"));
        await PageAsync(desk, "Reset VPN", "vpn");
        await PageAsync(desk, "VPN client", "vpn", "windows");
        await PageAsync(desk, "Printers", "windows");
        await PageAsync(hr, "VPN for HR", "vpn");

        Assert.Equal(["Reset VPN", "VPN client", "VPN for HR"], await TaggedAsync(_admin, "vpn"));

        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = "rita", DisplayName = "Rita", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{desk}/roles", new { principalType = "User", principalId = id.ToString(), role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        var rita = _f.NewClient();
        (await rita.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "rita", password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(["Reset VPN", "VPN client"], await TaggedAsync(rita, "vpn"));
        Assert.Equal(["Printers", "VPN client"], await TaggedAsync(rita, "WINDOWS"));
    }
}
