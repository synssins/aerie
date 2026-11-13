using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The role matrix of the design (spec section 7), asked of the API: every action on a space's content, sent by each kind of caller,
/// in a normal space and in a sensitive one. The expected answer follows from one rule, not from the handlers:
/// not signed in 401; no role in the space 404 (the space is not disclosed); a role below the action's minimum 403; otherwise it works.
/// An Admin is Owner of a space that is not sensitive and has nothing in a sensitive one.
/// A mismatch is a finding for the owner; neither side is changed to make this pass without their word.
/// </summary>
public sealed class RoleMatrixTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private readonly Dictionary<string, HttpClient> _callers = [];
    private HttpClient _owner = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The callers, weakest first. The number is the role they hold in the space; -1 not signed in, 0 no role, 9 an Admin without a role.</summary>
    private static readonly (string Name, int Level)[] Callers =
        [("anonymous", -1), ("outsider", 0), ("reader", 1), ("contributor", 2), ("supervisor", 3), ("owner", 4), ("admin", 9)];

    /// <summary>One action: the least role that may do it, the answer when it works, and how to build a request against fresh content of a space.</summary>
    private sealed record Case(string Action, SpaceRole Min, HttpStatusCode Ok, Func<RoleMatrixTests, Guid, Task<HttpRequestMessage>> Request, bool AdminAnywhere = false);

    private static HttpRequestMessage Json(HttpMethod method, string path, object? body = null) =>
        new(method, path) { Content = body is null ? null : JsonContent.Create(body) };

    /// <summary>An upload is the raw request body; the file's name travels in a header.</summary>
    private static HttpRequestMessage File(string path, byte[] bytes, string name, string type)
    {
        _ = type;
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
    }

    private static readonly Case[] Cases =
    [
        new("tree.read", SpaceRole.Reader, HttpStatusCode.OK, (t, s) => Task.FromResult(Json(HttpMethod.Get, $"/api/v1/spaces/{s}/tree"))),
        new("space.read", SpaceRole.Reader, HttpStatusCode.OK, (t, s) => Task.FromResult(Json(HttpMethod.Get, $"/api/v1/spaces/{s}"))),
        new("page.read", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}")),
        new("page.version.list", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}/versions")),
        new("page.version.read", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}/versions/1.0")),
        new("page.diff", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}/diff?from=1.0&to=1.0")),
        new("page.markdown", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}/markdown")),
        new("page.export", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/pages/{await t.PageAsync(s)}/export?format=docx")),
        new("attachment.read", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/attachments/{await t.AttachmentAsync(s)}")),
        new("image.read", SpaceRole.Reader, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Get, $"/api/v1/images/{await t.ImageAsync(s)}")),

        new("page.create", SpaceRole.Contributor, HttpStatusCode.Created, (t, s) => Task.FromResult(Json(HttpMethod.Post, $"/api/v1/spaces/{s}/pages", new { kind = "Page", title = "New" }))),
        new("page.save", SpaceRole.Contributor, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Put, $"/api/v1/pages/{await t.PageAsync(s)}", new { title = "Saved", contentJson = Doc, baseVersion = "1.0" })),
        new("draft.save", SpaceRole.Contributor, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Put, $"/api/v1/pages/{await t.PageAsync(s)}/draft", new { title = "Draft", contentJson = Doc, baseVersion = "1.0" })),
        new("page.version.restore", SpaceRole.Contributor, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Post, $"/api/v1/pages/{await t.PageAsync(s)}/versions/1.0/restore")),
        new("attachment.add", SpaceRole.Contributor, HttpStatusCode.Created, async (t, s) => File($"/api/v1/pages/{await t.PageAsync(s)}/attachments", "hello"u8.ToArray(), "note.txt", "text/plain")),
        new("attachment.remove", SpaceRole.Contributor, HttpStatusCode.NoContent, async (t, s) => Json(HttpMethod.Delete, $"/api/v1/attachments/{await t.AttachmentAsync(s)}")),
        new("image.add", SpaceRole.Contributor, HttpStatusCode.Created, (t, s) => Task.FromResult(File($"/api/v1/spaces/{s}/images", Png, "dot.png", "image/png"))),

        // The page is someone else's (the Owner made it): moving or deleting it needs a Supervisor.
        new("page.move", SpaceRole.Supervisor, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Post, $"/api/v1/pages/{await t.PageAsync(s)}/move", new { parentId = await t.PageAsync(s, "Folder") })),
        new("page.delete", SpaceRole.Supervisor, HttpStatusCode.NoContent, async (t, s) => Json(HttpMethod.Delete, $"/api/v1/pages/{await t.PageAsync(s)}")),
        new("bin.list", SpaceRole.Supervisor, HttpStatusCode.OK, (t, s) => Task.FromResult(Json(HttpMethod.Get, $"/api/v1/spaces/{s}/bin"))),
        new("bin.empty", SpaceRole.Supervisor, HttpStatusCode.NoContent, (t, s) => Task.FromResult(Json(HttpMethod.Post, $"/api/v1/spaces/{s}/bin/empty"))),
        // An Admin restores from the admin bin without a role in the space, a sensitive one too: that is how the handler is written, and was before.
        new("page.restore", SpaceRole.Supervisor, HttpStatusCode.OK, async (t, s) => Json(HttpMethod.Post, $"/api/v1/pages/{await t.DeletedPageAsync(s)}/restore"), AdminAnywhere: true),

        new("space.role.list", SpaceRole.Owner, HttpStatusCode.OK, (t, s) => Task.FromResult(Json(HttpMethod.Get, $"/api/v1/spaces/{s}/roles"))),
        new("space.role.grant", SpaceRole.Owner, HttpStatusCode.NoContent, (t, s) => Task.FromResult(Json(HttpMethod.Put, $"/api/v1/spaces/{s}/roles", new { principalType = "User", principalId = Guid.NewGuid().ToString(), role = "Reader" }))),
        new("space.role.revoke", SpaceRole.Owner, HttpStatusCode.NoContent, async (t, s) => Json(HttpMethod.Delete, $"/api/v1/spaces/{s}/roles/{await t.AssignmentAsync(s)}")),
        new("space.update", SpaceRole.Owner, HttpStatusCode.OK, (t, s) => Task.FromResult(Json(HttpMethod.Patch, $"/api/v1/spaces/{s}", new { description = "Changed" }))),
    ];

    public async ValueTask InitializeAsync()
    {
        _ = await _f.SignedInBreakGlassAsync(); // the app is set up
        foreach (var (name, level) in Callers)
        {
            if (level < 0) { _callers[name] = _f.NewClient(); continue; }
            await using (var scope = _f.Services.CreateAsyncScope())
            {
                var user = new KbUser { UserName = name, DisplayName = name, IsAdmin = level == 9, CreatedAt = DateTimeOffset.UtcNow };
                Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
            }
            var c = _f.NewClient();
            (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
            _callers[name] = c;
        }
        _owner = _callers["owner"];
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>A space in which each caller has the role of their name. Made as a normal space, then marked sensitive: only an Admin can do that.</summary>
    private async Task<Guid> SpaceAsync(bool sensitive)
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var key = sensitive ? "vault" : "desk";
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        foreach (var (name, level) in Callers.Where(c => c.Level is >= 1 and <= 4))
        {
            var me = (await _callers[name].GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
            (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = me.ToString(), role = ((SpaceRole)level).ToString() }, Ct)).EnsureSuccessStatusCode();
        }
        if (sensitive) (await admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        return space;
    }

    // Fresh content for one request, made by the Owner of the space, so every case starts from the same state.
    private async Task<Guid> PageAsync(Guid space, string kind = "Page")
    {
        var r = await _owner.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind, title = $"{kind} {Guid.NewGuid():N}", contentJson = kind == "Page" ? Doc : null }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    private async Task<Guid> DeletedPageAsync(Guid space)
    {
        var page = await PageAsync(space);
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        return page;
    }

    private async Task<Guid> AttachmentAsync(Guid space)
    {
        using var request = File($"/api/v1/pages/{await PageAsync(space)}/attachments", "hello"u8.ToArray(), "note.txt", "text/plain");
        var r = await _owner.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ImageAsync(Guid space)
    {
        using var request = File($"/api/v1/spaces/{space}/images", Png, "dot.png", "image/png");
        var r = await _owner.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> AssignmentAsync(Guid space)
    {
        var principal = Guid.NewGuid().ToString();
        (await _owner.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = principal, role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        var roles = await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{space}/roles", Ct);
        return roles.EnumerateArray().Single(a => a.GetProperty("principalId").GetString() == principal).GetProperty("id").GetGuid();
    }

    private static HttpStatusCode Expected(Case c, int level, bool sensitive) => level switch
    {
        -1 => HttpStatusCode.Unauthorized,
        0 => HttpStatusCode.NotFound,
        9 when c.AdminAnywhere => c.Ok,
        9 => sensitive ? HttpStatusCode.NotFound : c.Ok,
        _ => level >= (int)c.Min ? c.Ok : HttpStatusCode.Forbidden,
    };

    private async Task<List<string>> MismatchesAsync(bool sensitive)
    {
        var space = await SpaceAsync(sensitive);
        var wrong = new List<string>();
        foreach (var c in Cases)
        {
            foreach (var (name, level) in Callers)
            {
                using var request = await c.Request(this, space);
                var got = (await _callers[name].SendAsync(request, Ct)).StatusCode;
                var expected = Expected(c, level, sensitive);
                if (got != expected) wrong.Add($"{c.Action} as {name}: {(int)got}, expected {(int)expected}");
            }
        }
        return wrong;
    }

    [Fact]
    public async Task NormalSpace_EveryActionAnswersByRole() => Assert.Empty(await MismatchesAsync(sensitive: false));

    [Fact]
    public async Task SensitiveSpace_EveryActionAnswersByRole_AndAnAdminHasNothing() => Assert.Empty(await MismatchesAsync(sensitive: true));
}
