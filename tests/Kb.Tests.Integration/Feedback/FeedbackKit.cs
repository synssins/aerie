using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>
/// The set-up comments.md and ratings.md give the lab: a space with a Reader, a Contributor, a Supervisor and an Owner, a second Reader,
/// a page and a folder in it. The break-glass account is the site administrator with no role in the space.
/// </summary>
public sealed class FeedbackKit : IAsyncDisposable
{
    public KbAppFactory F { get; }
    public HttpClient Admin { get; private set; } = null!;
    public HttpClient Reader { get; private set; } = null!;
    public HttpClient Reader2 { get; private set; } = null!;
    public HttpClient Contributor { get; private set; } = null!;
    public HttpClient Supervisor { get; private set; } = null!;
    public HttpClient Owner { get; private set; } = null!;
    /// <summary>Signed in, no role in the space (which is private).</summary>
    public HttpClient Outsider { get; private set; } = null!;
    public Guid Space { get; private set; }
    public Guid Page { get; private set; }
    public Guid Folder { get; private set; }
    public Dictionary<string, Guid> Ids { get; } = [];

    private FeedbackKit(KbAppFactory f) => F = f;

    public static async Task<FeedbackKit> CreateAsync(KbAppFactory? f = null)
    {
        var k = new FeedbackKit(f ?? new KbAppFactory(KbAppFactory.NewDbPath()));
        k.Admin = await k.F.SignedInBreakGlassAsync();
        k.Space = await k.SpaceAsync("Feedback", "fb");
        foreach (var (name, role) in new[] { ("rita", "Reader"), ("rory", "Reader"), ("cora", "Contributor"), ("sam", "Supervisor"), ("olga", "Owner") })
        {
            k.Ids[name] = await UserAsync(k.F, name);
            (await k.Admin.PutAsJsonAsync($"/api/v1/spaces/{k.Space}/roles", new { principalType = "User", principalId = k.Ids[name].ToString(), role }, Ct)).EnsureSuccessStatusCode();
        }
        k.Ids["otto"] = await UserAsync(k.F, "otto");
        (k.Reader, k.Reader2, k.Contributor, k.Supervisor, k.Owner, k.Outsider) =
            (await k.SignInAsync("rita"), await k.SignInAsync("rory"), await k.SignInAsync("cora"), await k.SignInAsync("sam"), await k.SignInAsync("olga"), await k.SignInAsync("otto"));
        k.Page = await k.PageAsync(k.Space, "Runbook");
        k.Folder = await k.PageAsync(k.Space, "Guides", "Folder");
        return k;
    }

    public async Task<Guid> SpaceAsync(string name, string key, string visibility = "Private") =>
        (await (await Admin.PostAsJsonAsync("/api/v1/spaces", new { name, key, visibility }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public async Task<Guid> PageAsync(Guid space, string title, string kind = "Page") =>
        (await (await Admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind, title }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public async Task<HttpClient> SignInAsync(string name)
    {
        var c = F.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>A personal API token of the person, read access everywhere.</summary>
    public async Task<HttpClient> TokenAsync(HttpClient signedIn)
    {
        var token = (await (await signedIn.PostAsJsonAsync("/api/v1/tokens", new { name = "Script", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
        var c = F.NewClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    public Task SiteAsync(bool comments, bool ratings) =>
        Admin.PutAsJsonAsync("/api/v1/admin/feedback", new { commentsEnabled = comments, ratingsEnabled = ratings }, Ct).ContinueWith(t => t.Result.EnsureSuccessStatusCode(), Ct);

    public async Task SpaceSwitchAsync(bool? comments = null, bool? ratings = null) =>
        (await Owner.PatchAsJsonAsync($"/api/v1/spaces/{Space}", new { commentsEnabled = comments, ratingsEnabled = ratings }, Ct)).EnsureSuccessStatusCode();

    public async Task PageSwitchAsync(Guid page, bool? comments = null, bool? ratings = null) =>
        (await Supervisor.PutAsJsonAsync($"/api/v1/pages/{page}/feedback", new { commentsEnabled = comments, ratingsEnabled = ratings }, Ct)).EnsureSuccessStatusCode();

    public async Task<T> DbAsync<T>(Func<KbDbContext, Task<T>> read) => await F.WithDbValueAsync(read);

    public Task<List<AuditEntry>> AuditAsync(string action) => DbAsync(db => db.AuditEntries.Where(a => a.Action == action).ToListAsync(Ct));

    public async ValueTask DisposeAsync() => await F.DisposeAsync();
}
