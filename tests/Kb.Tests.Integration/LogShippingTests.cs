using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kb.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kb.Tests.Integration;

/// <summary>
/// Audit entries shipped to the local log file, to syslog and to an HTTPS collector.
/// The background shipper is taken out of the app and each test runs it itself, so "before it shipped" is a fixed point.
/// </summary>
public sealed class LogShippingTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/logs";
    private const string Secret = "collector-secret-value";
    private readonly string _dbPath = KbAppFactory.NewDbPath();
    private readonly Collector _collector = new();
    private readonly KbAppFactory _f;

    public LogShippingTests() => _f = Instance();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string LogFolder => _dbPath + ".logs";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private KbAppFactory Instance() => new(_dbPath, services: s =>
    {
        foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList()) s.Remove(d);
        s.AddHttpClient(HttpsLogShipperKind.ClientName).ConfigurePrimaryHttpMessageHandler(() => _collector);
    });

    /// <summary>Stands in for an HTTPS collector: keeps what was posted, or answers 503 while it is "down".</summary>
    private sealed class Collector : HttpMessageHandler
    {
        public List<(Uri Url, string? Header, string Body)> Posts { get; } = [];
        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Posts.Add((request.RequestUri!, request.Headers.TryGetValues("X-Collector-Key", out var v) ? v.Single() : null, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static Task<int> ShipAsync(KbAppFactory f) => f.Services.GetRequiredService<LogShipperService>().RunOnceAsync(Ct);

    private static async Task<JsonElement> AddAsync(HttpClient admin, string kind, object settings, object? secrets = null, string name = "Collector")
    {
        var r = await admin.PostAsJsonAsync($"{Url}/targets", new { name, kind, settings, secrets, enabled = true }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> TargetAsync(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    private List<JsonElement> FileLines() => !Directory.Exists(LogFolder) ? [] :
        [.. Directory.GetFiles(LogFolder, "audit-*.jsonl").Order().SelectMany(File.ReadAllLines).Select(l => JsonDocument.Parse(l).RootElement)];

    private static List<string> Actions(IEnumerable<JsonElement> events) => [.. events.Select(e => e.GetProperty("action").GetString()!)];

    [Fact]
    public async Task FirstStart_HasTheLocalFile_AndSignInsLandInIt_OneJsonLineEach()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "breakglass", password = "pw-never-logged" }, Ct);
        await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "nobody-here", password = "pw-never-logged" }, Ct);
        await admin.PostAsync("/api/v1/auth/signout", null, Ct);
        admin = await _f.SignedInBreakGlassAsync();

        var local = await TargetAsync(admin, "Local log file");
        Assert.Equal(("file", true), (local.GetProperty("kind").GetString(), local.GetProperty("enabled").GetBoolean()));
        Assert.True(await ShipAsync(_f) > 0);

        var lines = FileLines();
        Assert.Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i), lines.Select(l => l.GetProperty("sequence").GetInt64())); // all of them, in order
        var actions = Actions(lines);
        Assert.Contains("auth.signin", actions);
        Assert.Contains("auth.breakglass", actions);
        Assert.Contains("auth.signout", actions);
        Assert.Equal(2, actions.Count(a => a == "auth.signin.failed"));

        var ok = lines.First(l => l.GetProperty("action").GetString() == "auth.signin");
        Assert.Equal("breakglass", ok.GetProperty("actor").GetString());
        Assert.Equal(64, ok.GetProperty("hash").GetString()!.Length);
        Assert.False(string.IsNullOrEmpty(ok.GetProperty("server").GetString()));
        var unknown = lines.Last(l => l.GetProperty("action").GetString() == "auth.signin.failed");
        Assert.Equal(("nobody-here", "unknown user"), (unknown.GetProperty("data").GetProperty("userName").GetString(), unknown.GetProperty("data").GetProperty("reason").GetString()));
        Assert.DoesNotContain("pw-never-logged", string.Join("\n", lines.Select(l => l.GetRawText())));

        Assert.Equal(0, await ShipAsync(_f)); // nothing twice
        Assert.Equal(lines.Count, FileLines().Count);
        Assert.Equal(lines.Count, (await TargetAsync(admin, "Local log file")).GetProperty("lastSequence").GetInt64());
    }

    [Fact]
    public async Task Syslog_Udp_SendsOneRfc5424MessagePerEntry_FromNowOn()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Before" }, Ct); // history is not sent to a new target
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await AddAsync(admin, "syslog", new { host = "127.0.0.1", port = $"{port}", protocol = "udp" });
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "After" }, Ct);

        await ShipAsync(_f);
        var received = new List<string>();
        for (var waited = 0; received.Count < 2 && waited < 2000; waited += 20)
        {
            while (udp.Available > 0) received.Add(Encoding.UTF8.GetString((await udp.ReceiveAsync(Ct)).Buffer));
            if (received.Count < 2) await Task.Delay(20, Ct);
        }

        Assert.Equal(["logs.target.add", "space.create"], received.Select(m => m.Split(' ')[5]));
        Assert.All(received, m => Assert.Matches(@"^<110>1 \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \S+ kb - \S+ - \{", m)); // facility 13 (audit), severity 6 (info)
        Assert.Equal("space.create", JsonDocument.Parse(received[1][received[1].IndexOf('{')..]).RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Syslog_Tcp_Unreachable_KeepsItsPlace_ShowsTheError_ThenCatchesUp()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // the collector is down
        await AddAsync(admin, "syslog", new { host = "127.0.0.1", port = $"{port}", protocol = "tcp" });
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "One" }, Ct);

        await ShipAsync(_f);
        var down = await TargetAsync(admin, "Collector");
        Assert.False(string.IsNullOrEmpty(down.GetProperty("lastError").GetString()));
        var stuckAt = down.GetProperty("lastSequence").GetInt64();
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Two" }, Ct);

        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync(Ct);
        await ShipAsync(_f);
        using var client = await accept;
        using var reader = new StreamReader(client.GetStream());
        var text = await reader.ReadToEndAsync(Ct);
        listener.Stop();

        var messages = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["logs.target.add", "space.create", "space.create"], messages.Select(m => m.Split(' ')[5]));
        var up = await TargetAsync(admin, "Collector");
        Assert.Equal(JsonValueKind.Null, up.GetProperty("lastError").ValueKind);
        Assert.Equal(stuckAt + 3, up.GetProperty("lastSequence").GetInt64());
    }

    [Fact]
    public async Task Https_PostsABatch_WithTheSecretHeader_AndRetriesAfterAnError()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var bad = await admin.PostAsJsonAsync($"{Url}/targets", new { name = "Plain", kind = "https", settings = new { url = "http://collector.example/in" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True((await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("url", out _));

        await AddAsync(admin, "https", new { url = "https://collector.example/in", headerName = "X-Collector-Key" }, new { headerValue = Secret });
        _collector.Down = true;
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "One" }, Ct);
        await ShipAsync(_f);
        Assert.Contains("503", (await TargetAsync(admin, "Collector")).GetProperty("lastError").GetString());

        _collector.Down = false;
        await ShipAsync(_f);
        var post = Assert.Single(_collector.Posts);
        Assert.Equal(("https://collector.example/in", Secret), (post.Url.ToString(), post.Header));
        Assert.Equal(["logs.target.add", "space.create"], Actions(JsonDocument.Parse(post.Body).RootElement.EnumerateArray()));
    }

    [Fact]
    public async Task Test_SendsOneTestEvent_AndAnswersTheError()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var id = (await AddAsync(admin, "https", new { url = "https://collector.example/in" })).GetProperty("id").GetString();

        var ok = await (await admin.PostAsync($"{Url}/targets/{id}/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(["logs.test"], Actions(JsonDocument.Parse(_collector.Posts.Single().Body).RootElement.EnumerateArray()));

        _collector.Down = true;
        var failed = await (await admin.PostAsync($"{Url}/targets/{id}/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("503", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Target_Disabled_ShipsNothing_ThenResumesWhereItStopped()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var id = (await AddAsync(admin, "https", new { url = "https://collector.example/in" })).GetProperty("id").GetString();
        await ShipAsync(_f);
        var before = _collector.Posts.Count;

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Url}/targets/{id}", new { name = "Collector", settings = new { url = "https://collector.example/in" }, enabled = false }, Ct)).StatusCode);
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "While off" }, Ct);
        await ShipAsync(_f);
        Assert.Equal(before, _collector.Posts.Count);

        await admin.PutAsJsonAsync($"{Url}/targets/{id}", new { name = "Collector", settings = new { url = "https://collector.example/in" }, enabled = true }, Ct);
        await ShipAsync(_f);
        Assert.Contains("space.create", Actions(_collector.Posts.Skip(before).SelectMany(p => JsonDocument.Parse(p.Body).RootElement.EnumerateArray().ToList())));
    }

    [Fact]
    public async Task TwoInstances_OnlyOneShips()
    {
        await using var second = Instance();
        var admin = await _f.SignedInBreakGlassAsync();
        await second.SignedInBreakGlassAsync();
        var id = (await AddAsync(admin, "https", new { url = "https://collector.example/in" })).GetProperty("id").GetGuid();

        Assert.True(await _f.Services.GetRequiredService<LogShipperService>().ClaimAsync(id, Ct)); // the first server holds this target
        await ShipAsync(second);
        Assert.Empty(_collector.Posts);
        await ShipAsync(_f);
        Assert.NotEmpty(_collector.Posts);
    }

    [Fact]
    public async Task Secret_IsNeverReturned_AndChangesAreAudited_WithoutValues()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var add = await admin.PostAsJsonAsync($"{Url}/targets", new { name = "Collector", kind = "https", settings = new { url = "https://collector.example/in", headerName = "X-Collector-Key" }, secrets = new { headerValue = Secret }, enabled = true }, Ct);
        var addBody = await add.Content.ReadAsStringAsync(Ct);
        var id = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString();
        var put = await admin.PutAsJsonAsync($"{Url}/targets/{id}", new { name = "Collector 2", settings = new { url = "https://collector.example/in", headerName = "X-Collector-Key" }, secrets = new { headerValue = (string?)null }, enabled = true }, Ct);
        var list = await admin.GetStringAsync(Url, Ct);
        Assert.All(new[] { addBody, await put.Content.ReadAsStringAsync(Ct), list }, b => Assert.DoesNotContain(Secret, b));
        Assert.Equal("headerValue", (await TargetAsync(admin, "Collector 2")).GetProperty("secretsSet").EnumerateArray().Single().GetString()); // null kept it
        Assert.Contains(JsonDocument.Parse(list).RootElement.GetProperty("kinds").EnumerateArray(), k => k.GetProperty("kind").GetString() == "syslog");

        await ShipAsync(_f);
        Assert.Equal(Secret, _collector.Posts.Last().Header);
        await _f.WithDbAsync(async db =>
        {
            var stored = await db.LogTargets.SingleAsync(t => t.Name == "Collector 2", Ct);
            Assert.False(string.IsNullOrEmpty(stored.SecretsProtected));
            Assert.DoesNotContain(Secret, stored.SecretsProtected + stored.SettingsJson);
        });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/targets/{id}", Ct)).StatusCode);

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action.StartsWith("logs.target.")).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(["logs.target.add", "logs.target.change", "logs.target.remove"], entries.Select(e => e.Action));
            Assert.All(entries, e => { Assert.Contains("https", e.DataJson); Assert.DoesNotContain(Secret, e.DataJson); Assert.DoesNotContain("collector.example", e.DataJson); });
        });
    }

    [Fact]
    public async Task Routes_NotAdmin_403_AndUnknownKindOrMissingField_400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Url}/targets", new { name = "x", kind = "pigeon", settings = new { } }, Ct)).StatusCode);
        var noHost = await admin.PostAsJsonAsync($"{Url}/targets", new { name = "x", kind = "syslog", settings = new { port = "514", protocol = "udp" } }, Ct);
        Assert.True((await noHost.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("host", out _));
        var badProtocol = await admin.PostAsJsonAsync($"{Url}/targets", new { name = "x", kind = "syslog", settings = new { host = "h", protocol = "carrier" } }, Ct);
        Assert.True((await badProtocol.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("protocol", out _));

        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Kb.Core.Identity.KbUser>>();
            Assert.True((await users.CreateAsync(new Kb.Core.Identity.KbUser { UserName = "tech", DisplayName = "Tech", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "tech", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"{Url}/targets", new { name = "x", kind = "file", settings = new { path = "x" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"{Url}/targets/{id}", new { name = "x", settings = new { } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{Url}/targets/{id}/test", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"{Url}/targets/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task SignIn_TooManyFromOneAddress_Gets429()
    {
        await using var strict = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:SignInsPerMinutePerAddress"] = "3" });
        var c = strict.NewClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "nobody", password = "x" }, Ct)).StatusCode);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live", Ct)).StatusCode);
    }
}
