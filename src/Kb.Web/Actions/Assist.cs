using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Kb.Web.Import;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// AI assist in the editor: material a person pastes (text, screenshots, files) built into a page, or merged into the page they
// edit. The model writes blocks; the server checks every word against the page and the pasted material, lets existing text go
// only as a duplicate (or as the old wording of what the pasted material updates), and answers a preview. Nothing is saved:
// the person puts it in the editor and saves under their own rights.

public sealed record AssistFile(string Name, string Data);
public sealed record AssistRequest(Guid? PageId, string? Title, string? ContentJson, string? Text, IReadOnlyList<AssistFile>? Files);
/// <summary>Removed: duplicates taken out. Changed: existing text the pasted material updates (from, to). Invented: words found in
/// neither the page nor the pasted material. Tags and AppliesTo: suggestions, as an import makes them.</summary>
public sealed record AssistDto(string Title, string ContentJson, IReadOnlyList<DiffLine> Diff, IReadOnlyList<FormatRemovedDto> Removed,
    IReadOnlyList<AssistChangeDto> Changed, IReadOnlyList<string> Invented, IReadOnlyList<string> Warnings, IReadOnlyList<string> Tags,
    IReadOnlyList<Kb.Plugins.Actions.AppliesToDto> AppliesTo);
public sealed record AssistChangeDto(string From, string To);

[Action("page.assist")]
public sealed record AssistPage : IAction<AssistDto>
{
    public required Guid SpaceId { get; init; }
    public Guid? PageId { get; init; }
    public string? Title { get; init; }
    public required string ContentJson { get; init; }
    public string? Text { get; init; }
    /// <summary>Pasted files, as base64. Not logged.</summary>
    [JsonIgnore] public IReadOnlyList<AssistFile>? Files { get; init; }
}

internal sealed partial class AssistPageHandler(KbDbContext db, IAccess access, ImportService imports, AiService ai, InventoryService inventory, IActionBus bus, DocRenderers renderers)
    : IActionHandler<AssistPage, AssistDto>
{
    private const int MaxPasted = 60_000, MaxFiles = 20, MaxPictures = 8;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Section labels the model may add although neither the page nor the pasted material says them.</summary>
    public static readonly string[] Labels =
    [
        "Overview", "Summary", "Background", "Applies to", "Before you start", "Prerequisites", "Symptoms", "Cause", "Fix", "Resolution", "Workaround",
        "Steps", "Procedure", "Verification", "Rollback", "Notes", "References", "Details", "Versions", "Contacts", "Escalation",
    ];

    private const string Task =
        "\"page\" is a page of the knowledge base as Markdown (empty for a new page) and \"pasted\" is material a person pasted to build or update it. " +
        "Answer the whole page as it should read with the pasted material in it, as blocks: \"heading\" for sections, \"steps\" for numbered procedures, " +
        "\"bullets\" for lists, \"code\" for commands and configuration, \"table\" for tabular data (first row is the header), \"note\" for warnings, " +
        "\"paragraph\" for the rest, \"image\" for a picture. Keep what the page says well as it is, in its place. Put each new piece where it belongs: " +
        "update a step the pasted material changes, add a missing step, add a row to a table. When something is in the page or the pasted material twice, " +
        "keep one copy. Use only the words of \"page\" and \"pasted\"; for a section title you may also use one of \"labels\". " +
        "Keep each picture of the page (a line like ![](page-img-2)) as an \"image\" block with that name; place each picture listed in \"images\" " +
        "at the step it shows, as an \"image\" block with its file name, with a short description in \"text\". " +
        "Give \"title\" (the page's own title if it has one). Suggest up to 8 short lower-case topic tags, and in \"apps\" the applications the content is about, " +
        "with the exact names from \"inventory\" when one matches, the version if the content says it, and the servers it names.";

    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<AssistPage> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (await access.RoleAsync(ctx.Actor, ctx.Action.SpaceId, ctx.Cancel) is null) return Problem.NotFound;
        if (ctx.Action.PageId is { } id && ((_page = await db.FindVisibleAsync(access, ctx.Actor, id, track: false, ctx.Cancel)) is not { Kind: PageKind.Page } || _page.SpaceId != ctx.Action.SpaceId))
            return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, ctx.Action.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return new Problem(403, "You can't add or change pages in this space.");
        if (ctx.Actor.TokenId is not null) return new Problem(403, "AI assist is used in the app, not with an API token.");
        if (!await imports.MayAsync(ctx.Actor, AiFeatures.Assist, ctx.Cancel)) return new Problem(403, "AI assist is not open to you.");
        return await db.Spaces.AnyAsync(s => s.Id == ctx.Action.SpaceId && s.IsSensitive, ctx.Cancel) ? Problem.Refused("Sensitive spaces are never sent to the AI.") : null;
    }

    [GeneratedRegex(@"^(note|warning|important|tip|caution|attention)\s*:\s*")]
    private static partial Regex LabelPrefix();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownImage();

    public async Task<Outcome<AssistDto>> HandleAsync(ActionContext<AssistPage> ctx)
    {
        var req = ctx.Action;
        var before = string.IsNullOrWhiteSpace(req.ContentJson) ? """{"type":"doc","content":[]}""" : req.ContentJson;
        if (!PageSupport.IsEditorDoc(before)) return Problem.Invalid("contentJson", "Not an editor document.");
        // The page is input too: instruction-like text in it refuses the call, as pasted text does in the service.
        if (await ai.PageRefusalAsync(ctx.Actor, AiFeatures.Assist, _page, before) is { } refusedPage) return refusedPage;

        var warnings = new List<string>();
        var pasted = new StringBuilder(req.Text?.Trim() ?? "");
        var pictures = new List<(string Name, byte[] Data, string Mime)>();
        var files = req.Files ?? [];
        if (files.Count > MaxFiles) return Problem.Invalid("files", $"{MaxFiles} files at most.");
        long total = 0;
        foreach (var f in files)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(f.Data); } catch (FormatException) { return Problem.Invalid("files", $"{f.Name}: not a file."); }
            if ((total += bytes.Length) > MaxFileBytes) return Problem.Invalid("files", "20 MB of files at most.");
            var name = Path.GetFileName(f.Name);
            var x = Extractors.Extract(name, bytes);
            switch (x.Kind)
            {
                case ExtractKind.Blocked: return Problem.Invalid("files", $"{name}: {x.Note}");
                case ExtractKind.Image: pictures.Add((name, bytes, x.Images[0].Mime)); break;
                case ExtractKind.Text:
                    pasted.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n\n=== {name} ===\n{x.Text}");
                    pictures.AddRange(x.Images.Select(i => (i.Name, i.Data, i.Mime)));
                    break;
                default: warnings.Add($"{name}: {x.Note ?? "not read"}"); break;
            }
        }
        var text = ImportService.Compact(pasted.ToString());
        if (text.Length > MaxPasted)
        {
            text = text[..MaxPasted];
            warnings.Add($"Only the first {MaxPasted:N0} characters of the pasted material were given to the model; the result does not cover the rest.");
        }
        if (text.Length == 0 && pictures.Count == 0 && DocFormat.Units(JsonNode.Parse(before)!.AsObject()).Count == 0) return Problem.Invalid("text", "Paste or drop something to work with.");
        if (pictures.Count > MaxPictures) { warnings.Add($"Only the first {MaxPictures} pictures were placed."); pictures = pictures[..MaxPictures]; }

        // The page's pictures stand in as names the model can only move; pasted ones become the space's images, as the person.
        var pageImages = new List<string>();
        var markdown = MarkdownImage().Replace(DocText.Markdown(before, renderers), m => { pageImages.Add(m.Groups[2].Value); return $"![{m.Groups[1].Value}](page-img-{pageImages.Count})"; });
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var apps = await inventory.CanViewAsync(ctx.Actor, ctx.Cancel)
            ? await db.InvApplications.AsNoTracking().OrderBy(a => a.NameKey).Take(300).Select(a => new { a.Id, a.Name }).ToListAsync(ctx.Cancel) : [];
        var data = new Dictionary<string, string>
        {
            ["page"] = (req.Title is { Length: > 0 } t ? $"# {t}\n\n" : "") + markdown,
            ["pasted"] = text.Length > 0 ? text : pictures.Count > 0 ? "(no text: the pictures are the material)" : "(nothing pasted: what is in the page is the material, to put in order)",
            ["images"] = string.Join("\n", pictures.Select(p => p.Name)),
            ["labels"] = string.Join(", ", Labels),
            ["inventory"] = string.Join("\n", apps.Select(a => a.Name)),
        };
        var r = await ai.RunAsync(ctx.Actor, AiFeatures.Assist, Task, data, Schema(), ctx.Cancel, [.. pictures.Select(p => new AiGuard.Picture(p.Mime, p.Data))]);
        if (r.Problem is { } p) return p;
        var output = r.Value!.Output;

        foreach (var pic in pictures)
        {
            var img = await bus.SendAsync(new Kb.Plugins.Actions.AddImage { SpaceId = req.SpaceId, File = new BytesUpload(pic.Name, pic.Data) }, ctx.Cancel);
            if (img.Value is { } dto) stored[pic.Name] = dto.Url;
        }
        string? Src(string name)
        {
            var n = Path.GetFileName(name.Trim());
            if (n.StartsWith("page-img-", StringComparison.Ordinal) && int.TryParse(n[9..], out var i) && i >= 1 && i <= pageImages.Count) return pageImages[i - 1];
            return stored.GetValueOrDefault(n);
        }
        var doc = DocBuilder.Build(DocBuilder.ReadBlocks(output["blocks"] as JsonArray), Src);
        // Pictures the model did not place go at the end, so none is lost; the page's own too.
        var placed = DocFormatImages(doc);
        foreach (var src in pageImages.Concat(stored.Values).Where(s => !placed.Contains(s)).Distinct())
            doc["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["src"] = src, ["alt"] = "" } });
        var title = (output["title"]?.GetValue<string>()?.Trim() is { Length: > 0 } ot ? ot : req.Title ?? "").Truncate(200);

        // Every word from the page or the pasted material (or a label); the rest is listed as invented.
        var known = new HashSet<string>(AiGuard.Words($"{req.Title} {DocText.Plain(before, renderers)} {text} {string.Join(' ', Labels)}").Select(w => w.ToLowerInvariant()));
        var outPlain = DocText.Plain(doc.ToJsonString(), renderers);
        var invented = AiGuard.Words($"{title} {outPlain}").Select(w => w.ToLowerInvariant()).Where(w => !known.Contains(w)).Distinct().Take(40).ToList();

        // Existing text may go only as a duplicate, or as the old wording of what the pasted material updates.
        var outUnits = DocFormat.Units(doc);
        var outNorm = DocFormat.Norm(outPlain);
        var existing = DocFormat.Units(JsonNode.Parse(before)!.AsObject()).Where(u => DocFormat.Norm(u.Text).Length > 0).ToList();
        var removed = new List<FormatRemovedDto>();
        var changed = new List<AssistChangeDto>();
        var lost = new List<string>();
        // A text the page has more often than the result: the extra copies were merged away, as duplicates.
        var copies = new HashSet<int>();
        foreach (var g in existing.GroupBy(u => DocFormat.Norm(u.Text)).Where(g => g.Count() > 1))
        {
            var left = outUnits.Count(o => DocFormat.Same(o.Text, g.First().Text) || DocFormat.Norm(o.Text).Contains(g.Key, StringComparison.Ordinal));
            foreach (var extra in g.Skip(Math.Max(1, left)))
            {
                removed.Add(new FormatRemovedDto([extra.N], Flat(extra.Text), Flat(g.First().Text)));
                copies.Add(extra.N);
            }
        }
        var labels = Labels.Select(l => l.ToLowerInvariant()).ToHashSet();
        foreach (var u in existing.Where(u => !copies.Contains(u.N)))
        {
            // A label the structure now says ("Steps" over a numbered list, "Note:" on a note) is structure, not content.
            var norm = LabelPrefix().Replace(DocFormat.Norm(u.Text), "");
            if (labels.Contains(norm) || norm.Length == 0) continue;
            if (outNorm.Contains(norm, StringComparison.Ordinal) || outUnits.Any(o => DocFormat.Same(o.Text, u.Text) || DocFormat.Same(o.Text, norm))) continue;
            if (existing.FirstOrDefault(o => o.N != u.N && DocFormat.Same(o.Text, u.Text) && outNorm.Contains(DocFormat.Norm(o.Text), StringComparison.Ordinal)) is { } copy)
            { removed.Add(new FormatRemovedDto([u.N], Flat(u.Text), Flat(copy.Text))); continue; }
            // Updated: what stays in its place is like it, and its words come from the pasted material.
            if (text.Length > 0 && outUnits.Select(o => (o, s: Similar(o.Text, u.Text))).Where(x => x.s >= 0.5).OrderByDescending(x => x.s).Select(x => x.o).FirstOrDefault() is { } upd
                && Overlap(upd.Text, text) >= 0.5)
            { changed.Add(new AssistChangeDto(Flat(u.Text), Flat(upd.Text))); continue; }
            lost.Add(Flat(u.Text));
        }
        if (lost.Count > 0)
        {
            ctx.Audit(AuditActions.PageAssist, "page", req.PageId?.ToString() ?? "new", req.SpaceId, new Dictionary<string, string> { ["outcome"] = "dropped-content", ["dropped"] = string.Join(" / ", lost).Truncate(500) });
            return new Problem(422, $"The AI left out part of the page ({string.Join(" / ", lost.Take(3)).Truncate(200)}), so its result was not used. Try again, or paste less at once.");
        }
        if (invented.Count > 0) warnings.Insert(0, $"Words in neither the page nor the pasted material: {string.Join(", ", invented.Take(20))}. Confirm the document is correct before saving.");

        // Suggestions, as an import makes them.
        var tags = (output["tags"]?.AsArray() ?? []).Select(x => x?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "").Where(x => x.Length is > 0 and <= 50 && !x.Contains(',')).ToList();
        var applies = new List<Kb.Plugins.Actions.AppliesToDto>();
        foreach (var a in output["apps"]?.AsArray() ?? [])
        {
            var name = a?["name"]?.GetValue<string>()?.Trim() ?? "";
            var version = a?["version"]?.GetValue<string>()?.Trim() ?? "";
            if (name.Length == 0) continue;
            var match = apps.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var tag = (match is null ? name : $"{match.Name}:{(version.Length > 0 ? version : "ALL")}").ToLowerInvariant();
            if (tag.Length <= 50 && !tag.Contains(',')) tags.Add(tag);
            if (match is null) continue;
            var installed = await db.InvInstalls.AsNoTracking().Where(i => i.ApplicationId == match.Id).Select(i => i.Server).ToListAsync(ctx.Cancel);
            var servers = (a!["servers"]?.AsArray() ?? []).Select(s => s?.GetValue<string>()).Select(s => installed.FirstOrDefault(i => string.Equals(i, s, StringComparison.OrdinalIgnoreCase))).OfType<string>().Distinct().ToList();
            applies.Add(new Kb.Plugins.Actions.AppliesToDto(match.Id, match.Name, servers));
        }

        var json = doc.ToJsonString();
        ctx.Audit(AuditActions.PageAssist, "page", req.PageId?.ToString() ?? "new", req.SpaceId, new Dictionary<string, string>
        {
            ["outcome"] = "preview", ["files"] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pictures"] = pictures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["duplicatesRemoved"] = removed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["invented"] = invented.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return new AssistDto(title, json, LineDiff.Diff(DocText.MarkdownLines(before, renderers), DocText.MarkdownLines(json, renderers)), removed, changed, invented, warnings,
            [.. tags.Distinct().Take(20)], applies);
    }

    private static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
            ["blocks"] = new JsonObject { ["type"] = "array", ["items"] = DocBuilder.BlockSchema() },
            ["tags"] = new JsonObject { ["type"] = "array", ["maxItems"] = 8, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 } },
            ["apps"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 10,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["version"] = new JsonObject { ["type"] = "string" }, ["servers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                    ["required"] = new JsonArray("name"), ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("title", "blocks"),
        ["additionalProperties"] = false,
    };

    private static HashSet<string> DocFormatImages(JsonObject doc)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            if (o["type"]?.GetValue<string>() == "image" && o["attrs"]?["src"]?.GetValue<string>() is { } s) found.Add(s);
            foreach (var k in o["content"] as JsonArray ?? []) Walk(k);
        }
        Walk(doc);
        return found;
    }

    /// <summary>How much of one text's words the other shares, 0 to 1.</summary>
    private static double Similar(string a, string b)
    {
        var (x, y) = (AiGuard.Words(DocFormat.Norm(a)).ToHashSet(), AiGuard.Words(DocFormat.Norm(b)).ToHashSet());
        return x.Count == 0 || y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    /// <summary>How much of a text's words come from the other, 0 to 1.</summary>
    private static double Overlap(string part, string whole)
    {
        var (x, y) = (AiGuard.Words(DocFormat.Norm(part)), AiGuard.Words(DocFormat.Norm(whole)).ToHashSet());
        return x.Count == 0 ? 0 : (double)x.Count(y.Contains) / x.Count;
    }

    private static string Flat(string s)
    {
        var f = Regex.Replace(s, @"\s+", " ").Trim();
        return f.Length <= 160 ? f : f[..157] + "…";
    }
}
