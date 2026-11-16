using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>
/// The lab's black-box findings against the image with all 52 T1 operations, each reproduced with the lab's request first:
/// tokens acting with their owner's role, paging links, label bodies, Basic with a token, and the limit on failed Basic attempts.
/// </summary>
public sealed class DcLabFindingsTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>A personal token of the signed-in caller, as aerie's own token page makes it.</summary>
    private static async Task<string> TokenAsync(HttpClient owner, string access = "Write", Guid? space = null) =>
        (await (await owner.PostAsJsonAsync("/api/v1/tokens", new { name = "Lab " + Guid.NewGuid().ToString("N")[..6], kind = "Personal", scopes = new[] { new { spaceId = space, access } }, days = 30 }, Ct))
            .JsonAsync(HttpStatusCode.Created)).GetProperty("token").GetString()!;

    private HttpClient Bearer(string token)
    {
        var c = _f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private HttpClient BasicToken(string user, string token, KbAppFactory? on = null)
    {
        var c = (on ?? _f).NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        return c;
    }

    /// <summary>An Admin who is a real account (not break-glass), signed in once with a code turned off for the test, holding a token.</summary>
    private async Task<(string Token, HttpClient Session)> AdminTokenAsync(string access = "Write")
    {
        if (await UserIdAsync(_f, "boss") is null) await UserAsync(_f, "boss", u => { u.IsAdmin = true; u.DisplayName = "Boss"; });
        // The session a token is made in has passed the sign-in code; here the test signs the Admin in with the break-glass session's token instead.
        await _f.WithDbAsync(async db =>
        {
            var boss = await db.Users.SingleAsync(u => u.UserName == "boss", Ct);
            var text = TokenFormat.Generate();
            db.ApiTokens.Add(new ApiToken
            {
                OwnerUserId = boss.Id, Name = "Lab", Kind = TokenKind.Personal, Prefix = text[..TokenFormat.PrefixShown], Hash = TokenFormat.Hash(text), CreatedBy = boss.Id,
                ScopesJson = JsonSerializer.Serialize(new[] { new TokenScope(null, access == "Write" ? TokenAccess.Write : TokenAccess.Read) }, Web),
                CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync(Ct);
            _last = text;
        });
        return (_last!, _admin);
    }

    private string? _last;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 1. An Admin's Write token creates spaces: the role decides, the scope only filters (identity.md, Scopes).
    [Fact]
    public async Task AdminsWriteToken_CreatesAndAdministersSpaces()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        Assert.Equal("LAB", (await (await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" })).JsonAsync(HttpStatusCode.OK)).GetProperty("key").GetString());
        Assert.Equal("PRIV", (await (await t.PostJsonAsync("/rest/api/space/_private", new { key = "PRIV", name = "Private" })).JsonAsync(HttpStatusCode.OK)).GetProperty("key").GetString());
        Assert.Equal("~boss", (await (await t.PostJsonAsync("/rest/api/space/personal", new { })).JsonAsync(HttpStatusCode.OK)).GetProperty("key").GetString());
        Assert.Equal("Lab renamed", (await (await t.PutAsJsonAsync("/rest/api/space/LAB", new { key = "LAB", name = "Lab renamed" }, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await t.PutAsync("/rest/api/space/LAB/archive", null, Ct)).StatusCode);
        var task = await (await t.DeleteAsync("/rest/api/space/PRIV", Ct)).JsonAsync(HttpStatusCode.Accepted);
        Assert.True((await t.GetJsonAsync("/rest/api/longtask/" + task.GetProperty("id").GetString())).GetProperty("successful").GetBoolean());
        Assert.Equal(1, (await t.GetJsonAsync("/rest/api/longtask")).GetProperty("size").GetInt32());

        // A Read token still only reads; aerie's own API still gives a token no Admin power.
        var (read, _) = await AdminTokenAsync("Read");
        await (await Bearer(read).PostJsonAsync("/rest/api/space", new { key = "NO", name = "No" })).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.GetAsync("/api/v1/admin/users", Ct)).StatusCode);
    }

    // 1. The same rule for space roles: an Owner's Write token holds the Owner's rights on the surface (here the trash, a Supervisor's right).
    [Fact]
    public async Task OwnersWriteToken_HoldsTheOwnersRights()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Ops", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var olga = await UserAsync(_f, "olga");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = olga.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();
        var session = _f.NewClient();
        (await session.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "olga", password = Password }, Ct)).EnsureSuccessStatusCode();
        var t = Bearer(await TokenAsync(session));
        Assert.Equal(0, (await t.GetJsonAsync("/rest/api/space/OPS/trash")).GetProperty("size").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await t.DeleteAsync("/rest/api/space/OPS/trash", Ct)).StatusCode);
        // A token limited to another space sees nothing of this one.
        var other = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Other", key = "other" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{other}/roles", new { principalType = "User", principalId = olga.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();
        await (await Bearer(await TokenAsync(session, space: other)).GetAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    // 2. An Admin's token looks people up (users.md, Who may look people up).
    [Fact]
    public async Task AdminsToken_LooksUpUsers()
    {
        var (token, _) = await AdminTokenAsync("Read");
        var ravi = await UserAsync(_f, "ravi");
        await DcPlumbingTests.SetGroupAsync(_f, "ops", ravi);
        var t = Bearer(token);
        Assert.Equal("ravi", (await t.GetJsonAsync("/rest/api/user?username=ravi")).GetProperty("username").GetString());
        Assert.Equal(2, (await t.GetJsonAsync("/rest/api/user/list")).GetProperty("size").GetInt32());
        Assert.Equal("ops", (await t.GetJsonAsync($"/rest/api/user/memberof?key={ravi:N}")).GetProperty("results")[0].GetProperty("name").GetString());
    }

    // 3. Uploading to a folder (attachments.md, Q-0006), as the lab sent it: a token, multipart, the XSRF header.
    [Fact]
    public async Task UploadToAFolder_IsAccepted()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" });
        var folder = (await (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "Folder", space = new { key = "LAB" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString();
        await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "Child", space = new { key = "LAB" }, ancestors = new[] { new { id = folder } } });
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("bytes")), "file", "lab.txt" } };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/rest/api/content/{folder}/child/attachment") { Content = form };
        req.Headers.Add("X-Atlassian-Token", "no-check");
        Assert.Equal("lab.txt", (await (await t.SendAsync(req, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0].GetProperty("title").GetString());
    }

    // 4. Every paged operation: prev from start > 0, next while more remain, both keeping the request's query.
    [Fact]
    public async Task EveryPagedOperation_HasPrevAndNext()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" });
        var folder = (await (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "Folder", space = new { key = "LAB" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString();
        for (var i = 0; i < 4; i++)
            (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = $"Child {i}", space = new { key = "LAB" }, ancestors = new[] { new { id = folder } } })).EnsureSuccessStatusCode();
        for (var i = 0; i < 3; i++)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", $"f{i}.bin" } };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/rest/api/content/{folder}/child/attachment") { Content = form };
            req.Headers.Add("X-Atlassian-Token", "no-check");
            (await t.SendAsync(req, Ct)).EnsureSuccessStatusCode();
        }
        await t.PostJsonAsync($"/rest/api/content/{folder}/label", new[] { new { name = "a" }, new { name = "b" }, new { name = "c" } });
        foreach (var name in new[] { "u1", "u2" }) await UserAsync(_f, name);
        for (var i = 0; i < 2; i++) await t.PostJsonAsync("/rest/api/space", new { key = $"S{i}", name = $"Space {i}" });

        var paths = new[]
        {
            "/rest/api/content", $"/rest/api/content/{folder}/child/page", $"/rest/api/content/{folder}/descendant/page", $"/rest/api/content/{folder}/child/attachment",
            $"/rest/api/content/{folder}/label", "/rest/api/space/LAB/content/page", "/rest/api/space", "/rest/api/user/list",
            "/rest/api/content/search?cql=type%3Dpage", "/rest/api/search?cql=type%3Dpage",
        };
        foreach (var path in paths)
        {
            var join = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var page = await t.GetJsonAsync($"{path}{join}start=1&limit=1&expand=version");
            var links = page.GetProperty("_links");
            Assert.True(links.TryGetProperty("prev", out var prev), $"{path}: no prev");
            Assert.True(links.TryGetProperty("next", out var next), $"{path}: no next");
            Assert.Contains("start=0", prev.GetString(), StringComparison.Ordinal);
            Assert.Contains("start=2", next.GetString(), StringComparison.Ordinal);
            Assert.Contains("expand=version", next.GetString(), StringComparison.Ordinal);
            Assert.Equal("https://localhost", links.GetProperty("base").GetString());
        }
        // Inside a map each list has links of its own too.
        var map = await t.GetJsonAsync($"/rest/api/content/{folder}/child?expand=page&start=1&limit=1");
        Assert.True(map.GetProperty("page").GetProperty("_links").TryGetProperty("prev", out _));
        Assert.True(map.GetProperty("page").GetProperty("_links").TryGetProperty("next", out _));
    }

    // 5. Both bodies the contract allows for labels (a label, or a list), and that JSON sent as a JSON string.
    [Fact]
    public async Task LabelBodies_OneOrMany()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" });
        var id = (await (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "P", space = new { key = "LAB" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString();
        var path = $"/rest/api/content/{id}/label";
        async Task<int> SendAsync(string json, string type = "application/json") =>
            (await (await t.PostAsync(path, new StringContent(json, Encoding.UTF8, type), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("size").GetInt32();
        Assert.Equal(1, await SendAsync("""{"prefix":"global","name":"one"}"""));
        Assert.Equal(3, await SendAsync("""[{"prefix":"global","name":"two"},{"name":"three"}]"""));
        Assert.Equal(4, await SendAsync("\"[{\\\"name\\\":\\\"four\\\"}]\""));
        Assert.Equal(5, await SendAsync("﻿{\"name\":\"five\"}"));
        // (StringContent sends "application/json; charset=utf-8", as generated clients do.)
        await (await t.PostAsync(path, new StringContent("", Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await t.PostAsync(path, new StringContent("42", Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    // Q-0018: a part named file is a file whether or not it has a file name, as clients generated from the published files send it.
    [Fact]
    public async Task FilePartWithoutAFileName_IsAFile()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" });
        var page = (await (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "P", space = new { key = "LAB" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString();
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x0D, 0x0A, 0x80];
        async Task<HttpResponseMessage> PostAsync(string path, byte[] data, string? type = null, string? fileName = null, params (string Name, string Value)[] fields)
        {
            var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(data);
            if (type is not null) part.Headers.ContentType = new MediaTypeHeaderValue(type);
            if (fileName is null) form.Add(part, "file"); else form.Add(part, "file", fileName);
            foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
            req.Headers.Add("X-Atlassian-Token", "no-check");
            return await t.SendAsync(req, Ct);
        }
        var path = $"/rest/api/content/{page}/child/attachment";
        var made = (await (await PostAsync(path, bytes, "image/png", null, ("comment", "unnamed"))).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0];
        var id = made.GetProperty("id").GetString();
        Assert.Equal($"attachment-{id}", made.GetProperty("title").GetString());
        Assert.Equal("application/octet-stream", made.GetProperty("extensions").GetProperty("mediaType").GetString()); // the part's type is ignored
        Assert.Equal(("unnamed", bytes.Length), (made.GetProperty("extensions").GetProperty("comment").GetString(), made.GetProperty("extensions").GetProperty("fileSize").GetInt32()));
        var download = made.GetProperty("_links").GetProperty("download").GetString()!;
        Assert.Equal(bytes, await t.GetByteArrayAsync(download, Ct)); // as received, not through a text decoder
        // A second unnamed part never clashes.
        Assert.NotEqual(id, (await (await PostAsync(path, [1, 2], null)).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0].GetProperty("id").GetString());

        // New bytes without a file name keep the title and the media type.
        var named = (await (await PostAsync(path, [7], "text/csv", "data.csv")).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0];
        var nid = named.GetProperty("id").GetString();
        var v2 = await (await PostAsync($"{path}/{nid}/data", bytes, "image/png")).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("data.csv", "text/csv"), (v2.GetProperty("title").GetString(), v2.GetProperty("extensions").GetProperty("mediaType").GetString()));
        Assert.Equal(bytes, await t.GetByteArrayAsync(v2.GetProperty("_links").GetProperty("download").GetString()!, Ct));
        // Zero file parts, or two, give 400.
        var none = new MultipartFormDataContent { { new StringContent("x"), "comment" } };
        var noneReq = new HttpRequestMessage(HttpMethod.Post, $"{path}/{nid}/data") { Content = none };
        noneReq.Headers.Add("X-Atlassian-Token", "no-check");
        await (await t.SendAsync(noneReq, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        var two = new MultipartFormDataContent { { new ByteArrayContent([1]), "file" }, { new ByteArrayContent([2]), "file" } };
        var twoReq = new HttpRequestMessage(HttpMethod.Post, $"{path}/{nid}/data") { Content = two };
        twoReq.Headers.Add("X-Atlassian-Token", "no-check");
        await (await t.SendAsync(twoReq, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    // Q-0017: scan cursors are always strings.
    [Fact]
    public async Task ScanCursors_AreStrings()
    {
        var (token, _) = await AdminTokenAsync();
        var t = Bearer(token);
        await t.PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" });
        for (var i = 0; i < 3; i++) await t.PostJsonAsync("/rest/api/content", new { type = "page", title = $"P{i}", space = new { key = "LAB" } });
        var first = await t.GetJsonAsync("/rest/api/content/scan?limit=1");
        Assert.Equal(JsonValueKind.String, first.GetProperty("nextCursor").ValueKind);
        var second = await t.GetJsonAsync("/rest/api/content/scan?limit=1&cursor=" + first.GetProperty("nextCursor").GetString());
        Assert.Equal((JsonValueKind.String, JsonValueKind.String, JsonValueKind.String),
            (second.GetProperty("cursor").ValueKind, second.GetProperty("nextCursor").ValueKind, second.GetProperty("prevCursor").ValueKind));
    }

    // 6. Basic with a token in the password field acts like Bearer; the user name must be the owner's.
    [Fact]
    public async Task BasicWithAToken_ActsLikeBearer_EvenWithSignInCodes()
    {
        var (token, _) = await AdminTokenAsync();
        Assert.Equal("boss", (await BasicToken("BOSS", token).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        Assert.Equal("LAB", (await (await BasicToken("boss", token).PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" })).JsonAsync(HttpStatusCode.OK)).GetProperty("key").GetString());
        var wrongUser = await BasicToken("someone", token).GetAsync("/rest/api/user/current", Ct);
        await wrongUser.ErrorAsync(HttpStatusCode.Unauthorized);
        Assert.Empty(wrongUser.Headers.WwwAuthenticate);
        await (await BasicToken("boss", "kbt_not-a-real-token").GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        // A refused token was no password guess: the account is not locked out.
        await _f.WithDbAsync(async db => Assert.Equal(0, (await db.Users.SingleAsync(u => u.UserName == "boss", Ct)).AccessFailedCount));
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == Kb.Core.Audit.AuditActions.TokenUse, Ct)));
    }

    // 6. Failed Basic attempts count against the sign-in limit per address; past it 429 with Retry-After before any credential check.
    [Fact]
    public async Task FailedBasic_IsLimited_BeforeAnyCredentialCheck_AndNoMore()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:SignInsPerMinutePerAddress"] = "3" });
        await UserAsync(f, "ravi");
        // Right credentials go through and do not count.
        Assert.Equal("ravi", (await Basic(f, "ravi").GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        var wrong = Basic(f, "ravi", "wrong-password");
        for (var i = 0; i < 3; i++) await (await wrong.GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        for (var i = 0; i < 3; i++)
        {
            var limited = await wrong.GetAsync("/rest/api/user/current", Ct);
            await limited.ErrorAsync(HttpStatusCode.TooManyRequests);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
        // Past the limit nothing is checked: the account saw three wrong passwords, not six, and right credentials wait too.
        await f.WithDbAsync(async db =>
        {
            Assert.Equal(3, (await db.Users.SingleAsync(u => u.UserName == "ravi", Ct)).AccessFailedCount);
            Assert.Equal(3, await db.AuditEntries.CountAsync(a => a.Action == Kb.Core.Audit.AuditActions.BasicRefused && a.DataJson.Contains("wrong password"), Ct));
        });
        await (await Basic(f, "ravi").GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.TooManyRequests);
        // No more broadly: calls without Basic from the same address, and other sign-ins, are not held back.
        Assert.Equal("anonymous", (await f.NewClient().GetJsonAsync("/rest/api/user/anonymous")).GetProperty("type").GetString());
        var session = f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ravi", password = Password }, Ct)).StatusCode);
        Assert.Equal("ravi", (await session.GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
    }
}
