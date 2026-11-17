using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web;
using Kb.Web.Import;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// File kinds a plugin claims (contract 0.6.0): a file of a kind the app does not read, that a loaded plugin reads, is not failed. It gets
/// a draft with one empty page, the AI is not called for it, and the plugin's contributors fill the page. A page left empty fails the
/// item, saying so. A kind the app reads is unchanged; scanners, sensitive spaces and the audit log apply as to any file.
/// </summary>
public sealed class ImportClaimTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";

    /// <summary>A model that counts its calls and drafts one page.</summary>
    private sealed class CountingModel(Seen seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref seen.ModelCalls);
            var content = new JsonObject
            {
                ["pages"] = new JsonArray(new JsonObject { ["title"] = "Text", ["blocks"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["text"] = "Some text." }) }),
                ["tags"] = new JsonArray(), ["apps"] = new JsonArray(), ["owner"] = "", ["reviewMonths"] = 0, ["destination"] = "inbox",
            }.ToJsonString();
            var answer = new JsonObject
            {
                ["model"] = "stub", ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Seen
    {
        public int ModelCalls;
        public readonly ConcurrentBag<(string File, byte[] Bytes, Actor Actor)> Files = [];
        public readonly ConcurrentBag<ImportDraft> Drafts = [];
    }

    /// <summary>Reads ".abc" and ".abz" files: one box naming the file and its size. Answers nothing for any other.</summary>
    private sealed class Reader(Seen seen, IActorAccessor actor) : IImportContributor
    {
        public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
        {
            await using var s = await file.OpenReadAsync(ct);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            seen.Files.Add((file.FileName, ms.ToArray(), actor.Current));
            if (!(file.FileName.EndsWith(".abc", StringComparison.Ordinal) || file.FileName.EndsWith(".abz", StringComparison.Ordinal))) return null;
            var node = new JsonObject { ["type"] = "claimBox", ["attrs"] = new JsonObject { ["label"] = $"{file.FileName} {file.Length}" } };
            return new ImportContribution([JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()]);
        }
    }

    /// <summary>Claims ".nil" and adds nothing.</summary>
    private sealed class Nothing : IImportContributor
    {
        public Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct) => Task.FromResult<ImportContribution?>(null);
    }

    private sealed class Drafts(Seen seen) : IDraftContributor
    {
        public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct) { seen.Drafts.Add(draft); return Task.FromResult<DraftContribution?>(null); }
    }

    /// <summary>Flags any file whose name holds "infected".</summary>
    private sealed class Scanner : IMalwareScanner
    {
        public string ProviderId => "kb.test.scanner";
        public Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken ct) =>
            Task.FromResult(fileName.Contains("infected", StringComparison.Ordinal) ? ScanVerdict.Infected : ScanVerdict.Clean);
    }

    private static readonly byte[] Binary = [0, 0xFF, 0x10, 0, 0xFE, 0x01, 0x80, 0x00, 0x7F, 0x02];
    private readonly Seen _seen = new();
    private readonly KbAppFactory _f;
    private HttpClient _admin = null!;
    private HttpClient _cara = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ImportClaimTests()
    {
        var regs = new PluginRegistrations();
        regs.Add("kb.test.reader", new ImportFileKindReg([".abc", ".abz", ".txt"])); // .txt: the app reads it, so the claim does not count
        regs.Add("kb.test.reader", new ImportContributorReg(typeof(Reader)));
        regs.Add("kb.test.second", new ImportFileKindReg([".abc"])); // claims a kind another plugin claims too, and reads nothing
        regs.Add("kb.test.nothing", new ImportFileKindReg([".nil"]));
        regs.Add("kb.test.nothing", new ImportContributorReg(typeof(Nothing)));
        regs.Add("kb.test.drafts", new DraftContributorReg(typeof(Drafts)));
        var seen = _seen;
        _f = new KbAppFactory(KbAppFactory.NewDbPath(), services: s =>
        {
            s.AddHttpClient(AiService.ClientName).ConfigurePrimaryHttpMessageHandler(() => new CountingModel(seen));
            s.AddSingleton<IRegistrations>(regs);
            s.AddSingleton(seen);
            s.AddSingleton<IMalwareScanner, Scanner>();
        });
    }

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        (await _admin.PutAsJsonAsync("/api/v1/admin/ai", new
        {
            settings = new
            {
                enabled = true, baseUrl = "http://model.local", model = "stub", timeoutSeconds = 30, maxTokens = 4096, temperature = 0, reasoning = false, ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset,
                maxInputChars = 200000, userPerMinute = 100, tokenPerMinute = 100, features = new { import = "Everyone", format = "Everyone", search = "Off" },
            },
        }, Ct)).EnsureSuccessStatusCode();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ops", name = "Ops" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = "cara", DisplayName = "cara", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        _cara = _f.NewClient();
        (await _cara.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "cara", password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Uploads one file, runs the worker, and answers the item's detail.</summary>
    private async Task<JsonElement> ImportAsync(object batch, string name, byte[] data)
    {
        var id = (await (await _cara.PostAsJsonAsync("/api/v1/imports", batch, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var body = new ByteArrayContent(data);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", name);
        Assert.Equal(HttpStatusCode.OK, (await _cara.PostAsync($"/api/v1/imports/{id}/files", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _cara.PostAsync($"/api/v1/imports/{id}/start", null, Ct)).StatusCode);
        var worker = _f.Services.GetRequiredService<ImportWorker>();
        for (var i = 0; i < 20 && await worker.RunOnceAsync(Ct); i++) { }
        var item = (await _cara.GetFromJsonAsync<JsonElement>("/api/v1/imports", Ct)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("batchId").GetGuid() == id);
        return await _cara.GetFromJsonAsync<JsonElement>($"/api/v1/imports/items/{item.GetProperty("id").GetGuid()}", Ct);
    }

    private static string Status(JsonElement d) => d.GetProperty("status").GetString()!;
    private static string? Error(JsonElement d) => d.GetProperty("error").GetString();

    [Fact]
    public async Task AClaimedKind_ReachesTheContributor_AndGivesAPage_WithoutTheAI()
    {
        var detail = await ImportAsync(new { }, "Network_Map.abc", Binary);

        Assert.Equal("Ready", Status(detail));
        Assert.Equal(0, _seen.ModelCalls);
        var (file, bytes, actor) = Assert.Single(_seen.Files);
        Assert.Equal("Network_Map.abc", file);
        Assert.Equal(Binary, bytes);
        Assert.Equal((ActorKind.Plugin, "kb.test.reader", ActorSources.Import), (actor.Kind, actor.PluginId, actor.Source));
        var page = detail.GetProperty("proposal").GetProperty("pages")[0];
        Assert.Equal("Network Map", page.GetProperty("title").GetString());
        var nodes = JsonNode.Parse(page.GetProperty("contentJson").GetString()!)!["content"]!.AsArray();
        Assert.Equal("Network_Map.abc 10", Assert.Single(nodes)!["attrs"]!["label"]!.GetValue<string>());
        Assert.Equal("Plugin: kb.test.reader, kb.test.second", detail.GetProperty("proposal").GetProperty("model").GetString());
        // The draft contributors ran on it too, after the import contributor.
        Assert.Contains("claimBox", Assert.Single(_seen.Drafts).Pages[0].ContentJson, StringComparison.Ordinal);
        await _f.WithDbAsync(async db =>
        {
            var claim = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.ImportClaim, Ct);
            Assert.Contains("\"plugins\":\"kb.test.reader,kb.test.second\"", claim.DataJson, StringComparison.Ordinal);
            Assert.Contains("Network_Map.abc", claim.DataJson, StringComparison.Ordinal);
            Assert.Single(await db.AuditEntries.Where(a => a.Action == AuditActions.ImportContribute).ToListAsync(Ct));
        });
    }

    [Fact]
    public async Task AClaimedKind_ThatIsAZipInside_IsNotUnpacked()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        await using (var w = new StreamWriter(zip.CreateEntry("part.txt").Open())) await w.WriteAsync("inside");
        var detail = await ImportAsync(new { }, "drawing.abz", ms.ToArray());

        Assert.Equal("Ready", Status(detail));
        Assert.Equal(ms.ToArray(), Assert.Single(_seen.Files).Bytes);
        Assert.Single(detail.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task AnUnclaimedKind_TheAppDoesNotRead_FailsAsBefore_AndAClaimOnAKindTheAppReadsChangesNothing()
    {
        var unknown = await ImportAsync(new { }, "drawing.xyz", Binary);
        Assert.Equal(("Failed", Extractors.NotRead), (Status(unknown), Error(unknown)));

        var text = await ImportAsync(new { }, "notes.txt", Encoding.UTF8.GetBytes("Some text."));
        Assert.Equal("Ready", Status(text));
        Assert.Equal(1, _seen.ModelCalls);
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.ImportClaim, Ct)));
    }

    [Fact]
    public async Task AClaimedKind_ThePluginAddsNothingTo_Fails_SayingSo()
    {
        var detail = await ImportAsync(new { }, "empty.nil", Binary);
        Assert.Equal(("Failed", "Only kb.test.nothing reads this kind of file, and it added nothing to the draft. The file is kept as an attachment."),
            (Status(detail), Error(detail)));
        Assert.Equal(0, _seen.ModelCalls);
    }

    [Fact]
    public async Task AClaimedKind_BoundForASensitiveSpace_IsNotGivenToThePlugin_AndFails()
    {
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var detail = await ImportAsync(new { spaceId = _space }, "map.abc", Binary);
        Assert.Equal(("Failed", "Only a plugin reads this kind of file, and plugins are not given files that go to a sensitive space. The file is kept as an attachment."),
            (Status(detail), Error(detail)));
        Assert.Empty(_seen.Files);
        Assert.Empty(_seen.Drafts);
    }

    [Fact]
    public async Task AClaimedFile_AScannerFlags_StaysBlocked()
    {
        var detail = await ImportAsync(new { }, "infected.abc", Binary);
        Assert.Equal("Blocked", Status(detail));
        Assert.Empty(_seen.Files);
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.ImportClaim, Ct)));
    }
}
