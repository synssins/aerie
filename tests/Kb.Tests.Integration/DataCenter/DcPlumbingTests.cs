using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>The rules every Data Center compatible operation shares (contracts/behaviour/common.md), and server information.</summary>
public sealed class DcPlumbingTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task ServerInformation_NeedsSignIn_AndReportsThePresentedVersion()
    {
        await (await _f.NewClient().GetAsync("/rest/api/server-information", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);

        await UserAsync(_f, "ravi");
        var info = await Basic(_f, "ravi").GetJsonAsync("/rest/api/server-information");
        Assert.Equal("https://localhost", info.GetProperty("baseUrl").GetString());
        Assert.Equal("9.2.4", info.GetProperty("version").GetString());
        Assert.Equal(9_002_004, info.GetProperty("buildNumber").GetInt32());
        Assert.Equal(9_002_004, info.GetProperty("marketplaceBuildNumber").GetInt32());
        Assert.True(DateTimeOffset.TryParse(info.GetProperty("buildDate").GetString(), out _));
    }

    [Fact]
    public async Task Basic_SignsInEachRequest_AndAuditsIt()
    {
        var id = await UserAsync(_f, "ravi");
        var me = await Basic(_f, "ravi").GetJsonAsync("/rest/api/user/current");
        Assert.Equal("known", me.GetProperty("type").GetString());
        Assert.Equal(id.ToString("N"), me.GetProperty("userKey").GetString());
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BasicUse && a.ActorId == id, Ct)));
    }

    [Fact]
    public async Task Basic_WrongPassword_Is401_EvenWhereAnonymousIsAllowed_AndIsAudited()
    {
        var id = await UserAsync(_f, "ravi");
        foreach (var path in new[] { "/rest/api/user/current", "/rest/api/user/anonymous" })
        {
            var r = await Basic(_f, "ravi", "not-the-password").GetAsync(path, Ct);
            await r.ErrorAsync(HttpStatusCode.Unauthorized);
            Assert.Empty(r.Headers.WwwAuthenticate); // no Basic challenge: a browser shows no password dialog (identity.md)
        }
        await (await Basic(_f, "nobody").GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SignInFailed && a.ActorId == id && a.DataJson.Contains("basic"), Ct)));
    }

    [Fact]
    public async Task Basic_IsRefused_ForAccountsThatUseCodes_WithTheWrongPasswordAnswer()
    {
        var coded = await UserAsync(_f, "coded", u => u.CodesRequired = true);
        var right = await Basic(_f, "coded").GetAsync("/rest/api/user/current", Ct);
        var wrong = await Basic(_f, "coded", "not-the-password").GetAsync("/rest/api/user/current", Ct);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await right.Content.ReadAsStringAsync(Ct));
        await right.ErrorAsync(HttpStatusCode.Unauthorized);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SignInFailed && a.ActorId == coded && a.DataJson.Contains("codes required"), Ct)));
        // An Admin uses codes by policy.
        await UserAsync(_f, "boss", u => u.IsAdmin = true);
        await (await Basic(_f, "boss").GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        // An account an Admin exempted from codes uses Basic.
        await UserAsync(_f, "demo", u => u.CodesExempt = true);
        Assert.Equal("demo", (await Basic(_f, "demo").GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Basic_BreakGlass_SignsIn_AndAlertsTheAdmins()
    {
        Assert.Equal("breakglass", (await Basic(_f, "breakglass", KbAppFactory.BreakGlassPassword).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BreakGlass, Ct)));
    }

    [Fact]
    public async Task Basic_IsIgnoredOnAeriesOwnApi()
    {
        await UserAsync(_f, "ravi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Basic(_f, "ravi").GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task UnknownPath_Is404RestError_NotTheUiPage()
    {
        await (await _f.NewClient().GetAsync("/rest/api/no-such-thing", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().GetAsync("/rest/api/no-such-thing.json", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().GetAsync("/rest/api/", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().PostAsJsonAsync("/rest/api/content/1/anything", new { }, Ct)).ErrorAsync(HttpStatusCode.NotFound);
        // The OAuth 2.0 surface is not built yet; its paths answer JSON too, never the web app.
        await (await _f.NewClient().GetAsync("/rest/oauth2/latest/authorize", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().PostAsJsonAsync("/rest/oauth2/latest/token", new { }, Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrongMethod_Is405RestError()
    {
        foreach (var method in new[] { HttpMethod.Delete, HttpMethod.Put, HttpMethod.Patch })
        {
            using var req = new HttpRequestMessage(method, "/rest/api/user/list") { Content = JsonContent.Create(new { }) };
            await (await _f.NewClient().SendAsync(req, Ct)).ErrorAsync(HttpStatusCode.MethodNotAllowed);
        }
    }

    [Fact]
    public async Task Health_IsOpenToAnyone()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/ready", Ct)).StatusCode);
    }

    /// <summary>No route takes multipart yet (attachments will, and test it over HTTP); the rule itself is checked here.</summary>
    [Theory]
    [InlineData("POST", "multipart/form-data; boundary=x", null, false)]
    [InlineData("PUT", "application/x-www-form-urlencoded", null, false)]
    [InlineData("DELETE", "multipart/form-data; boundary=x", "other", false)]
    [InlineData("POST", "multipart/form-data; boundary=x", "no-check", true)]
    [InlineData("POST", "multipart/form-data; boundary=x", "nocheck", true)]
    [InlineData("POST", "application/json", null, true)]
    [InlineData("GET", "multipart/form-data; boundary=x", null, true)]
    public void MultipartAndFormWrites_NeedTheXsrfHeader(string method, string type, string? header, bool ok)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = method;
        http.Request.ContentType = type;
        if (header is not null) http.Request.Headers["X-Atlassian-Token"] = header;
        Assert.Equal(ok, Kb.Web.DataCenter.DcRest.XsrfOk(http.Request));
    }

    [Fact]
    public async Task ApiTokens_ReachTheSurface()
    {
        var id = await UserAsync(_f, "ravi");
        var cookie = _f.NewClient();
        (await cookie.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ravi", password = Password }, Ct)).EnsureSuccessStatusCode();
        var created = await (await cookie.PostAsJsonAsync("/api/v1/tokens", new { name = "Agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 30 }, Ct))
            .JsonAsync(HttpStatusCode.Created);
        var token = _f.NewClient();
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.GetProperty("token").GetString());
        Assert.Equal(id.ToString("N"), (await token.GetJsonAsync("/rest/api/user/current")).GetProperty("userKey").GetString());
    }

    [Fact]
    public async Task ContextPath_And_BaseUrl_ComeFromSettings()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string>
            { ["Kb:DataCenter:ContextPath"] = "/kb", ["Kb:DataCenter:BaseUrl"] = "https://wiki.example.org/kb" });
        var anon = await f.NewClient().GetJsonAsync("/kb/rest/api/user/anonymous");
        Assert.Equal("https://wiki.example.org/kb", anon.GetProperty("_links").GetProperty("base").GetString());
        Assert.Equal("/kb", anon.GetProperty("_links").GetProperty("context").GetString());
        Assert.Equal("https://wiki.example.org/kb/rest/api/user/anonymous", anon.GetProperty("_links").GetProperty("self").GetString());
        Assert.Equal(HttpStatusCode.OK, (await f.NewClient().GetAsync("/kb" + Kb.Web.DataCenter.DcUserJson.AvatarPath, Ct)).StatusCode);
    }

    [Fact]
    public async Task DefaultPicture_IsServed()
    {
        var r = await _f.NewClient().GetAsync(Kb.Web.DataCenter.DcUserJson.AvatarPath, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/svg+xml", r.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void BuildNumber_IsDerivedFromTheVersion()
    {
        Assert.Equal(9_002_004, Kb.Web.DataCenter.DcRoutes.Derived("9.2.4"));
        Assert.Equal(10_000_001, Kb.Web.DataCenter.DcRoutes.Derived("10.0.1"));
        Assert.Equal(9_000_000, Kb.Web.DataCenter.DcRoutes.Derived("9"));
    }

    internal static async Task SetGroupAsync(KbAppFactory f, string name, params Guid[] members)
    {
        await f.WithDbAsync(async db =>
        {
            var g = new KbGroup { Key = "local:" + Guid.NewGuid(), Name = name, IsLocal = true };
            db.KbGroups.Add(g);
            db.KbGroupMembers.AddRange(members.Select(m => new KbGroupMember { GroupId = g.Id, UserId = m }));
            await db.SaveChangesAsync(Ct);
        });
    }
}
