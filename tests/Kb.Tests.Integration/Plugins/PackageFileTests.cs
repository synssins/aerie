using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>
/// A package built the way the developer guide says (tools/pack-plugin.ps1), installed through the Plugins screen's route,
/// loaded after the restart, used, then removed. KB_PLUGIN_PACKAGE names the .kbplugin file; without it the test does nothing.
/// For the sample: KB_PLUGIN_PACKAGE=artifacts/plugins/kb.sample-2.4.0.kbplugin.
/// </summary>
public sealed class PackageFileTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private readonly ITestOutputHelper output;
    private readonly Seen _seen = new();
    private KbAppFactory _f;

    /// <summary>The model server: it answers {"ok":true} and keeps the headers of what it was sent.</summary>
    private sealed class Seen : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Headers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Headers.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            const string answer = """{"model":"stub","choices":[{"message":{"content":"{\"ok\":true}"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    public PackageFileTests(ITestOutputHelper output)
    {
        this.output = output;
        var seen = _seen;
        _f = new(KbAppFactory.NewDbPath(), services: s => s.AddHttpClient(AiService.ClientName).ConfigurePrimaryHttpMessageHandler(() => seen));
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task ABuiltPackage_Installs_Loads_Works_AndIsRemoved()
    {
        if (Environment.GetEnvironmentVariable("KB_PLUGIN_PACKAGE") is not { Length: > 0 } path) return;
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var read = PluginPackageReader.Read(bytes);
        Assert.True(read.Ok, read.Problem?.Message);
        var id = read.Value!.Manifest.Id;
        output.WriteLine($"{id} {read.Value.Manifest.Version} sha256 {read.Value.Sha256}, {bytes.Length} bytes");

        var admin = await _f.SignedInBreakGlassAsync();
        var up = await admin.PostAsync(Url, new ByteArrayContent(bytes), Ct);
        output.WriteLine($"upload: {(int)up.StatusCode} {await up.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        _f = await _f.RestartAsync();
        admin = await _f.SignedInBreakGlassAsync();
        var plugin = (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);
        output.WriteLine($"after restart: {plugin.GetProperty("state")} loaded={plugin.GetProperty("loaded")} reason={(plugin.TryGetProperty("reason", out var why) ? why.ToString() : "")}");
        Assert.True(plugin.GetProperty("loaded").GetBoolean());

        if (id == "kb.sample")
        {
            // The sample's own route, on a page of a new space: acknowledge it and count.
            var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ack", name = "Ack" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Read me", contentJson = """{"type":"doc","content":[]}""" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            var ack = await admin.PostAsync($"/api/v1/plugins/kb.sample/pages/{page}/ack", null, Ct);
            output.WriteLine($"ack: {(int)ack.StatusCode} {await ack.Content.ReadAsStringAsync(Ct)} action={string.Join(",", ack.Headers.TryGetValues("X-Kb-Action", out var a) ? a : [])}");
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            Assert.Equal(1, (await ack.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("count").GetInt32());
            var script = await admin.GetAsync($"/plugins/{id}/{read.Value.Manifest.Version}/ui/index.js", Ct);
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            output.WriteLine($"ui script: {(int)script.StatusCode}");
        }

        if (id == "example.gateway-headers")
        {
            // The developer guide's walkthrough: settings and secrets as an Admin sets them, then the AI's call carries the headers.
            (await admin.PutAsJsonAsync($"{Url}/{id}/settings", new { values = new Dictionary<string, string?>
            {
                ["Host"] = "llm.example.com", ["Header1Name"] = "X-Client-Id", ["Header1Value"] = "id-123", ["Header2Name"] = "X-Client-Secret", ["Header2Value"] = "secret-456",
            } }, Ct)).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync("/api/v1/admin/ai", new { settings = new
            {
                enabled = true, baseUrl = "https://llm.example.com", model = "m", timeoutSeconds = 30, maxTokens = 100, temperature = 0, reasoning = false, ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset,
                maxInputChars = 200000, userPerMinute = 10, tokenPerMinute = 10, importConcurrency = 1, features = new { import = "Off", format = "Off", search = "Off" },
            } }, Ct)).EnsureSuccessStatusCode();
            var test = await (await admin.PostAsync("/api/v1/admin/ai/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            output.WriteLine($"ai test: {test}; headers sent: {string.Join(", ", _seen.Headers.SelectMany(h => h.Keys))}");
            Assert.True(test.GetProperty("ok").GetBoolean());
            Assert.Equal("id-123", _seen.Headers.Single()["X-Client-Id"]);
            Assert.Equal("secret-456", _seen.Headers.Single()["X-Client-Secret"]);
        }

        var removed = await admin.DeleteAsync($"{Url}/{id}?deleteData=true", Ct);
        output.WriteLine($"remove: {(int)removed.StatusCode}");
        Assert.True(removed.IsSuccessStatusCode);
        _f = await _f.RestartAsync();
        admin = await _f.SignedInBreakGlassAsync();
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("plugins").EnumerateArray(),
            p => p.GetProperty("id").GetString() == id && p.GetProperty("loaded").GetBoolean());
    }
}
