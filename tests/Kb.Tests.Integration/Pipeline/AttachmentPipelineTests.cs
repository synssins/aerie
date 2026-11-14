using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The attachment and upload-setting routes go through the action bus: each answers with its action's name, a plugin can refuse an upload and shape a download,
/// only an Admin gets past the handlers of the settings, and the answers, headers and audit entries are what they were.
/// </summary>
public sealed class AttachmentPipelineTests : IAsyncLifetime
{
    private const string Uploads = "/api/v1/admin/uploads";
    private readonly List<long?> _limits = [];
    private KbAppFactory _f;
    private HttpClient _admin = null!;
    private Guid _space, _page;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AttachmentPipelineTests() => _f = new(KbAppFactory.NewDbPath(), services: s => s.AddSingleton<IStartupFilter>(new BodyLimits(_limits)));

    /// <summary>The test host has no request size limit. This gives every request one that can be changed, as a web server does, and notes what an upload left it at.</summary>
    private sealed class BodyLimits(List<long?> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                var limit = new Limit();
                ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
                await n(ctx);
                if (ctx.Request.Path.Value!.EndsWith("/attachments", StringComparison.Ordinal)) seen.Add(limit.MaxRequestBodySize);
            });
            next(app);
        };

        private sealed class Limit : IHttpMaxRequestBodySizeFeature
        {
            public bool IsReadOnly => false;
            public long? MaxRequestBodySize { get; set; } = 30_000_000;
        }
    }

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Installer" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private Task<HttpResponseMessage> UploadAsync(string text = "#!/bin/sh\necho hi", string? name = "set up.sh")
    {
        var body = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (name is not null) body.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return _admin.PostAsync($"/api/v1/pages/{_page}/attachments", body, Ct);
    }

    private async Task<AttachmentDto> UploadedAsync(string? name = "set up.sh") => (await (await UploadAsync(name: name)).Content.ReadFromJsonAsync<AttachmentDto>(Ct))!;

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
    public async Task EveryAttachmentRoute_AnswersWithItsActionName()
    {
        var added = await UploadAsync();
        Assert.Equal((HttpStatusCode.Created, "attachment.add"), (added.StatusCode, Action(added)));
        var json = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["id", "name", "size", "url", "at"], json.EnumerateObject().Select(p => p.Name));
        var id = json.GetProperty("id").GetString();
        Assert.Equal(("set up.sh", 17, $"/api/v1/attachments/{id}"), (json.GetProperty("name").GetString(), json.GetProperty("size").GetInt64(), json.GetProperty("url").GetString()));
        Assert.Equal($"/api/v1/attachments/{id}", added.Headers.Location?.ToString());

        var read = await _admin.GetAsync($"/api/v1/attachments/{id}", Ct);
        Assert.Equal((HttpStatusCode.OK, "attachment.read"), (read.StatusCode, Action(read)));
        Assert.Equal("#!/bin/sh\necho hi", await read.Content.ReadAsStringAsync(Ct));
        // A refusal by the core names the action too.
        var missing = await _admin.GetAsync($"/api/v1/attachments/{Guid.NewGuid()}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "attachment.read"), (missing.StatusCode, Action(missing)));
        var removed = await _admin.DeleteAsync($"/api/v1/attachments/{id}", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "attachment.remove"), (removed.StatusCode, Action(removed)));
        var gone = await _admin.DeleteAsync($"/api/v1/attachments/{id}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "attachment.remove"), (gone.StatusCode, Action(gone)));

        var settings = await _admin.GetAsync(Uploads, Ct);
        Assert.Equal((HttpStatusCode.OK, "settings.uploads.read"), (settings.StatusCode, Action(settings)));
        Assert.Equal("""{"attachmentMb":25,"serverLimitBytes":null}""", await settings.Content.ReadAsStringAsync(Ct));
        var saved = await _admin.PutAsJsonAsync(Uploads, new { attachmentMb = 2 }, Ct);
        Assert.Equal((HttpStatusCode.OK, "settings.uploads.save"), (saved.StatusCode, Action(saved)));
        Assert.Equal("""{"attachmentMb":2,"serverLimitBytes":null}""", await saved.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The download's headers, all of them, as they were before the route sent an action. Only X-Kb-Action is new.</summary>
    [Fact]
    public async Task Download_HeadersAreWhatTheyWere()
    {
        var a = await UploadedAsync();

        var r = await _admin.GetAsync(a.Url, Ct);

        var headers = r.Headers.Concat(r.Content.Headers).Where(h => h.Key is not ("X-Kb-Action" or "X-Kb-Version"))
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").Order(StringComparer.Ordinal);
        Assert.Equal(
        [
            "Content-Disposition: attachment; filename=\"set up.sh\"; filename*=UTF-8''set%20up.sh",
            "Content-Length: 17",
            "Content-Security-Policy: default-src 'none'; sandbox",
            "Content-Type: application/octet-stream",
            "Cross-Origin-Resource-Policy: same-origin",
            "Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()",
            "Referrer-Policy: same-origin",
            "X-Content-Type-Options: nosniff",
            "X-Frame-Options: DENY",
        ], headers);
    }

    [Fact]
    public async Task Add_RefusedByHook_Is422_NothingIsStored()
    {
        await SetAsync("Refuse", "attachment.add");

        var r = await UploadAsync();

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Empty((await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{_page}", Ct))!.Attachments);
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.Attachments.AnyAsync(Ct));
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AttachmentAdd, Ct));
        });
    }

    /// <summary>The app's limit replaces the web server's before the file is read, by the handler or by a plugin's hook; a request that says it is too large leaves it alone, as before.</summary>
    [Fact]
    public async Task Add_SetsTheRequestSizeLimit_BeforeAHookRuns_UnlessTheRequestSaysItIsTooLarge()
    {
        _limits.Clear();
        (await UploadAsync()).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync(Uploads, new { attachmentMb = 1 }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(text: new string('x', 1_048_577))).StatusCode);
        await SetAsync("Refuse", "attachment.add");
        Assert.Equal((HttpStatusCode)422, (await UploadAsync()).StatusCode);

        Assert.Equal([25 * 1_048_576L, 30_000_000L, 1_048_576L], _limits);
    }

    [Fact]
    public async Task Read_FilterShapesTheResult()
    {
        var a = await UploadedAsync();
        await SetAsync("Blank", "attachment.read:FileName");

        var r = await _admin.GetAsync(a.Url, Ct);

        // The filter took the name from the answer, not from the attachment: the page still lists it by name.
        Assert.Equal((HttpStatusCode.OK, null), (r.StatusCode, r.Content.Headers.ContentDisposition));
        Assert.Equal("set up.sh", Assert.Single((await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{_page}", Ct))!.Attachments).Name);
    }

    [Fact]
    public async Task UploadSettingsRoutes_Are403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var user = await UserAsync();

        var read = await user.GetAsync(Uploads, Ct);
        var save = await user.PutAsJsonAsync(Uploads, new { attachmentMb = 500 }, Ct);

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.Equal((HttpStatusCode.Forbidden, "settings.uploads.read"), (read.StatusCode, Action(read)));
        Assert.Equal((HttpStatusCode.Forbidden, "settings.uploads.save"), (save.StatusCode, Action(save)));
        await _f.WithDbAsync(async db => Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == UploadLimits.AttachmentKey, Ct)));
    }

    [Fact]
    public async Task Writes_AreAudited_AsTheCaller_AndBadUploadsAnswerAsBefore()
    {
        var a = await UploadedAsync();
        Assert.Equal("file", (await UploadedAsync(name: null)).Name); // no name given
        Assert.Equal(new string('n', 255), (await UploadedAsync(name: new string('n', 300))).Name);
        Assert.Equal("passwd", (await UploadedAsync(name: "../../etc/passwd")).Name);
        (await _admin.DeleteAsync(a.Url, Ct)).EnsureSuccessStatusCode();
        var empty = await UploadAsync(text: "");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("Empty file.", (await empty.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("file")[0].GetString());
        var bad = await _admin.PutAsJsonAsync(Uploads, new { attachmentMb = 4097 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("1 to 4096.", (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("attachmentMb")[0].GetString());
        (await _admin.PutAsJsonAsync(Uploads, new { attachmentMb = 1 }, Ct)).EnsureSuccessStatusCode();
        var large = await UploadAsync(text: new string('x', 1_048_577));
        Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
        Assert.Equal("1 MB maximum.", (await large.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("file")[0].GetString());

        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var stored = await db.Attachments.IgnoreQueryFilters().SingleAsync(x => x.Id == a.Id, Ct);
            Assert.Equal((_page, "set up.sh", 17L, me), (stored.PageId, stored.FileName, stored.Size, stored.CreatedBy));
            Assert.NotNull(stored.DeletedAt);
            Assert.Equal(4, await db.Attachments.IgnoreQueryFilters().CountAsync(Ct)); // the empty and the large one left no row
            var add = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.AttachmentAdd && e.TargetId == a.Id.ToString(), Ct);
            Assert.Equal((me, "attachment", _space, $$"""{"name":"set up.sh","page":"{{_page}}"}"""), (add.ActorId, add.TargetType, add.SpaceId, add.DataJson));
            var remove = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.AttachmentRemove, Ct);
            Assert.Equal((me, "attachment", a.Id.ToString(), _space, """{"name":"set up.sh"}"""), (remove.ActorId, remove.TargetType, remove.TargetId, remove.SpaceId, remove.DataJson));
            var setting = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.SettingsChange, Ct);
            Assert.Equal((me, "setting", UploadLimits.AttachmentKey, """{"value":"1"}"""), (setting.ActorId, setting.TargetType, setting.TargetId, setting.DataJson));
            var value = await db.RuntimeSettings.SingleAsync(s => s.Key == UploadLimits.AttachmentKey, Ct);
            Assert.Equal(("1", me), (value.Value, value.UpdatedBy));
        });
    }
}
