using System.Collections.Concurrent;
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
using Kb.Plugins.Actions;
using Kb.Web;
using Kb.Web.Import;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Plugins' import contributors (contract 0.4.0): they read each scanned file of an import and add nodes to its draft, within limits;
/// one that fails is skipped and counted against its plugin; nothing is given to them from an item with a secret or bound for a sensitive
/// space. The actions an accepted draft sends say they come from the import, and only the core can say so. Draft contributors (0.5.0)
/// read the drafted pages and add where they say; a retry drafts anew, so nothing is added twice.
/// </summary>
public sealed class ImportContributorTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";

    /// <summary>A model that drafts one page with one paragraph from whatever it is given.</summary>
    private sealed class OnePageModel : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new JsonObject
            {
                ["pages"] = new JsonArray(new JsonObject { ["title"] = "Flow", ["blocks"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["text"] = "The flow." }) }),
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

    /// <summary>What the fake plugins saw.</summary>
    private sealed class Seen
    {
        public readonly ConcurrentBag<(string File, bool Writable, string Text, Actor Actor)> Calls = [];
        public readonly ConcurrentBag<(string Action, Actor Actor)> Hooks = [];
        public readonly ConcurrentBag<(string Plugin, string Where)> Failures = [];
        public readonly ConcurrentBag<(ImportDraft Draft, Actor Actor)> Drafts = [];
        public readonly ConcurrentBag<string> Created = [];
    }

    private sealed class Recorder(Seen seen) : IPluginFailures
    {
        public void Report(string pluginId, string where, Exception? exception = null) => seen.Failures.Add((pluginId, where));
    }

    private static ImportContribution Nodes(params string[] json) => new([.. json.Select(j => JsonDocument.Parse(j).RootElement.Clone())]);

    private sealed class Good(Seen seen, IActorAccessor actor) : IImportContributor
    {
        public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
        {
            await using var s = await file.OpenReadAsync(ct);
            var text = await new StreamReader(s).ReadToEndAsync(ct);
            seen.Calls.Add((file.FileName, s.CanWrite, text, actor.Current));
            return file.FileName.EndsWith(".drawio", StringComparison.Ordinal) ? Nodes(new JsonObject { ["type"] = "probeBox", ["attrs"] = new JsonObject { ["label"] = "from " + file.FileName } }.ToJsonString()) : null;
        }
    }

    private sealed class Throws : IImportContributor
    {
        public Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct) => throw new InvalidOperationException("broken");
    }

    private sealed class Slow : IImportContributor
    {
        public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Nodes("""{"type":"paragraph"}""");
        }
    }

    private sealed class TooMany : IImportContributor
    {
        public Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct) =>
            Task.FromResult<ImportContribution?>(Nodes([.. Enumerable.Repeat("""{"type":"paragraph"}""", ImportContributors.MaxNodes + 1)]));
    }

    private sealed class NotABlock : IImportContributor
    {
        public Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct) =>
            Task.FromResult<ImportContribution?>(Nodes("""{"type":"doc","content":[]}"""));
    }

    private sealed class DraftGood(Seen seen, IActorAccessor actor) : IDraftContributor
    {
        public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct)
        {
            seen.Drafts.Add((draft, actor.Current));
            return Task.FromResult<DraftContribution?>(new([new DraftAddition(0, DraftPlace.Start, Nodes("""{"type":"draftChart","attrs":{"from":"text"}}""").Nodes)]));
        }
    }

    private sealed class NoSuchPage : IDraftContributor
    {
        public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct) =>
            Task.FromResult<DraftContribution?>(new([new DraftAddition(draft.Pages.Count, DraftPlace.End, Nodes("""{"type":"paragraph"}""").Nodes)]));
    }

    private sealed class Origin(Seen seen) : IBeforeAction<CreatePage>, IBeforeAction<AddAttachment>
    {
        public Task<Problem?> BeforeAsync(ActionContext<CreatePage> ctx)
        {
            seen.Hooks.Add((ctx.Name, ctx.Actor));
            if (ctx.Action.ContentJson is { } c) seen.Created.Add(c);
            return Task.FromResult<Problem?>(null);
        }
        public Task<Problem?> BeforeAsync(ActionContext<AddAttachment> ctx) { seen.Hooks.Add((ctx.Name, ctx.Actor)); return Task.FromResult<Problem?>(null); }
    }

    private readonly Seen _seen = new();
    private readonly KbAppFactory _f;
    private HttpClient _admin = null!;
    private HttpClient _cara = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ImportContributorTests()
    {
        var regs = new PluginRegistrations();
        regs.Add("kb.test.good", new ImportContributorReg(typeof(Good)));
        regs.Add("kb.test.throws", new ImportContributorReg(typeof(Throws)));
        regs.Add("kb.test.slow", new ImportContributorReg(typeof(Slow)));
        regs.Add("kb.test.toomany", new ImportContributorReg(typeof(TooMany)));
        regs.Add("kb.test.notablock", new ImportContributorReg(typeof(NotABlock)));
        regs.Add("kb.test.draft", new DraftContributorReg(typeof(DraftGood)));
        regs.Add("kb.test.nosuchpage", new DraftContributorReg(typeof(NoSuchPage)));
        regs.Add("kb.test.origin", new BeforeHook(typeof(CreatePage), typeof(Origin), false));
        regs.Add("kb.test.origin", new BeforeHook(typeof(AddAttachment), typeof(Origin), false));
        var seen = _seen;
        _f = new KbAppFactory(KbAppFactory.NewDbPath(), services: s =>
        {
            s.AddHttpClient(AiService.ClientName).ConfigurePrimaryHttpMessageHandler(() => new OnePageModel());
            s.AddSingleton<IRegistrations>(regs);
            s.AddSingleton(seen);
            s.AddSingleton<IPluginFailures>(new Recorder(seen));
        });
    }

    public async ValueTask InitializeAsync()
    {
        _f.Services.GetRequiredService<ImportContributors>().Limit = TimeSpan.FromMilliseconds(300);
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

    private async Task<JsonElement> DraftAsync(object batch, string name, string text)
    {
        var id = (await (await _cara.PostAsJsonAsync("/api/v1/imports", batch, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", name);
        Assert.Equal(HttpStatusCode.OK, (await _cara.PostAsync($"/api/v1/imports/{id}/files", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _cara.PostAsync($"/api/v1/imports/{id}/start", null, Ct)).StatusCode);
        var worker = _f.Services.GetRequiredService<ImportWorker>();
        for (var i = 0; i < 20 && await worker.RunOnceAsync(Ct); i++) { }
        var item = (await _cara.GetFromJsonAsync<JsonElement>("/api/v1/imports", Ct)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("batchId").GetGuid() == id);
        Assert.Equal("Ready", item.GetProperty("status").GetString());
        return await _cara.GetFromJsonAsync<JsonElement>($"/api/v1/imports/items/{item.GetProperty("id").GetGuid()}", Ct);
    }

    private static string FirstPage(JsonElement detail) => detail.GetProperty("proposal").GetProperty("pages")[0].GetProperty("contentJson").GetString()!;

    private static List<string> Warnings(JsonElement detail) => [.. detail.GetProperty("proposal").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)];

    [Fact]
    public async Task AContributor_ReadsTheScannedFile_AndItsNodesAreInTheDraft_AndOnesThatFailAreSkippedAndCounted()
    {
        var detail = await DraftAsync(new { }, "flow.drawio", "<mxfile><diagram>boxes</diagram></mxfile>");

        var call = Assert.Single(_seen.Calls);
        Assert.Equal(("flow.drawio", false, "<mxfile><diagram>boxes</diagram></mxfile>"), (call.File, call.Writable, call.Text));
        Assert.Equal((ActorKind.Plugin, "kb.test.good", ActorSources.Import), (call.Actor.Kind, call.Actor.PluginId, call.Actor.Source));
        Assert.Equal(detail.GetProperty("id").GetGuid(), call.Actor.ImportItemId);
        // Added at the end of the draft, and named, so the importer sees what a plugin added.
        var nodes = JsonNode.Parse(FirstPage(detail))!["content"]!.AsArray();
        Assert.Equal("probeBox", nodes[^1]!["type"]!.GetValue<string>());
        Assert.Equal("from flow.drawio", nodes[^1]!["attrs"]!["label"]!.GetValue<string>());
        Assert.Contains(Warnings(detail), w => w.Contains("kb.test.good added 1 block from flow.drawio", StringComparison.Ordinal));
        // The four that threw, ran too long, or answered what the core cannot use added nothing and were counted against their plugins.
        Assert.Equal(["kb.test.notablock", "kb.test.slow", "kb.test.throws", "kb.test.toomany"],
            _seen.Failures.Where(f => f.Where == "import contributor").Select(f => f.Plugin).Order(StringComparer.Ordinal));
        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action == AuditActions.ImportContribute).Select(a => a.DataJson).ToListAsync(Ct);
            Assert.Single(entries, e => e!.Contains("kb.test.good", StringComparison.Ordinal));
            Assert.Single(entries, e => e!.Contains("kb.test.draft", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task NoContributorIsGiven_AFileWithASecret_OrOneBoundForASensitiveSpace()
    {
        var secret = await DraftAsync(new { }, "notes.txt", "Admin password: Hunter2Secret!");
        Assert.Empty(_seen.Calls);
        Assert.Contains(Warnings(secret), w => w.StartsWith("Plugins were not given these files", StringComparison.Ordinal));

        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        await DraftAsync(new { spaceId = _space }, "flow.drawio", "<mxfile/>");
        Assert.Empty(_seen.Calls);
        Assert.Empty(_seen.Drafts);
        Assert.Empty(_seen.Failures);
    }

    [Fact]
    public async Task ADraftContributor_SeesTheDraft_ItsNodesGoWhereItSays_TheyAreSaved_AndARetryAddsThemOnce()
    {
        var detail = await DraftAsync(new { }, "flow.drawio", "<mxfile/>");
        var item = detail.GetProperty("id").GetGuid();

        // It saw the drafted page with the file contributor's node already in it, as its plugin, from the import.
        var (draft, actor) = Assert.Single(_seen.Drafts);
        Assert.Equal(item, draft.ImportItemId);
        var page = Assert.Single(draft.Pages);
        Assert.Equal("Flow", page.Title);
        Assert.Contains("The flow.", page.ContentJson, StringComparison.Ordinal);
        Assert.Contains("probeBox", page.ContentJson, StringComparison.Ordinal);
        Assert.Equal(["flow.drawio"], draft.Files);
        Assert.Equal((ActorKind.Plugin, "kb.test.draft", ActorSources.Import, (Guid?)item), (actor.Kind, actor.PluginId, actor.Source, actor.ImportItemId));

        // Its node is at the start, the file's at the end; it is named in the warnings. One naming a page the draft lacks added nothing.
        var nodes = JsonNode.Parse(FirstPage(detail))!["content"]!.AsArray();
        Assert.Equal(["draftChart", "paragraph", "probeBox"], nodes.Select(n => n!["type"]!.GetValue<string>()));
        Assert.Contains(Warnings(detail), w => w == "kb.test.draft added 1 block to \"Flow\".");
        Assert.Contains(("kb.test.nosuchpage", "draft contributor"), _seen.Failures);

        // A retry drafts the page again: the contributors run again, and the node is there once.
        Assert.True((await _cara.PostAsync($"/api/v1/imports/items/{item}/retry", null, Ct)).IsSuccessStatusCode);
        var worker = _f.Services.GetRequiredService<ImportWorker>();
        for (var i = 0; i < 20 && await worker.RunOnceAsync(Ct); i++) { }
        detail = await _cara.GetFromJsonAsync<JsonElement>($"/api/v1/imports/items/{item}", Ct);
        Assert.Equal(2, _seen.Drafts.Count);
        Assert.Single(JsonNode.Parse(FirstPage(detail))!["content"]!.AsArray(), n => n!["type"]!.GetValue<string>() == "draftChart");

        // Accepting saves it: the page.create a plugin's hook sees comes from this import and already holds the node.
        var accept = await _cara.PostAsJsonAsync($"/api/v1/imports/items/{item}/accept", new
        {
            pages = new[] { new { title = "Flow", contentJson = FirstPage(detail), tags = (string[]?)null } }, spaceId = _space,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Contains(_seen.Hooks, h => h.Action == "page.create" && h.Actor.ImportItemId == item);
        Assert.Contains("draftChart", Assert.Single(_seen.Created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheActionsAnAcceptedDraftSends_ComeFromTheImport_AndACallerCannotSaySo()
    {
        var detail = await DraftAsync(new { }, "flow.drawio", "<mxfile/>");
        var item = detail.GetProperty("id").GetGuid();
        var accept = await _cara.PostAsJsonAsync($"/api/v1/imports/items/{item}/accept", new
        {
            pages = new[] { new { title = "Flow", contentJson = FirstPage(detail), tags = (string[]?)null } }, spaceId = _space, attachOriginals = true,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var fromImport = _seen.Hooks.ToList();
        Assert.Contains(fromImport, h => h.Action == "page.create");
        Assert.Contains(fromImport, h => h.Action == "attachment.add");
        Assert.All(fromImport, h => Assert.Equal((ActorKind.User, ActorSources.Import, (Guid?)item), (h.Actor.Kind, h.Actor.Source, h.Actor.ImportItemId)));

        // A page made by hand, even with the fields in its body, is not from an import.
        _seen.Hooks.Clear();
        var made = await _cara.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "By hand", contentJson = """{"type":"doc","content":[]}""", source = "import", importItemId = item }, Ct);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var hook = Assert.Single(_seen.Hooks);
        Assert.Equal((ActorSources.Web, (Guid?)null), (hook.Actor.Source, hook.Actor.ImportItemId));
    }
}
