using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kb.Core.Access;
using Kb.Core.Identity;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The authorization matrix: every route of the API, asked by each of seven callers (not signed in, signed in with no role, Reader,
/// Contributor, Supervisor, Owner, an Admin without a role), once against a normal space and once against a sensitive one.
/// The expected answers are data: Security/matrix/&lt;area&gt;.json, one entry per action. They follow from the design's rule for the kind
/// of action, not from the handlers:
/// - content of a space: not signed in 401; no role in the space 404 (the space is not disclosed); a role below the action's minimum 403;
///   otherwise it works. An Admin is Owner of a normal space and has nothing in a sensitive one.
/// - administration: not signed in 401; anyone who is not an Admin 403.
/// - a person's own things: not signed in 401.
/// "allowed" in an entry means the caller was let in (neither 401 nor 403); it is used where a complete request would change the server
/// for the callers that follow.
/// A mismatch is a finding for the owner. Neither side is changed to make this pass without their word: a mismatch the owner has not
/// decided yet is listed in its entry under "known", and the test fails when it stops happening as well as when a new one appears.
/// </summary>
public sealed class MatrixTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private const string RichDoc = """{"type":"doc","content":[{"type":"codeBlock","attrs":{"language":"bash"},"content":[{"type":"text","text":"ls"}]},{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Server"}]}]}]}]}]}""";
    private const string Password = "user-password-0123456789";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The callers, weakest first. The number is the role they hold in the space; -1 not signed in, 0 no role, 9 an Admin without a role.</summary>
    private static readonly (string Name, int Level)[] Callers =
        [("anonymous", -1), ("outsider", 0), ("reader", 1), ("contributor", 2), ("supervisor", 3), ("owner", 4), ("admin", 9)];

    private sealed record Known(string Caller, string Space, int Got, string Why);

    private sealed record Entry(string Action, string Method, string Path, JsonElement? Body, string? Upload, Dictionary<string, JsonElement> Expect,
        Dictionary<string, JsonElement>? Sensitive, List<Known>? Known, string? Scope, bool Last, bool FreshSession, string? Note)
    {
        public string Area { get; set; } = "";
    }

    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private readonly Dictionary<string, HttpClient> _callers = [];
    private HttpClient _owner = null!, _breakGlass = null!;
    private string _spareKey = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<Entry> Load([CallerFilePath] string here = "")
    {
        var entries = new List<Entry>();
        foreach (var file in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(here)!, "matrix"), "*.json").Order(StringComparer.Ordinal))
        {
            var list = JsonSerializer.Deserialize<List<Entry>>(System.IO.File.ReadAllText(file), Web)!;
            foreach (var e in list) e.Area = Path.GetFileNameWithoutExtension(file);
            entries.AddRange(list);
        }
        // An entry marked "last" changes what a caller may do afterwards, so it runs after everything else.
        return [.. entries.Where(e => !e.Last), .. entries.Where(e => e.Last)];
    }

    public async ValueTask InitializeAsync()
    {
        _breakGlass = await _f.SignedInBreakGlassAsync();
        foreach (var (name, level) in Callers)
        {
            if (level < 0) { _callers[name] = _f.NewClient(); continue; }
            await NewUserAsync(name, admin: level == 9);
            _callers[name] = await SignInAsync(name);
        }
        _owner = _callers["owner"];
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> NewUserAsync(string name, bool admin = false)
    {
        await using var scope = _f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = name, DisplayName = name, IsAdmin = admin, CreatedAt = DateTimeOffset.UtcNow };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private async Task<HttpClient> SignInAsync(string name)
    {
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>A space in which each caller has the role of their name. Made as a normal space, then marked sensitive: only an Admin can do that.</summary>
    private async Task<Guid> SpaceAsync(bool sensitive)
    {
        var key = sensitive ? "vault" : "desk";
        var space = (await (await _breakGlass.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        foreach (var (name, level) in Callers.Where(c => c.Level is >= 1 and <= 4))
        {
            var me = (await _callers[name].GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
            (await _breakGlass.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = me.ToString(), role = ((SpaceRole)level).ToString() }, Ct)).EnsureSuccessStatusCode();
        }
        if (sensitive) (await _breakGlass.PatchAsJsonAsync($"/api/v1/admin/spaces/{space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        return space;
    }

    private static HttpRequestMessage Upload(string path, byte[] bytes, string name)
    {
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
    }

    // ---- what a request is made against: fresh for each request and made by the Owner, so every caller meets the same state

    private async Task<Guid> PageAsync(Guid space, string kind = "Page", string doc = Doc)
    {
        var r = await _owner.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind, title = $"{kind} {Guid.NewGuid():N}", contentJson = kind == "Page" ? doc : null }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    private async Task<Guid> SentAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var r = await _owner.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }
    }

    private async Task<string> ValueAsync(string name, Guid space)
    {
        switch (name)
        {
            case "space": return space.ToString();
            case "newGuid": return Guid.NewGuid().ToString();
            case "newKey": return "m" + Guid.NewGuid().ToString("N")[..10];
            case "page": return (await PageAsync(space)).ToString();
            case "folder": return (await PageAsync(space, "Folder")).ToString();
            case "richPage": return (await PageAsync(space, doc: RichDoc)).ToString();
            case "deletedPage":
            {
                var page = await PageAsync(space);
                Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
                return page.ToString();
            }
            case "attachment": return (await SentAsync(Upload($"/api/v1/pages/{await PageAsync(space)}/attachments", "hello"u8.ToArray(), "note.txt"))).ToString();
            case "image": return (await SentAsync(Upload($"/api/v1/spaces/{space}/images", Png, "dot.png"))).ToString();
            case "assignment":
            {
                var principal = Guid.NewGuid().ToString();
                (await _owner.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = principal, role = "Reader" }, Ct)).EnsureSuccessStatusCode();
                var roles = await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{space}/roles", Ct);
                return roles.EnumerateArray().Single(a => a.GetProperty("principalId").GetString() == principal).GetProperty("id").GetGuid().ToString();
            }
            case "accessRequest":
            {
                // Someone without a role asked for access. Written straight to the database: who may ask is its own entry.
                var request = new AccessRequest { SpaceId = space, RequesterId = await NewUserAsync("asker" + Guid.NewGuid().ToString("N")[..8]), RequestedRole = SpaceRole.Reader, Status = AccessRequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
                await _f.WithDbAsync(async db => { db.AccessRequests.Add(request); await db.SaveChangesAsync(Ct); });
                return request.Id.ToString();
            }
            case "spareSpace":
            {
                // A space of its own for the one request that may delete it. Its phrase is asked for right after, for the body.
                _spareKey = "m" + Guid.NewGuid().ToString("N")[..10];
                var made = await _breakGlass.PostAsJsonAsync("/api/v1/spaces", new { key = _spareKey, name = "Spare" }, Ct);
                Assert.Equal(HttpStatusCode.Created, made.StatusCode);
                return (await made.Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id.ToString();
            }
            case "spareSpacePhrase": return "delete " + _spareKey;
            case "deletedSpace":
            {
                var id = await ValueAsync("spareSpace", space);
                Assert.Equal(HttpStatusCode.NoContent, (await _breakGlass.PostAsJsonAsync($"/api/v1/admin/spaces/{id}/delete", new { confirm = "delete " + _spareKey }, Ct)).StatusCode);
                return id;
            }
            case "victim": return (await NewUserAsync("victim" + Guid.NewGuid().ToString("N")[..8])).ToString();
            case "token":
            {
                var made = await _owner.PostAsJsonAsync("/api/v1/tokens", new { name = "matrix " + Guid.NewGuid().ToString("N")[..8], kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct);
                Assert.Equal(HttpStatusCode.Created, made.StatusCode);
                return (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("info").GetProperty("id").GetGuid().ToString();
            }
            case "announcement":
            {
                var made = await _breakGlass.PostAsJsonAsync("/api/v1/announcements", new { title = "Matrix", body = "For removal." }, Ct);
                Assert.Equal(HttpStatusCode.Created, made.StatusCode);
                return (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().ToString();
            }
            default: throw new InvalidOperationException($"The matrix names {{{name}}}, which the test does not know how to make.");
        }
    }

    private async Task<string> FillAsync(string text, Guid space)
    {
        for (var open = text.IndexOf('{', StringComparison.Ordinal); open >= 0; open = text.IndexOf('{', open + 1))
        {
            var close = text.IndexOf('}', open);
            if (close < 0) break;
            var name = text[(open + 1)..close];
            if (name.Length == 0 || !name.All(char.IsLetter)) continue; // JSON of a body, not a name to fill in
            var value = await ValueAsync(name, space);
            text = text[..open] + value + text[(close + 1)..];
            open += value.Length - 1;
        }
        return text;
    }

    private async Task<HttpRequestMessage> RequestAsync(Entry e, Guid space)
    {
        var path = await FillAsync(e.Path, space);
        if (e.Upload is not null) return Upload(path, e.Upload == "png" ? Png : "hello"u8.ToArray(), e.Upload == "png" ? "dot.png" : "note.txt");
        var request = new HttpRequestMessage(new HttpMethod(e.Method), path);
        if (e.Body is { } body) request.Content = new StringContent(await FillBodyAsync(body, space), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Names in a body are filled in its string values only, so the braces of a document stored as text stay as they are.</summary>
    private async Task<string> FillBodyAsync(JsonElement body, Guid space)
    {
        var filled = new Dictionary<string, object?>();
        foreach (var p in body.EnumerateObject())
            filled[p.Name] = p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s && s.StartsWith('{') && s.EndsWith('}') && s[1..^1].All(char.IsLetter) ? await ValueAsync(s[1..^1], space) : p.Value;
        return JsonSerializer.Serialize(filled);
    }

    private static bool Met(JsonElement expected, HttpStatusCode got) =>
        expected.ValueKind == JsonValueKind.Number ? (int)got == expected.GetInt32() : got is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

    private async Task<List<string>> MismatchesAsync(bool sensitive)
    {
        var space = await SpaceAsync(sensitive);
        var where = sensitive ? "sensitive" : "normal";
        var wrong = new List<string>();
        foreach (var e in Load().Where(e => !sensitive || e.Scope != "server")) // what does not depend on a space is asked once
        {
            foreach (var (name, level) in Callers)
            {
                var expected = sensitive && e.Sensitive?.GetValueOrDefault(name) is { ValueKind: not JsonValueKind.Undefined } other ? other : e.Expect[name];
                // Signing out ends the session it is sent with, so that caller gets a session of its own for it.
                var client = e.FreshSession && level >= 0 ? await SignInAsync(name) : _callers[name];
                using var request = await RequestAsync(e, space);
                var got = (await client.SendAsync(request, Ct)).StatusCode;
                var known = e.Known?.FirstOrDefault(k => k.Caller == name && (k.Space == "both" || k.Space == where));
                if (known is not null)
                {
                    if ((int)got != known.Got) wrong.Add($"{e.Area}: {e.Action} as {name} ({where}): the known mismatch ({known.Got}) no longer happens, it answers {(int)got}. Take it out of \"known\".");
                    continue;
                }
                if (!Met(expected, got)) wrong.Add($"{e.Area}: {e.Action} as {name} ({where}): {(int)got}, expected {expected}  [{e.Method} {request.RequestUri}]");
            }
        }
        return wrong;
    }

    /// <summary>Every mismatch on a line of its own: the list is the finding.</summary>
    private static void None(List<string> wrong) => Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

    [Fact]
    public async Task NormalSpace_EveryEntryAnswersAsExpected() => None(await MismatchesAsync(sensitive: false));

    [Fact]
    public async Task SensitiveSpace_EveryEntryAnswersAsExpected() => None(await MismatchesAsync(sensitive: true));

    /// <summary>A new route cannot slip past the matrix: every action that has a route has an entry, with that route's method, and every entry names all seven callers.</summary>
    [Fact]
    public void EveryRouteHasAnEntry()
    {
        var routes = _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(r => r.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal) && r.Metadata.GetMetadata<ActionAttribute>() is not null)
            .Select(r => (r.Metadata.GetMetadata<ActionAttribute>()!.Name, Method: r.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single())).ToList();
        Assert.True(routes.Count > 90, $"the route list was not read ({routes.Count})");
        var entries = Load();

        Assert.Empty(routes.Where(r => !entries.Any(e => e.Action == r.Name && e.Method == r.Method)).Select(r => $"{r.Method} {r.Name} has no entry"));
        Assert.Empty(entries.Where(e => !routes.Any(r => e.Action == r.Name)).Select(e => $"{e.Area}: {e.Action} is not an action with a route"));
        Assert.Empty(entries.Where(e => Callers.Any(c => !e.Expect.ContainsKey(c.Name))).Select(e => $"{e.Area}: {e.Action} does not name every caller"));
        Assert.Empty(entries.GroupBy(e => e.Action).Where(g => g.Count() > 1).Select(g => $"{g.Key} has {g.Count()} entries"));
    }

    /// <summary>A mismatch the owner has not decided yet is listed in its entry under "known". None is open now; this keeps a new one from going unseen.</summary>
    [Fact]
    public void KnownMismatches_AreTheOnesReported() =>
        Assert.Empty(Load().SelectMany(e => (e.Known ?? []).Select(k => $"{e.Action} as {k.Caller} ({k.Space}): {k.Got}")));
}
