using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The findings of docs/security/2026-plugin-model-findings.md that are not a row of the authorization matrix, one test each:
/// an image of another space in an export and in a move, the admin bin and a sensitive space, a draft after its author lost the role.
/// </summary>
public sealed class FindingTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string key) =>
        (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, bool admin = false, params (Guid Space, string Role)[] roles)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, IsAdmin = admin, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        foreach (var (space, role) in roles)
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    private static async Task<Guid> ImageAsync(HttpClient by, Guid space)
    {
        var body = new ByteArrayContent(Png);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", "dot.png");
        var r = await by.PostAsync($"/api/v1/spaces/{space}/images", body, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static string DocWith(params Guid[] images) =>
        JsonSerializer.Serialize(new { type = "doc", content = images.Select(i => new { type = "image", attrs = new { src = $"/api/v1/images/{i}", alt = "dot" } }) });

    private static async Task<PageDto> PageAsync(HttpClient by, Guid space, string content)
    {
        var r = await by.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Page " + Guid.NewGuid().ToString("N")[..6], contentJson = content }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    /// <summary>
    /// Someone who can edit a page can write any image id into it. The page then shows that image only to people who may see it
    /// (the image route checks each reader). An export must not be a way around that.
    /// </summary>
    [Fact]
    public async Task Export_EmbedsOnlyImagesThePagesSpaceMayShow()
    {
        var (here, elsewhere) = (await SpaceAsync("here"), await SpaceAsync("elsewhere"));
        var (cara, _) = await UserAsync("cara", roles: (here, "Contributor"));
        var theirs = await ImageAsync(_admin, elsewhere);
        var mine = await ImageAsync(cara, here);
        Assert.Equal(HttpStatusCode.NotFound, (await cara.GetAsync($"/api/v1/images/{theirs}", Ct)).StatusCode);
        var page = await PageAsync(cara, here, DocWith(mine, theirs));

        var docx = await (await cara.GetAsync($"/api/v1/pages/{page.Id}/export?format=docx", Ct)).Content.ReadAsByteArrayAsync(Ct);

        using var zip = new ZipArchive(new MemoryStream(docx));
        Assert.Single(zip.Entries, e => e.FullName.Contains("media/", StringComparison.Ordinal)); // the page's own image, and not the other space's

        // Someone with a role in both spaces sees both images on the page, so their export has both: an export holds what the page shows its reader.
        var (dana, _) = await UserAsync("dana", roles: [(here, "Reader"), (elsewhere, "Reader")]);
        var both = await (await dana.GetAsync($"/api/v1/pages/{page.Id}/export?format=docx", Ct)).Content.ReadAsByteArrayAsync(Ct);
        using var danas = new ZipArchive(new MemoryStream(both));
        Assert.Equal(2, danas.Entries.Count(e => e.FullName.Contains("media/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Moving a page to another space lets that space show the images the page uses. Only images the page's own space may show
    /// are passed on: an image id of a third space, written into the page, is not laundered by a move.
    /// </summary>
    [Fact]
    public async Task Move_PassesOnOnlyImagesThePagesSpaceMayShow()
    {
        var (from, elsewhere, target) = (await SpaceAsync("from"), await SpaceAsync("elsewhere"), await SpaceAsync("target"));
        var theirs = await ImageAsync(_admin, elsewhere);
        var mine = await ImageAsync(_admin, from);
        var page = await PageAsync(_admin, from, DocWith(mine, theirs));
        var (tara, _) = await UserAsync("tara", roles: (target, "Reader"));

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/v1/pages/{page.Id}/move", new { spaceId = target, parentId = (Guid?)null }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await tara.GetAsync($"/api/v1/images/{mine}", Ct)).StatusCode);        // came with the page
        Assert.Equal(HttpStatusCode.NotFound, (await tara.GetAsync($"/api/v1/images/{theirs}", Ct)).StatusCode); // never was this page's space's to show

        // A second move passes on what the first one granted: the image still belongs to "from", and "target" may show it.
        var third = await SpaceAsync("third");
        var (theo, _) = await UserAsync("theo", roles: (third, "Reader"));
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/v1/pages/{page.Id}/move", new { spaceId = third, parentId = (Guid?)null }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await theo.GetAsync($"/api/v1/images/{mine}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await theo.GetAsync($"/api/v1/images/{theirs}", Ct)).StatusCode);
    }

    /// <summary>
    /// "Admins have no silent access to a sensitive space." The admin bin is no exception: it lists nothing of a sensitive space
    /// the Admin has no access to, and the Admin cannot restore from it. With emergency access (which tells the Owners) both work.
    /// </summary>
    [Fact]
    public async Task AdminBin_HasNothingOfASensitiveSpace_WithoutAccessToIt()
    {
        var (vault, desk) = (await SpaceAsync("vault"), await SpaceAsync("desk"));
        var (olga, _) = await UserAsync("olga", roles: [(vault, "Owner"), (desk, "Owner")]);
        var (adam, _) = await UserAsync("adam", admin: true);
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var secret = await PageAsync(olga, vault, DocWith());
        var plain = await PageAsync(olga, desk, DocWith());
        foreach (var p in new[] { secret, plain }) Assert.Equal(HttpStatusCode.NoContent, (await olga.DeleteAsync($"/api/v1/pages/{p.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await olga.PostAsync($"/api/v1/spaces/{vault}/bin/empty", null, Ct)).StatusCode); // now only the admin bin has it

        async Task<List<Guid>> BinAsync() => [.. (await adam.GetFromJsonAsync<List<BinItemDto>>("/api/v1/admin/bin", Ct))!.Select(i => i.Id)];

        Assert.Equal([plain.Id], await BinAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await adam.PostAsync($"/api/v1/pages/{secret.Id}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adam.PostAsync($"/api/v1/pages/{plain.Id}/restore", null, Ct)).StatusCode); // a normal space: as before

        // Emergency access lets an Admin read a sensitive space. It is a Reader's access: the bin of that space stays out of reach.
        Assert.True((await adam.PostAsJsonAsync($"/api/v1/spaces/{vault}/emergency-access", new { reason = "Look at a page." }, Ct)).IsSuccessStatusCode);
        Assert.Empty(await BinAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await adam.PostAsync($"/api/v1/pages/{secret.Id}/restore", null, Ct)).StatusCode);

        // Only a role with the bin right in that space opens it, and an Owner of the space gives that role.
        var adamId = (await adam.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        (await olga.PutAsJsonAsync($"/api/v1/spaces/{vault}/roles", new { principalType = "User", principalId = adamId.ToString(), role = "Supervisor" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal([secret.Id], await BinAsync());
        Assert.Equal(HttpStatusCode.OK, (await adam.PostAsync($"/api/v1/pages/{secret.Id}/restore", null, Ct)).StatusCode);
    }

    /// <summary>
    /// Finding 10. Clearing a space's sensitive flag makes every Admin its Owner. An Admin may do that only with access to the space
    /// (a role, or emergency access, which tells the Owners), and the Owners are told when it happens.
    /// </summary>
    [Fact]
    public async Task ClearingTheSensitiveFlag_NeedsAccessToTheSpace_AndTellsItsOwners()
    {
        var vault = await SpaceAsync("vault");
        var (olga, _) = await UserAsync("olga", roles: (vault, "Owner"));
        var (adam, adamId) = await UserAsync("adam", admin: true);
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        async Task<List<string>> AlertsAsync() => [.. (await olga.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Ct)).EnumerateArray()
            .Where(n => n.GetProperty("kind").GetString() == "security.alert").Select(n => n.GetProperty("title").GetString()!)];
        var before = (await AlertsAsync()).Count;

        foreach (var change in new object[] { new { isSensitive = false }, new { isPublic = true } })
        {
            var refused = await adam.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", change, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("No access to this space.", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        }
        await _f.WithDbAsync(async db => Assert.True((await db.Spaces.SingleAsync(s => s.Id == vault, Ct)) is { IsSensitive: true, IsPublic: false }));
        // What does not lower the space's protection needs no access: a new name, and the flag itself on a space that has none.
        Assert.Equal(HttpStatusCode.OK, (await adam.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", new { name = "Vault" }, Ct)).StatusCode);
        Assert.Equal(before, (await AlertsAsync()).Count);

        Assert.True((await adam.PostAsJsonAsync($"/api/v1/spaces/{vault}/emergency-access", new { reason = "It is not sensitive any more." }, Ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adam.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", new { isSensitive = false }, Ct)).StatusCode);

        Assert.Contains("Vault is no longer a sensitive space", await AlertsAsync());

        // The same change by the space's own address, by an Admin who is an Owner there: the other Owners hear of it all the same.
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{vault}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        (await olga.PutAsJsonAsync($"/api/v1/spaces/{vault}/roles", new { principalType = "User", principalId = adamId.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await adam.PatchAsJsonAsync($"/api/v1/spaces/{vault}", new { isSensitive = false }, Ct)).StatusCode);
        Assert.Equal(2, (await AlertsAsync()).Count(a => a == "Vault is no longer a sensitive space"));
    }

    /// <summary>Finding 12. A space may show an image of another space while a page that uses it is there. When the last such page leaves, so does that right.</summary>
    [Fact]
    public async Task MoveAway_TakesBackTheImagesTheSpaceNoLongerUses()
    {
        var (home, guest) = (await SpaceAsync("home"), await SpaceAsync("guest"));
        var (tara, _) = await UserAsync("tara", roles: (guest, "Reader"));
        var (one, two) = (await ImageAsync(_admin, home), await ImageAsync(_admin, home));
        var first = await PageAsync(_admin, home, DocWith(one, two));
        var second = await PageAsync(_admin, home, DocWith(two));
        foreach (var p in new[] { first, second })
            Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/v1/pages/{p.Id}/move", new { spaceId = guest, parentId = (Guid?)null }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tara.GetAsync($"/api/v1/images/{one}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/v1/pages/{first.Id}/move", new { spaceId = home, parentId = (Guid?)null }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await tara.GetAsync($"/api/v1/images/{one}", Ct)).StatusCode); // no page of "guest" uses it any more
        Assert.Equal(HttpStatusCode.OK, (await tara.GetAsync($"/api/v1/images/{two}", Ct)).StatusCode);       // the second page still does
    }

    /// <summary>Finding 13. Saving a page keeps the uploads it uses from being cleaned up: its own space's, not an upload of another space it merely names.</summary>
    [Fact]
    public async Task Saving_KeepsOnlyUploadsThePagesSpaceMayShow()
    {
        var (here, elsewhere) = (await SpaceAsync("here"), await SpaceAsync("elsewhere"));
        var (cara, _) = await UserAsync("cara", roles: (here, "Contributor"));
        var (theirs, mine) = (await ImageAsync(_admin, elsewhere), await ImageAsync(cara, here));

        var page = await PageAsync(cara, here, DocWith(mine, theirs));
        (await cara.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Draft", contentJson = DocWith(mine, theirs), baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();

        await _f.WithDbAsync(async db =>
        {
            Assert.False((await db.SpaceImages.SingleAsync(i => i.Id == mine, Ct)).Pending);
            Assert.True((await db.SpaceImages.SingleAsync(i => i.Id == theirs, Ct)).Pending);
        });
    }

    /// <summary>
    /// Finding 14. The HTTP client the app gives a plugin checks the host of each request against the manifest. A redirect would
    /// be followed below that check, so it is not followed: the plugin gets the redirect and asks again, and that request is checked.
    /// </summary>
    [Fact]
    public async Task PluginHttpClient_DoesNotFollowRedirects()
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();

        HttpMessageHandler handler = _f.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(PluginHttp.ClientName("kb.test.probe"));
        while (handler is DelegatingHandler { InnerHandler: { } inner }) handler = inner;

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>A draft is its author's, and it holds text of the page. Once the author has no role in the space, the draft is out of reach too.</summary>
    [Fact]
    public async Task Draft_IsOutOfReach_OnceItsAuthorHasNoRoleInTheSpace()
    {
        var desk = await SpaceAsync("desk");
        var (cara, caraId) = await UserAsync("cara", roles: (desk, "Contributor"));
        var page = await PageAsync(cara, desk, DocWith());
        (await cara.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Draft", contentJson = DocWith(), baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await cara.GetAsync($"/api/v1/pages/{page.Id}/draft", Ct)).StatusCode);

        var roles = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{desk}/roles", Ct);
        var assignment = roles.EnumerateArray().Single(a => a.GetProperty("principalId").GetString() == caraId.ToString()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/spaces/{desk}/roles/{assignment}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await cara.GetAsync($"/api/v1/pages/{page.Id}/draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cara.DeleteAsync($"/api/v1/pages/{page.Id}/draft", Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.PageDrafts.AnyAsync(d => d.PageId == page.Id, Ct))); // refused, so not deleted either

        // A page has one draft. Cara can no longer let go of hers, so it must not keep the page from the next editor for a day.
        var (dana, danaId) = await UserAsync("dana", roles: (desk, "Contributor"));
        Assert.Equal(HttpStatusCode.OK, (await dana.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Dana's", contentJson = DocWith(), baseVersion = "1.0" }, Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.Equal(danaId, (await db.PageDrafts.SingleAsync(d => d.PageId == page.Id, Ct)).UserId));
    }

    /// <summary>
    /// A save that loses the race for its version number lets go of what it changed for that page, and of nothing else: rows another
    /// handler of the same request holds (a plugin's hook may have sent this save) stay as they are, changed or not.
    /// </summary>
    [Fact]
    public async Task Publish_ThatLosesTheRace_LetsGoOnlyOfItsOwnPage()
    {
        var desk = await SpaceAsync("desk");
        var page = await PageAsync(_admin, desk, DocWith());
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        await _f.WithDbAsync(async db =>
        {
            db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = 1, Minor = 1, Title = "Taken", ContentJson = DocWith(), AuthorId = me, At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        await using var scope = _f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var space = await db.Spaces.SingleAsync(s => s.Id == desk, Ct);
        space.Description = "Changed by the handler around it.";                       // a change that is still to be saved
        var other = await db.Pages.FirstAsync(p => p.SpaceId == desk, Ct);              // a row that is only held
        var tracked = await db.Pages.SingleAsync(p => p.Id == page.Id, Ct);
        tracked.Title = "Would be saved";

        var answer = await PageSupport.PublishAsync(db, scope.ServiceProvider.GetRequiredService<IAccess>(), scope.ServiceProvider.GetRequiredService<DocRenderers>(),
            Actor.User(me), tracked, DocWith(), major: false, note: null, tags: [], now: DateTimeOffset.UtcNow);

        Assert.Equal(409, answer.Problem?.Status);
        Assert.Equal(EntityState.Modified, db.Entry(space).State);
        Assert.Equal(EntityState.Detached, db.Entry(tracked).State);
        Assert.Empty(db.ChangeTracker.Entries<PageVersion>());
        _ = other;
        await db.SaveChangesAsync(Ct); // what the handler around it had pending is saved; nothing of the refused save is
        await _f.WithDbAsync(async fresh =>
        {
            Assert.Equal("Changed by the handler around it.", (await fresh.Spaces.SingleAsync(s => s.Id == desk, Ct)).Description);
            Assert.NotEqual("Would be saved", (await fresh.Pages.SingleAsync(p => p.Id == page.Id, Ct)).Title);
        });
    }
}
