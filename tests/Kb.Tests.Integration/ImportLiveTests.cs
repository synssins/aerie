using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Web.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Import against a real model and a local corpus, for a person to read the results: KB_AI_URL and KB_AI_MODEL name the model,
/// KB_CORPUS the corpus folder, KB_LIVE_FILES (optional, ';'-separated paths in it) the files. Without them it does nothing.
/// The only hard checks: the worker finishes every item, and text addressed to the AI is refused.
/// </summary>
public sealed class ImportLiveTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Sample =
    [
        "bundles/bundle-056/howto.md", "bundles/bundle-056/step1.png", "bundles/bundle-056/step2.png", "bundles/bundle-056/step3.png", "bundles/bundle-056/step4.png",
        "clean/data/known issue_20251103.yaml", "clean/email/notice2897.eml", "clean/word/change request_3520.docx", "messy/text/notes258.txt",
        "edge-cases/printer-notes.txt", "edge-cases/meeting-recap.md", "edge-cases/passwords-DO-NOT-SHARE.txt", "edge-cases/Thumbs.db",
        "edge-cases/change-request-notes.txt", "edge-cases/truncated.docx", "edge-cases/path-traversal-names.zip", "edge-cases/screenshot.pdf",
        "edge-cases/utf16be-no-bom.txt", "onenote/notebook-folder/Runbooks.one",
    ];

    [Fact]
    public async Task Corpus_ThroughTheRealModel()
    {
        var url = Environment.GetEnvironmentVariable("KB_AI_URL");
        var root = Environment.GetEnvironmentVariable("KB_CORPUS");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(root)) return;
        var files = Environment.GetEnvironmentVariable("KB_LIVE_FILES") is { Length: > 0 } list ? list.Split(';') : Sample;
        // KB_LIVE_CONCURRENCY: drafts at once, run by the background worker as on a server; unset: one at a time, by hand.
        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("KB_LIVE_CONCURRENCY"), out var c) ? c : 0;
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: concurrency > 0 ? new Dictionary<string, string> { ["Kb:Import:PollSeconds"] = "1" } : null);
        if (Environment.GetEnvironmentVariable("KB_LIVE_DIR") is { Length: > 0 } dir)
            files = [.. Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))];
        var admin = await f.SignedInBreakGlassAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/ai", new
        {
            settings = new
            {
                enabled = true, baseUrl = url, model = Environment.GetEnvironmentVariable("KB_AI_MODEL") ?? "", timeoutSeconds = 300, maxTokens = 8192, temperature = 0, reasoning = false,
                ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset, maxInputChars = 200000, userPerMinute = 100, tokenPerMinute = 100, importConcurrency = Math.Max(1, concurrency),
                features = new { import = "Everyone", format = "Everyone", search = "Off" },
            },
        }, Ct)).EnsureSuccessStatusCode();
        var app = (await (await admin.PostAsJsonAsync("/api/v1/inventory/apps", new { name = "Flibbertigibbet Controller" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/api/v1/inventory/apps/{app}/installs", new { server = "APP02", version = "4.0.2" }, Ct);

        var batch = (await (await admin.PostAsJsonAsync("/api/v1/imports", new { }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var path in files)
        {
            var body = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(root, path), Ct));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.Add("X-File-Name", Uri.EscapeDataString(path));
            var r = await admin.PostAsync($"/api/v1/imports/{batch}/files", body, Ct);
            output.WriteLine($"upload {path}: {(int)r.StatusCode}");
        }
        (await admin.PostAsync($"/api/v1/imports/{batch}/start", null, Ct)).EnsureSuccessStatusCode();
        var worker = f.Services.GetRequiredService<ImportWorker>();
        var started = DateTime.UtcNow;
        if (concurrency > 0)
            for (var i = 0; i < 3600; i++)
            {
                await Task.Delay(1000, Ct);
                var open = 0;
                await f.WithDbAsync(async db => open = await db.ImportItems.CountAsync(x => x.Status == Kb.Core.Import.ImportStatus.Queued || x.Status == Kb.Core.Import.ImportStatus.Working, Ct)
                    + await db.ImportBatches.CountAsync(b => !b.Grouped && b.Error == null, Ct));
                if (open == 0) break;
            }
        else
            for (var i = 0; i < 500 && await worker.RunOnceAsync(Ct); i++) { }
        output.WriteLine($"worker: {(DateTime.UtcNow - started).TotalSeconds:0}s");

        var items = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/imports", Ct)).GetProperty("items").EnumerateArray().ToList();
        foreach (var it in items)
        {
            var item = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/imports/items/{it.GetProperty("id").GetGuid()}", Ct);
            output.WriteLine($"\n== {it.GetProperty("title")} [{it.GetProperty("status")}] {it.GetProperty("error")}");
            foreach (var file in item.GetProperty("files").EnumerateArray())
                output.WriteLine($"   file {file.GetProperty("path")} {file.GetProperty("kind")} {file.GetProperty("note")}");
            if (item.GetProperty("proposal") is { ValueKind: JsonValueKind.Object } p)
            {
                foreach (var page in p.GetProperty("pages").EnumerateArray())
                {
                    var doc = page.GetProperty("redactedJson").GetString()!;
                    output.WriteLine($"   page \"{page.GetProperty("title")}\" {doc.Length} chars, images {ImportService.ImportImageIds(doc).Count()}");
                    output.WriteLine($"   {Kb.Core.Content.DocText.Plain(doc)[..Math.Min(400, Kb.Core.Content.DocText.Plain(doc).Length)].Replace('\n', ' ')}");
                }
                output.WriteLine($"   tags {p.GetProperty("tags")} applies {p.GetProperty("appliesTo")} owner {p.GetProperty("owner")} review {p.GetProperty("reviewMonths")}");
                output.WriteLine($"   sensitive {p.GetProperty("sensitive").GetArrayLength()} duplicates {p.GetProperty("duplicates").GetArrayLength()} warnings {p.GetProperty("warnings")}");
            }
        }
        // Format: a flat page of runbook text, formatted by the model; it must keep every word or be refused.
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ops", name = "Ops" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        string P(string t) => $$"""{"type":"paragraph","content":[{"type":"text","text":"{{t}}"}]}""";
        var flat = $$"""{"type":"doc","content":[{{P("Restart the print spooler on PRN02")}},{{P("Steps")}},{{P("1. Open services.msc on PRN02")}},{{P("2. Stop Print Spooler then start it")}},{{P("3. Print a test page. If it fails check event ID 372 in PrintService/Admin")}},{{P("Note: do not reboot during business hours")}}]}""";
        var created = await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Spooler", contentJson = flat }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var fr = await admin.PostAsJsonAsync($"/api/v1/pages/{created.GetProperty("id").GetGuid()}/format", new { contentJson = flat }, Ct);
        output.WriteLine($"\nformat: {(int)fr.StatusCode} {await fr.Content.ReadAsStringAsync(Ct)}");
        Assert.True(fr.StatusCode is System.Net.HttpStatusCode.OK or (System.Net.HttpStatusCode)422);

        await f.WithDbAsync(async db =>
        {
            foreach (var a in await db.AuditEntries.Where(a => a.Action == AuditActions.AiRefused).ToListAsync(Ct)) output.WriteLine($"refused: {a.DataJson}");
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AiRefused, Ct) || !files.Any(p => p.Contains("printer-notes", StringComparison.Ordinal)));
        });
        Assert.DoesNotContain(items, i => i.GetProperty("status").GetString() is "Queued" or "Working");
    }
}
