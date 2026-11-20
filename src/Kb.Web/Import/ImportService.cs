using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Import;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Import;

/// <summary>A file's bytes as an upload, for sending an action as the importer (an image, an attachment).</summary>
internal sealed class BytesUpload(string name, byte[] data) : UploadContent
{
    public override string? FileName => name;
    public override long? Length => data.Length;
    public override Task<Stream> OpenReadAsync(CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(data, false));
}

/// <summary>
/// AI import. Files are uploaded into a batch and kept in storage; a background worker reads them (expanding zips and mail),
/// groups those that belong together, and has the AI draft page(s) from each group: a proposal only the importer sees, in their
/// import inbox. Nothing is published until they review it and accept it, which sends the normal page actions as them, so
/// their own rights decide. Every guardrail of the AI service applies to each call.
/// </summary>
public sealed partial class ImportService(KbDbContext db, IFileStore store, AiService ai, InventoryService inventory, IAccess access,
    Kb.Core.Search.ISearchProvider search, IEnumerable<IMalwareScanner> scanners, IAuditLog audit, TimeProvider time, DocRenderers renderers,
    ImportContributors contributors, ILogger<ImportService> log)
{
    public const int MaxFilesPerBatch = 1000;
    public const long MaxFileBytes = 256L * 1024 * 1024;
    /// <summary>What everything inside the files of one upload (archives in archives, mail attachments, pictures) may add up to once unpacked.</summary>
    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxUnpackedFiles = 5000;
    public const string OverBudget = "Left out: this upload holds too much once unpacked.";

    /// <summary>What is left of one upload's allowance for unpacked files.</summary>
    private sealed class Budget
    {
        private long _bytes = MaxUnpackedBytes;
        private int _files = MaxUnpackedFiles;
        public bool Take(long bytes) => --_files >= 0 && (_bytes -= bytes) >= 0;
    }
    public const string FileUrl = "/api/v1/imports/files/";
    // shortcut: the model reads at most the first 60,000 characters of an item (about 15,000 tokens); a longer one is cut, with a warning.
    private const int MaxSourceChars = 60_000, MaxPictures = 8, MaxPictureBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Key(Guid fileId) => $"imports/{fileId:N}";

    public Task<bool> MayImportAsync(Actor actor, CancellationToken ct) => MayAsync(actor, AiFeatures.Import, ct);

    public async Task<bool> MayAsync(Actor actor, string feature, CancellationToken ct)
    {
        var s = await ai.SettingsAsync(ct);
        return s.Enabled && await ai.MayUseAsync(actor, feature, s, ct);
    }

    // ---------- upload

    public async Task<ImportFile> AddFileAsync(ImportBatch batch, string path, byte[] data, CancellationToken ct)
    {
        var clean = string.Join('/', path.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != "." && p != ".."));
        if (clean.Length == 0) clean = "file";
        var f = new ImportFile
        {
            BatchId = batch.Id, UserId = batch.UserId, Path = clean.Length > 1000 ? clean[^1000..] : clean, Name = Trim(Path.GetFileName(clean), 255),
            Size = data.Length, Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), Kind = "Pending", CreatedAt = time.GetUtcNow(),
        };
        await store.SaveAsync(Key(f.Id), data, ct);
        db.ImportFiles.Add(f);
        await db.SaveChangesAsync(ct);
        return f;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------- reading and grouping (the worker)

    /// <summary>Reads every file of the batch (and what is inside them), then groups them into items. One bad file stays one bad file.</summary>
    public async Task GroupBatchAsync(ImportBatch batch, CancellationToken ct)
    {
        var pending = await db.ImportFiles.Where(f => f.BatchId == batch.Id && f.Kind == "Pending").ToListAsync(ct);
        var budget = new Budget();
        foreach (var f in pending)
        {
            var data = await ReadAsync(f.Id, ct);
            await ReadFileAsync(batch, f, data, 0, budget, ct);
        }
        await db.SaveChangesAsync(ct);
        var files = await db.ImportFiles.Where(f => f.BatchId == batch.Id && f.ItemId == null).ToListAsync(ct);
        var groups = ByIdentifiers(Group(files), files);
        groups = await GroupByContentAsync(batch.UserId, files, groups, ct);
        var now = time.GetUtcNow();
        foreach (var g in groups)
        {
            var members = files.Where(f => g.Contains(f.Id)).ToList();
            var main = members.FirstOrDefault(m => m.Kind == "Text") ?? members[0];
            var allBlocked = members.All(m => m.Kind is "Blocked");
            var item = new ImportItem
            {
                BatchId = batch.Id, UserId = batch.UserId, Title = CleanTitle(main.Name), CreatedAt = now, UpdatedAt = now,
                Status = allBlocked ? ImportStatus.Blocked : ImportStatus.Queued, Error = allBlocked ? main.Note : null,
            };
            db.ImportItems.Add(item);
            foreach (var m in members) m.ItemId = item.Id;
            // Pictures inside a document go with it.
            foreach (var e in files.Where(f => f.Embedded && f.ParentId is { } p && g.Contains(p))) e.ItemId = item.Id;
        }
        batch.Grouped = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<byte[]> ReadAsync(Guid fileId, CancellationToken ct)
    {
        await using var s = await store.OpenAsync(Key(fileId), ct) ?? throw new InvalidOperationException("The file is gone from storage.");
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task ReadFileAsync(ImportBatch batch, ImportFile f, byte[] data, int depth, Budget budget, CancellationToken ct)
    {
        foreach (var scanner in scanners)
        {
            await using var stream = new MemoryStream(data, false);
            ScanVerdict verdict;
            // A scanner that fails blocks this file, not the batch: a file nobody could scan is not read.
            try { verdict = await scanner.ScanAsync(stream, f.Name, ct); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                (f.Kind, f.Note) = ("Blocked", $"Not scanned: the virus scanner failed ({e.GetType().Name}).");
                return;
            }
            if (verdict == ScanVerdict.Infected)
            {
                (f.Kind, f.Note) = ("Blocked", "Quarantined: the scanner flagged it.");
                await audit.AppendAsync(new AuditEvent(AuditActions.ImportQuarantine, f.UserId, "importFile", f.Id.ToString(), Data: new Dictionary<string, string> { ["name"] = f.Name, ["sha256"] = f.Sha256 }), ct);
                return;
            }
        }
        var x = Extractors.Extract(f.Name, data);
        // A kind the app has no reader for, that a loaded plugin reads: kept whole for the plugin's import contributors. It is not
        // unpacked (it may be a zip inside) and its bytes are not given to the AI.
        if (data.Length > 0 && x.Kind is not (ExtractKind.Blocked or ExtractKind.Skipped or ExtractKind.Image) && contributors.ClaimersOf(f.Name) is { Count: > 0 } by)
        {
            (f.Kind, f.Note, f.Text) = ("Claimed", null, null);
            await audit.AppendAsync(new AuditEvent(AuditActions.ImportClaim, f.UserId, "importFile", f.Id.ToString(), Data: new Dictionary<string, string>
            {
                ["name"] = f.Name, ["sha256"] = f.Sha256, ["plugins"] = string.Join(",", by.Select(b => b.Id)),
            }), ct);
            return;
        }
        (f.Kind, f.Note, f.Text) = (x.Kind.ToString(), x.Note, x.Text.Length > 0 ? x.Text : null);
        if (x.Kind == ExtractKind.Blocked)
            await audit.AppendAsync(new AuditEvent(AuditActions.ImportQuarantine, f.UserId, "importFile", f.Id.ToString(), Data: new Dictionary<string, string> { ["name"] = f.Name, ["sha256"] = f.Sha256, ["reason"] = x.Note ?? "" }), ct);
        if (x.Kind == ExtractKind.Image) f.Mime = x.Images[0].Mime;
        // Pictures inside a document: stored, belong to it.
        if (x.Kind == ExtractKind.Text)
            foreach (var img in x.Images)
            {
                if (!budget.Take(img.Data.Length)) { f.Note = OverBudget; break; }
                var child = await AddFileAsync(batch, $"{f.Path}/{img.Name}", img.Data, ct);
                (child.Kind, child.Mime, child.Embedded, child.ParentId) = ("Image", img.Mime, true, f.Id);
            }
        // Files inside (a zip, a mail's attachments): read in turn, to a limited depth.
        foreach (var c in x.Children)
        {
            // Nothing more is stored once the upload's allowance is used up; the archive says that files were left out.
            if (!budget.Take(c.Data.Length)) { f.Note = OverBudget; break; }
            var child = await AddFileAsync(batch, $"{f.Path}/{c.Path}", c.Data, ct);
            child.ParentId = f.Id;
            if (depth + 1 > Extractors.MaxDepth) (child.Kind, child.Note) = ("Blocked", "Nested too deep.");
            else await ReadFileAsync(batch, child, c.Data, depth + 1, budget, ct);
        }
    }

    /// <summary>
    /// Groups by what the files say about each other: a picture a document names goes with it; files of one name stem go together;
    /// a mail goes with its attachments. Containers (a zip itself) and pictures inside documents are not items of their own.
    /// </summary>
    public static List<HashSet<Guid>> Group(IReadOnlyList<ImportFile> files)
    {
        var byId = files.ToDictionary(f => f.Id);
        bool IsMail(ImportFile f) => f.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);
        var candidates = files.Where(f => !f.Embedded && f.Kind is not ("Container" or "Skipped")).ToList();
        var groups = new List<HashSet<Guid>>();
        var placed = new Dictionary<Guid, HashSet<Guid>>();
        void Put(ImportFile f, HashSet<Guid> g) { g.Add(f.Id); placed[f.Id] = g; }

        bool InMail(ImportFile f) => f.ParentId is { } p && byId.TryGetValue(p, out var parent) && IsMail(parent);
        foreach (var doc in candidates.Where(f => f.Kind == "Text" && !InMail(f))) { var g = new HashSet<Guid>(); groups.Add(g); Put(doc, g); }
        foreach (var f in candidates.Where(f => !placed.ContainsKey(f.Id)))
        {
            // A mail's attachments go with the mail.
            if (f.ParentId is { } p && byId.TryGetValue(p, out var parent) && IsMail(parent) && placed.TryGetValue(parent.Id, out var mail)) { Put(f, mail); continue; }
            var stem = Stem(f.Name);
            var doc = candidates.Where(d => d.Kind == "Text" && d.Text is not null)
                .FirstOrDefault(d => d.Text!.Contains(f.Name, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(d => d.Kind == "Text" && stem.Length >= 3 && (Stem(d.Name) == stem || stem.StartsWith(Stem(d.Name) + "-", StringComparison.Ordinal) || stem.StartsWith(Stem(d.Name) + "_", StringComparison.Ordinal)));
            // A folder with one document and its screenshots (step1.png, Screenshot (2).png) is one item.
            if (doc is null && f.Kind == "Image")
            {
                var folder = Path.GetDirectoryName(f.Path);
                var docs = candidates.Where(d => d.Kind == "Text" && d.ParentId == f.ParentId && Path.GetDirectoryName(d.Path) == folder).Take(2).ToList();
                if (docs.Count == 1) doc = docs[0];
            }
            if (doc is not null && f.Kind == "Image") { Put(f, placed[doc.Id]); continue; }
            var own = new HashSet<Guid>();
            groups.Add(own);
            Put(f, own);
        }
        return groups;
    }

    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant().Trim();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:CHG|INC|RITM|PRB|REQ|TASK|CR|RFC)\d{5,}\b|\b[A-Z]{2,4}-\d{5,8}\b")]
    private static partial System.Text.RegularExpressions.Regex Identifier();

    /// <summary>
    /// Documents that name the same change, incident, request or vendor case number are one story: their groups join.
    /// Applications and servers are not used: they recur across unrelated documents.
    /// </summary>
    private const int MaxSharedIds = 8;

    public static List<HashSet<Guid>> ByIdentifiers(List<HashSet<Guid>> groups, IReadOnlyList<ImportFile> files)
    {
        var text = files.Where(f => f.Text is not null).ToDictionary(f => f.Id, f => f.Text!);
        var ids = groups.Select(g => g.Where(text.ContainsKey).SelectMany(f => Identifier().Matches(text[f]).Select(m => m.Value)).ToHashSet()).ToList();
        // A digest (a change calendar, a meeting log) names many stories: it joins none of them.
        for (var i = 0; i < ids.Count; i++) if (ids[i].Count > MaxSharedIds) ids[i].Clear();
        var parent = Enumerable.Range(0, groups.Count).ToArray();
        // A loop, not a call to itself: a long chain of files that name each other's numbers would otherwise be as deep as it is long.
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var owner = new Dictionary<string, int>();
        for (var i = 0; i < groups.Count; i++)
            foreach (var id in ids[i])
                if (owner.TryGetValue(id, out var j)) parent[Find(i)] = Find(j); else owner[id] = i;
        return [.. Enumerable.Range(0, groups.Count).GroupBy(Find).Select(g => g.SelectMany(i => groups[i]).ToHashSet())];
    }

    private static readonly JsonObject GroupSchema = JsonNode.Parse("""
        {"type":"object","properties":{"groups":{"type":"array","items":{"type":"object","properties":{"documents":{"type":"array","items":{"type":"integer"}}},"required":["documents"],"additionalProperties":false}}},"required":["groups"],"additionalProperties":false}
        """)!.AsObject();

    /// <summary>
    /// Asks the AI which documents belong together by their content alone (part 1 and part 2 of one procedure, for example).
    /// Only joins groups; an answer that names a document twice or one that is not there is ignored.
    /// </summary>
    private async Task<List<HashSet<Guid>>> GroupByContentAsync(Guid userId, IReadOnlyList<ImportFile> files, List<HashSet<Guid>> groups, CancellationToken ct)
    {
        var docs = groups.Select((g, i) => (Index: i, Main: files.FirstOrDefault(f => g.Contains(f.Id) && f.Kind == "Text"))).Where(x => x.Main is not null).ToList();
        if (docs.Count < 2 || docs.Count > 60) return groups;
        var listing = string.Join("\n\n", docs.Select((d, n) => $"[{n}] {d.Main!.Name}\n{Excerpt(d.Main.Text!, 700)}"));
        var r = await ai.RunAsync(Actor.User(userId), AiFeatures.Import,
            "The data lists documents by number. Group the numbers of documents that belong together by their content: parts of one procedure or topic that " +
            "should become one page. A document that stands alone is its own group. Every number appears in exactly one group.",
            new Dictionary<string, string> { ["documents"] = listing }, GroupSchema, ct);
        if (!r.Ok) return groups;
        var seen = new HashSet<int>();
        var merged = new List<HashSet<Guid>>();
        var used = new HashSet<int>();
        foreach (var g in r.Value!.Output["groups"]!.AsArray())
        {
            var nums = g!["documents"]!.AsArray().Select(n => n!.GetValue<int>()).Where(n => n >= 0 && n < docs.Count && seen.Add(n)).ToList();
            if (nums.Count == 0) continue;
            var joined = new HashSet<Guid>();
            foreach (var n in nums) { joined.UnionWith(groups[docs[n].Index]); used.Add(docs[n].Index); }
            merged.Add(joined);
        }
        merged.AddRange(groups.Where((_, i) => !used.Contains(i)));
        return merged;
    }

    private static string Excerpt(string text, int max) => text.Length <= max ? text : text[..max] + " …";

    // ---------- the proposal (the worker)

    private static JsonObject ProposalSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["title"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 }, ["blocks"] = new JsonObject { ["type"] = "array", ["items"] = DocBuilder.BlockSchema() } },
                    ["required"] = new JsonArray("title", "blocks"), ["additionalProperties"] = false,
                },
            },
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
            ["owner"] = new JsonObject { ["type"] = "string" },
            ["reviewMonths"] = new JsonObject { ["enum"] = new JsonArray(0, 1, 3, 6, 12, 24, 36) },
            ["destination"] = new JsonObject { ["type"] = "string" },
            ["found"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["people"] = Strings(20, 100), ["servers"] = Strings(20, 100), ["applications"] = Strings(20, 100), ["tickets"] = Strings(20, 100),
                    ["versions"] = Strings(20, 100), ["conflicts"] = Strings(5, 200),
                },
                ["additionalProperties"] = false,
            },
        },
        ["required"] = new JsonArray("pages", "tags", "apps", "owner", "reviewMonths", "destination"),
        ["additionalProperties"] = false,
    };

    /// <summary>
    /// A draft with mechanical slips, repaired, if it then matches <paramref name="schema"/>; otherwise null. The slips: the answer-wide
    /// fields (tags, apps, owner, ...) written inside the first page instead of beside the pages; blocks written into the list of pages
    /// instead of the page before them; and, after that, an answer-wide suggestion left out, which gets its empty value (no tags, no
    /// owner, no review, the inbox). Nothing of the content is added or dropped.
    /// </summary>
    internal static JsonNode? Lifted(string content, JsonObject schema)
    {
        JsonObject? root;
        var (from, to) = (content.IndexOf('{', StringComparison.Ordinal), content.LastIndexOf('}'));
        if (from < 0 || to < from) return null;
        root = ImportTools.ParseModelJson(content[from..(to + 1)]) as JsonObject;
        if (root?["pages"] is not JsonArray { Count: > 0 } pages || pages[0] is not JsonObject first || schema["properties"] is not JsonObject top) return null;
        var moved = false;
        foreach (var key in top.Select(p => p.Key).Where(k => k != "pages" && !root.ContainsKey(k) && first.ContainsKey(k)).ToList())
        {
            var value = first[key];
            first.Remove(key);
            root[key] = value;
            moved = true;
        }
        // A block in the list of pages (it has a type and no blocks of its own) belongs at the end of the page before it.
        for (var i = 1; i < pages.Count; i++)
        {
            if (pages[i] is not JsonObject b || !b.ContainsKey("type") || b.ContainsKey("blocks") || pages[i - 1]?["blocks"] is not JsonArray into) continue;
            pages.RemoveAt(i--);
            into.Add(b);
            moved = true;
        }
        foreach (var (key, empty) in new (string, Func<JsonNode>)[] { ("tags", () => new JsonArray()), ("apps", () => new JsonArray()), ("owner", () => JsonValue.Create("")),
            ("reviewMonths", () => JsonValue.Create(0)), ("destination", () => JsonValue.Create("inbox")) })
            if (top.ContainsKey(key) && !root.ContainsKey(key)) { root[key] = empty(); moved = true; }
        return moved && AiGuard.SchemaError(root, schema) is null ? root : null;
    }

    private static JsonObject Strings(int items, int length) =>
        new() { ["type"] = "array", ["maxItems"] = items, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = length } };

    // Where the task says to keep the regions a plugin's tool is for (Regions.Find) as written: the sentence, and the regions as data under
    // KeepField, go in only for documents that hold one, so documents without one (markup, diagram code) get the task exactly as before.
    private const string KeepMark = "{keep}";

    internal const string KeepField = "keep";

    internal const string KeepSentence =
        "The passages under \"keep\" are laid out by the position of their characters; copy each of them into one code block, exactly as written, where it stands in the documents. ";

    /// <summary>The drafting task, with the keep sentence where the documents hold a region a tool is for.</summary>
    internal static string DraftingTask(bool keep) => ProposalTask.Replace(KeepMark, keep ? KeepSentence : "", StringComparison.Ordinal);

    /// <summary>The regions of the source that the loaded plugins' tools are for, as each declares them, in the order they stand; empty for none.</summary>
    private List<string> KeptRegions(string source) =>
        [.. contributors.Tools.Where(t => t.Tool.For is { } f && ImportTools.Problem(t.Tool) is null)
            .SelectMany(t => Regions.Find(source, t.Tool.For!)).Select(r => string.Join("\n", r.Lines)).Distinct()];

    private const string ProposalTask =
        "Bring the documents in the data into this knowledge base as standardized page(s). Keep every fact, value, command and step exactly; " +
        "do not add, invent or drop information. Use headings for sections, \"steps\" blocks for numbered procedures, \"bullets\" for lists, " +
        "\"code\" for commands, scripts and configuration, \"table\" for tabular data (first row is the header), \"note\" for warnings. " +
        KeepMark +
        "Place each picture listed under \"images\" as an \"image\" block (its file name in \"image\") at the step it belongs to; write in its \"text\" a short description of what the picture shows. " +
        "Use one page unless the documents clearly cover separate topics. " +
        "Suggest up to 8 short lower-case topic tags. In \"apps\" name the applications the content is about, using the exact names from \"inventory\" when one matches, " +
        "with the version if the content says it and the servers it names. In \"owner\" give the team or person who owns the process or escalation path if the content names one, else an empty string. " +
        "Suggest \"reviewMonths\" (0 for none). In \"destination\" give the key of the best place from \"destinations\", or \"inbox\" if none fits. " +
        "In \"found\" list the people, servers, applications, ticket or case numbers and versions the content names, each exactly as written, " +
        "and in \"conflicts\" any fact the documents state in different ways (for example two versions for one upgrade), in a short sentence each.";

    /// <summary>Has the AI draft the item's page(s), then adds what the code finds itself: secrets, duplicates, added words.</summary>
    public async Task ProposeAsync(ImportItem item, CancellationToken ct)
    {
        var files = await db.ImportFiles.Where(f => f.ItemId == item.Id).OrderBy(f => f.Path).ToListAsync(ct);
        var batch = await db.ImportBatches.FirstAsync(b => b.Id == item.BatchId, ct);
        var actor = Actor.User(item.UserId);
        var docs = files.Where(f => f.Kind == "Text" && f.Text is not null).ToList();
        var images = files.Where(f => f.Kind == "Image").ToList();
        // Files only a plugin reads (and still does: a plugin disabled since is not loaded) are drafted by the plugins, without the AI.
        var claimed = files.Where(f => f.Kind == "Claimed").Select(f => (File: f, By: contributors.ClaimersOf(f.Name))).Where(c => c.By.Count > 0).ToList();
        var byPlugins = docs.Count == 0 && images.Count == 0;
        if (byPlugins && claimed.Count == 0)
        {
            (item.Status, item.Error) = (ImportStatus.Failed, files.Any(f => f.Kind == "Claimed") ? Extractors.NotRead
                : files.Select(f => f.Note).FirstOrDefault(n => n is not null) ?? "Nothing in it could be read.");
            return;
        }

        var source = string.Join("\n\n", docs.Select(d => $"=== {d.Path} ===\n{Compact(d.Text!)}"));
        var cut = source.Length > MaxSourceChars;
        if (cut) source = source[..MaxSourceChars];
        var visible = await access.VisibleSpacesAsync(actor, ct);
        var writable = new List<Guid>();
        foreach (var s in visible) if (await access.CanAsync(actor, s, SpaceAction.CreateEdit, ct)) writable.Add(s);
        var spaces = await db.Spaces.AsNoTracking().Where(s => writable.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var folders = await db.Pages.AsNoTracking().In(new PageScope(writable)).Where(p => p.Kind == PageKind.Folder).OrderBy(p => p.Title).Take(300)
            .Select(p => new { p.Id, p.SpaceId, p.Title }).ToListAsync(ct);
        var destinations = spaces.Values.Select(s => ($"space:{s.Id:N}", $"{s.Name}{(s.IsSensitive ? " (sensitive)" : "")}"))
            .Concat(folders.Select(f => ($"folder:{f.Id:N}", $"{spaces[f.SpaceId].Name} / {f.Title}"))).ToList();
        var apps = await inventory.CanViewAsync(actor, ct)
            ? await db.InvApplications.AsNoTracking().OrderBy(a => a.NameKey).Take(300).Select(a => new { a.Id, a.Name }).ToListAsync(ct) : [];

        var pictures = images.Where(i => i.Size <= MaxPictureBytes).Take(MaxPictures).ToList();
        var picData = new List<AiGuard.Picture>();
        foreach (var p in pictures) picData.Add(new AiGuard.Picture(p.Mime ?? "image/png", await ReadAsync(p.Id, ct)));
        var data = new Dictionary<string, string>
        {
            ["files"] = source.Length > 0 ? source : "(no text: the pictures are the content)",
            ["images"] = string.Join("\n", pictures.Select(p => p.Name)),
            ["inventory"] = string.Join("\n", apps.Select(a => a.Name)),
            ["destinations"] = string.Join("\n", destinations.Select(d => $"{d.Item1} = {d.Item2}")),
        };
        var kept = KeptRegions(source);
        if (kept.Count > 0) data[KeepField] = string.Join("\n\n", kept);
        JsonNode output;
        string drafter;
        long? ms = null;
        var claimers = claimed.SelectMany(c => c.By).Distinct().Select(c => c.Name).ToList();
        if (byPlugins)
        {
            // One empty page, titled from the file name; the plugins' contributors fill it.
            output = new JsonObject
            {
                ["pages"] = new JsonArray(new JsonObject { ["title"] = CleanTitle(claimed[0].File.Name), ["blocks"] = new JsonArray() }),
                ["tags"] = new JsonArray(), ["apps"] = new JsonArray(), ["owner"] = "", ["reviewMonths"] = 0, ["destination"] = "inbox",
            };
            drafter = $"Plugin: {string.Join(", ", claimers)}";
        }
        else
        {
            var r = await ai.RunAsync(actor, AiFeatures.Import, DraftingTask(kept.Count > 0), data, ProposalSchema(), ct, picData,
                salvage: content => Lifted(content, ProposalSchema()));
            if (r.Problem is { } p0)
            {
                (item.Status, item.Error) = p0.Status == 422 ? (ImportStatus.Blocked, "It holds text addressed to the AI, so it was not sent.") : (ImportStatus.Failed, p0.Message ?? "The AI could not draft it.");
                return;
            }
            (output, drafter, ms) = (r.Value!.Output, r.Value.Model, r.Value.Milliseconds);
        }

        // Pages from the blocks; pictures by name, any the AI did not place go at the end of the first page.
        var byName = images.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<Guid>();
        string? Src(string name) { if (!byName.TryGetValue(Path.GetFileName(name.Trim()), out var f)) return null; placed.Add(f.Id); return FileUrl + f.Id.ToString("N"); }
        var pages = new List<JsonObject>();
        foreach (var page in output["pages"]!.AsArray())
        {
            var doc = DocBuilder.Build(DocBuilder.ReadBlocks(page!["blocks"] as JsonArray), Src);
            pages.Add(new JsonObject { ["title"] = Trim(page["title"]!.GetValue<string>().Trim(), 200), ["doc"] = doc });
        }
        foreach (var img in images.Where(i => !placed.Contains(i.Id)))
            pages[0]["doc"]!["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["src"] = FileUrl + img.Id.ToString("N"), ["alt"] = img.Name } });

        var findings = Sensitive.Find(source);
        var warnings = files.Where(f => f.Note is not null && f.Kind != "Blocked").Select(f => $"{f.Name}: {f.Note}").ToList();
        if (cut) warnings.Add($"Only the first {MaxSourceChars:N0} characters were given to the model; the draft does not cover the rest. Split the file, or add the rest by hand.");
        var outWords = pages.SelectMany(p => AiGuard.Words(DocText.Plain(p["doc"]!.ToJsonString()))).Select(w => w.ToLowerInvariant()).ToList();
        var inWords = new HashSet<string>(AiGuard.Words(source).Select(w => w.ToLowerInvariant()));
        var added = outWords.Where(w => !inWords.Contains(w) && w.Length > 3).Distinct().ToList();
        if (source.Length > 0 && outWords.Count > 0 && added.Count > Math.Max(8, outWords.Distinct().Count() / 10))
            warnings.Add($"The draft uses words that are not in the source ({string.Join(", ", added.Take(12))}). Confirm the document is correct before saving.");
        var given = await ContributeAsync(item, batch, files, findings.Count > 0, pages, warnings, ct, byPlugins ? [] : [.. pictures.Zip(picData)], actor, byPlugins ? "" : source);
        if (byPlugins && pages[0]["doc"]!["content"]!.AsArray().Count == 0)
        {
            (item.Status, item.Error) = (ImportStatus.Failed, given
                ? $"Only {string.Join(" and ", claimers)} reads this kind of file, and it added nothing to the draft. The file is kept as an attachment."
                : "Only a plugin reads this kind of file, and plugins are not given files that go to a sensitive space. The file is kept as an attachment.");
            return;
        }

        var tags = (output["tags"]?.AsArray() ?? []).Select(t => t!.GetValue<string>().Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 50 && !t.Contains(',')).ToList();
        var appliesTo = new JsonArray();
        foreach (var a in output["apps"]?.AsArray() ?? [])
        {
            var name = a!["name"]?.GetValue<string>()?.Trim() ?? "";
            var version = a["version"]?.GetValue<string>()?.Trim() ?? "";
            var match = apps.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (name.Length == 0) continue;
            // Tags in App:Version form (App:ALL when no version is named), only for applications in the inventory; others by name.
            var tag = (match is null ? name : $"{match.Name}:{(version.Length > 0 ? version : "ALL")}").ToLowerInvariant();
            if (tag.Length <= 50 && !tag.Contains(',')) tags.Add(tag);
            if (match is null) continue;
            var installed = await db.InvInstalls.AsNoTracking().Where(i => i.ApplicationId == match.Id).Select(i => i.Server).ToListAsync(ct);
            var servers = (a["servers"]?.AsArray() ?? []).Select(s => s!.GetValue<string>()).Select(s => installed.FirstOrDefault(i => string.Equals(i, s, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>().Distinct().ToList();
            appliesTo.Add(new JsonObject { ["applicationId"] = match.Id, ["name"] = match.Name, ["servers"] = new JsonArray([.. servers.Select(s => (JsonNode)s)]) });
        }
        var ownerName = output["owner"]?.GetValue<string>()?.Trim() ?? "";
        JsonObject? owner = null;
        if (ownerName.Length > 0)
        {
            var g = await db.KbGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Name == ownerName, ct);
            var u = g is null ? await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.DisplayName == ownerName && x.DeletedAt == null, ct) : null;
            owner = g is not null ? new JsonObject { ["type"] = "Group", ["id"] = g.Key, ["name"] = g.Name }
                : u is not null ? new JsonObject { ["type"] = "User", ["id"] = u.Id.ToString("D"), ["name"] = u.DisplayName } : null;
        }
        var review = output["reviewMonths"]?.GetValue<int>() ?? 0;

        // Where to: the importer's choice at upload wins; the AI's suggestion is shown beside it.
        var suggested = output["destination"]?.GetValue<string>() ?? "inbox";
        JsonObject? Place(Guid space, Guid? parent) => spaces.ContainsKey(space) ? new JsonObject { ["spaceId"] = space, ["parentId"] = parent } : null;
        var suggestion = suggested.StartsWith("space:", StringComparison.Ordinal) && Guid.TryParse(suggested[6..], out var sid) ? Place(sid, null)
            : suggested.StartsWith("folder:", StringComparison.Ordinal) && Guid.TryParse(suggested[7..], out var fid) && folders.FirstOrDefault(f => f.Id == fid) is { } folder ? Place(folder.SpaceId, folder.Id) : null;
        // Secrets found: a sensitive space the importer can write to is suggested instead.
        if (findings.Count > 0 && (suggestion is null || !spaces[suggestion["spaceId"]!.GetValue<Guid>()].IsSensitive)
            && spaces.Values.Where(s => s.IsSensitive).OrderBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault() is { } safe)
            suggestion = Place(safe.Id, null);
        var chosen = batch.TargetSpaceId is { } ts ? Place(ts, batch.TargetParentId) : null;

        // A page like this one already: offer to update it instead of a copy.
        var duplicates = new JsonArray();
        var title = pages[0]["title"]!.GetValue<string>();
        var hits = await search.SearchAsync(new Kb.Core.Search.SearchQuery(title, visible, null, 5, 0), ct);
        var srcSet = new HashSet<string>(AiGuard.Words(source.Length > 0 ? source : title).Select(w => w.ToLowerInvariant()));
        foreach (var hit in hits.Hits)
        {
            var hp = await db.Pages.AsNoTracking().In(new PageScope(visible)).Where(x => x.Id == hit.PageId && x.Kind == PageKind.Page).Select(x => new { x.Id, x.Title, x.ContentJson, x.SpaceId }).FirstOrDefaultAsync(ct);
            if (hp is null) continue;
            var words = new HashSet<string>(AiGuard.Words(hp.Title + " " + DocText.Plain(hp.ContentJson, renderers)).Select(w => w.ToLowerInvariant()));
            var score = words.Count == 0 ? 0 : (double)words.Intersect(srcSet).Count() / words.Union(srcSet).Count();
            if (score >= 0.3 || string.Equals(hp.Title, title, StringComparison.OrdinalIgnoreCase))
                duplicates.Add(new JsonObject { ["pageId"] = hp.Id, ["title"] = hp.Title, ["score"] = Math.Round(score, 2), ["spaceId"] = hp.SpaceId });
        }

        var proposal = new JsonObject
        {
            ["pages"] = new JsonArray([.. pages.Select(p => (JsonNode)new JsonObject
            {
                ["title"] = p["title"]!.GetValue<string>(),
                ["contentJson"] = p["doc"]!.ToJsonString(),
                ["redactedJson"] = Sensitive.Redact(p["doc"]!.AsObject(), findings).ToJsonString(),
            })]),
            ["tags"] = new JsonArray([.. tags.Distinct().Take(20).Select(t => (JsonNode)t)]),
            ["appliesTo"] = appliesTo,
            ["owner"] = owner,
            ["reviewMonths"] = review > 0 ? review : null,
            ["destination"] = (chosen ?? suggestion)?.DeepClone(), // a node has one parent: the suggestion is also below
            ["suggestedDestination"] = suggestion,
            ["sensitive"] = new JsonArray([.. findings.Select(f => (JsonNode)new JsonObject { ["kind"] = f.Kind, ["sample"] = f.Sample })]),
            ["duplicates"] = duplicates,
            ["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode)w)]),
            ["model"] = drafter,
        };
        proposal["ms"] = ms;
        proposal["found"] = Found(output["found"] as JsonObject, source);
        item.Title = CleanTitle(title, fileName: false);
        item.ProposalJson = proposal.ToJsonString();
        (item.Status, item.Error) = (ImportStatus.Ready, null);
    }

    /// <summary>
    /// Plugins' import contributors add their nodes to the end of the first page; then their draft contributors read the pages and add
    /// where they say. Each is named in the warnings so the importer sees what a plugin added. They never read an item that holds a
    /// secret or that goes to a sensitive space. The pages are drafted anew on every retry, so nothing is added twice.
    /// </summary>
    /// <returns>False when the plugins were not given the files: they hold a secret, or go to a sensitive space.</returns>
    private async Task<bool> ContributeAsync(ImportItem item, ImportBatch batch, IReadOnlyList<ImportFile> files, bool secret, List<JsonObject> pages, List<string> warnings, CancellationToken ct,
        IReadOnlyList<(ImportFile File, AiGuard.Picture Data)>? sent = null, Actor? actor = null, string source = "")
    {
        if (!contributors.Any) return true;
        if (secret || (batch.TargetSpaceId is { } ts && await db.Spaces.AnyAsync(s => s.Id == ts && s.IsSensitive, ct)))
        {
            warnings.Add("Plugins were not given these files: they hold a secret, or go to a sensitive space.");
            return false;
        }
        var read = files.Where(f => !f.Embedded && f.Kind is not ("Blocked" or "Container" or "Skipped")).ToList();
        var contents = pages.Select(p => p["doc"]!["content"]!.AsArray()).ToList();
        foreach (var c in await contributors.RunAsync(item, read, ReadAsync, ct))
        {
            foreach (var n in c.Nodes) contents[0].Add(n);
            warnings.Add($"{c.PluginName} added {c.Nodes.Count} block{(c.Nodes.Count == 1 ? "" : "s")} from {c.FileName} at the end of the first page.");
        }
        // 0.11.0 (draft): the tools first, so the draft contributors see what the tools added (ImportDraft.ToolBlocks) and need not make it again.
        var tooled = actor is not null ? await ToolsAsync(item, actor, source, pages, contents, warnings, ct) : [];
        // Counted from the nodes themselves for each contributor, so blocks an earlier contributor added do not throw the numbers off.
        IReadOnlyList<DraftToolBlock> ToolBlocks() => [.. tooled.SelectMany(t => contents.Select((c, page) => (page, index: c.IndexOf(t.Node), c))
            .Where(x => x.index >= 0)
            .Select(x => new DraftToolBlock(x.page, x.index, t.PluginId, t.Tool) { After = t.After is null ? -1 : x.c.IndexOf(t.After) }))];
        var pictures = contributors.AnyDraft && actor is not null ? await DiagramsAsync(actor, sent ?? [], pages, warnings, ct) : [];
        foreach (var c in await contributors.RunDraftAsync(item, () => [.. pages.Select(p => new DraftPage(p["title"]!.GetValue<string>(), p["doc"]!.ToJsonString()))],
            [.. read.Select(f => f.Path)], c => ImportContributors.Place(contents, c.Additions), ct, pictures, ToolBlocks))
        {
            var where = string.Join(", ", c.Additions.Select(a => a.Page).Distinct().Order().Select(i => $"\"{pages[i]["title"]!.GetValue<string>()}\""));
            warnings.Add($"{c.PluginName} added {c.Count} block{(c.Count == 1 ? "" : "s")} to {where}.");
        }
        return true;
    }

    /// <summary>
    /// The pictures sent to the AI, as the draft shows them, each with the diagram the AI reads from it (contract 0.10.0): one more request
    /// per picture, for the first <see cref="DiagramReader.MaxPictures"/>. Not a diagram: none, and nothing is said. A request that fails,
    /// or an answer that says diagram and holds nothing usable: none, and one warning. The draft is left as it is either way.
    /// </summary>
    private async Task<List<DraftPicture>> DiagramsAsync(Actor actor, IReadOnlyList<(ImportFile File, AiGuard.Picture Data)> sent, List<JsonObject> pages, List<string> warnings,
        CancellationToken ct)
    {
        var found = new List<DraftPicture>();
        for (var i = 0; i < sent.Count; i++)
        {
            var (file, data) = sent[i];
            var src = FileUrl + file.Id.ToString("N");
            var page = Math.Max(0, pages.FindIndex(p => p["doc"]!["content"]!.AsArray().Any(n => n is not null && ImportContributors.Shows(n, src))));
            PictureDiagram? diagram = null;
            if (i < DiagramReader.MaxPictures)
            {
                // The whole picture, and its enlarged quarters when it can be cut: the labels of a big diagram are read far better then.
                var quarters = PictureQuarters.Cut(data, log);
                AiGuard.Picture[] shown = quarters is null ? [data] : [data, .. quarters];
                string Task(string task) => quarters is null ? task : task + " " + DiagramReader.QuartersNote;
                // First the boxes, containers and texts; then, given their list, the lines between them. Each answer fits the Admin's token
                // limit; one cut off at it keeps its complete lines.
                var r = await ai.RunAsync(actor, AiFeatures.Import, Task(DiagramReader.ItemsTask), new Dictionary<string, string> { ["image"] = file.Name }, DiagramReader.ItemsSchema(), ct, shown,
                    DiagramReader.Salvage);
                if (r.Problem?.Message?.StartsWith(AiService.CutOff, StringComparison.Ordinal) == true)
                {
                    // Too big with positions for the token limit, and the server kept none of it: once more, without positions and second lines.
                    r = await ai.RunAsync(actor, AiFeatures.Import, Task(DiagramReader.ShortItemsTask), new Dictionary<string, string> { ["image"] = file.Name }, DiagramReader.ShortItemsSchema(), ct, shown,
                        DiagramReader.Salvage);
                    if (r.Problem is null) warnings.Add($"The diagram in {file.Name} was read without the places of its boxes: with them it was larger than the AI's answer limit (Max tokens in Administration > AI).");
                }
                JsonNode? drawn = null;
                if (r.Value is { } items && items.Output["diagram"] is JsonValue yes && yes.GetValueKind() == JsonValueKind.True)
                {
                    var l = await ai.RunAsync(actor, AiFeatures.Import, Task(DiagramReader.LinesTask),
                        new Dictionary<string, string> { ["image"] = file.Name, ["items"] = DiagramReader.ItemList(items.Output) }, DiagramReader.LinesSchema(), ct, shown, DiagramReader.Salvage);
                    drawn = l.Value?.Output;
                    if (l.Problem is not null) warnings.Add($"The lines of the diagram in {file.Name} could not be read: {l.Problem.Message ?? "the AI did not answer"}");
                    if (items.Cut || l.Value?.Cut == true) warnings.Add($"The diagram in {file.Name} was larger than the AI's answer limit (Max tokens in Administration > AI): only part of it was read.");
                }
                if (r.Problem is not null) warnings.Add($"The diagram in {file.Name} could not be read: {r.Problem.Message ?? "the AI did not answer"}");
                else if ((diagram = DiagramReader.Read(r.Value!.Output, drawn)) is null)
                {
                    if (r.Value.Output["diagram"] is JsonValue said && said.GetValueKind() == JsonValueKind.True) warnings.Add($"The diagram in {file.Name} could not be read.");
                }
                else if (AiGuard.Tripwire(DiagramReader.Texts(diagram)) is not null)
                {
                    diagram = null;
                    warnings.Add($"The diagram in {file.Name} holds text addressed to the AI, so it was not passed on to plugins.");
                }
            }
            var size = Kb.Web.Exports.ImageSize(data.Data);
            found.Add(new DraftPicture(src, file.Name, page) { Diagram = diagram, Width = size?.Width ?? 0, Height = size?.Height ?? 0 });
        }
        return found;
    }

    // ---------- accepting

    /// <summary>
    /// Makes the page(s), or a new version of an existing page, from what the importer reviewed. Every step is the normal action,
    /// sent as them: they need the rights to create or edit there. Pictures become the space's images; the originals are attached.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<Guid>>> AcceptAsync(IActionBus bus, Actor actor, ImportItem item, AcceptRequest req, CancellationToken ct)
    {
        if (req.Pages is not { Count: > 0 } pages) return Problem.Invalid("pages", "At least one page.");
        if (req.UpdatePageId is not null && pages.Count != 1) return Problem.Invalid("pages", "One page updates one page.");
        var files = await db.ImportFiles.Where(f => f.ItemId == item.Id).ToListAsync(ct);
        Guid spaceId;
        string? baseVersion = null;
        if (req.UpdatePageId is { } target)
        {
            var existing = await bus.SendAsync(new ReadPage { PageId = target }, ct);
            if (existing.Problem is { } p) return p;
            (spaceId, baseVersion) = (existing.Value!.SpaceId, existing.Value.Version);
        }
        else if (req.SpaceId is { } s) spaceId = s;
        else return Problem.Invalid("destination", "Choose where the page goes.");
        var parentId = req.ParentId;
        // A folder typed in at placement: made first, under the chosen parent, by the normal action (the importer's rights, audited).
        if (req.UpdatePageId is null && req.NewFolder?.Trim() is { Length: > 0 } folderName)
        {
            if (folderName.Length > 200) return Problem.Invalid("newFolder", "200 characters at most.");
            var folder = await bus.SendAsync(new CreatePage { SpaceId = spaceId, ParentId = parentId, Kind = "Folder", Title = folderName, ContentJson = """{"type":"doc","content":[]}""" }, ct);
            if (folder.Problem is { } fp) return fp;
            parentId = folder.Value!.Id;
        }

        // Pictures of the import become images of the target space, under the importer's rights.
        var imageUrls = new Dictionary<string, string>();
        async Task<Problem?> ImagesAsync(string json)
        {
            foreach (var id in ImportImageIds(json))
            {
                var key = FileUrl + id.ToString("N");
                if (imageUrls.ContainsKey(key)) continue;
                var f = files.FirstOrDefault(x => x.Id == id && x.Kind == "Image");
                if (f is null) return Problem.Invalid("pages", "A picture is not part of this import.");
                var img = await bus.SendAsync(new AddImage { SpaceId = spaceId, File = new BytesUpload(f.Name, await ReadAsync(f.Id, ct)) }, ct);
                if (img.Problem is { } p) return p;
                imageUrls[key] = $"/api/v1/images/{img.Value!.Id}";
            }
            return null;
        }
        foreach (var page in pages)
            if (await ImagesAsync(page.ContentJson) is { } bad) return bad;
        string Swap(string json) => imageUrls.Aggregate(json, (j, kv) => j.Replace(kv.Key, kv.Value, StringComparison.Ordinal));

        var made = new List<Guid>();
        foreach (var page in pages)
        {
            Outcome<PageDto> saved = req.UpdatePageId is { } upd
                ? await bus.SendAsync(new SavePage { PageId = upd, Title = page.Title, ContentJson = Swap(page.ContentJson), BaseVersion = baseVersion!, Major = true, Note = $"Imported from {string.Join(", ", files.Where(f => f.ParentId is null).Select(f => f.Name).Take(5))}", Tags = page.Tags }, ct)
                : await bus.SendAsync(new CreatePage { SpaceId = spaceId, ParentId = parentId, Kind = "Page", Title = page.Title, ContentJson = Swap(page.ContentJson), Tags = page.Tags }, ct);
            if (saved.Problem is { } p) return made.Count == 0 ? p : new Problem(p.Status, $"{made.Count} page(s) were made before this failed: {p.Message ?? p.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            made.Add(saved.Value!.Id);
            if (req.Details is { } d)
            {
                var det = await bus.SendAsync(new Kb.Web.Actions.SavePageDetails { PageId = saved.Value.Id, Details = d }, ct);
                if (det.Problem is { } dp && dp.Status != 403) return dp;
            }
            await audit.AppendAsync(new AuditEvent(AuditActions.ImportAccept, actor.UserId, "page", saved.Value.Id.ToString(), spaceId, new Dictionary<string, string>
            {
                ["item"] = item.Id.ToString(), ["files"] = string.Join("; ", files.Where(f => !f.Embedded).Select(f => $"{f.Path} sha256:{f.Sha256}")).Truncate(3000),
                ["update"] = (req.UpdatePageId is not null).ToString(),
            }), ct);
        }
        // The originals, for provenance: every file the importer gave (not the pictures inside documents, not what was blocked).
        if (req.AttachOriginals)
            foreach (var f in files.Where(f => !f.Embedded && f.Kind is not ("Blocked" or "Container")))
            {
                var a = await bus.SendAsync(new AddAttachment { PageId = made[0], File = new BytesUpload(f.Name, await ReadAsync(f.Id, ct)) }, ct);
                if (a.Problem is { Status: not 400 } ap) return ap;
            }
        (item.Status, item.PageId, item.UpdatedAt) = (ImportStatus.Accepted, made[0], time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await DropFilesAsync(item.Id, actor.UserId, req.AttachOriginals ? "accepted, attached to the page" : "accepted, not attached", ct);
        return Outcome.List<Guid>(made);
    }

    public static IEnumerable<Guid> ImportImageIds(string json)
    {
        var at = 0;
        while ((at = json.IndexOf(FileUrl, at, StringComparison.Ordinal)) >= 0)
        {
            at += FileUrl.Length;
            if (at + 32 <= json.Length && Guid.TryParseExact(json.AsSpan(at, 32), "N", out var id)) yield return id;
        }
    }

    /// <summary>
    /// The stored originals of an item and the text read from them are deleted for good (no recycle bin) once it is accepted or
    /// discarded; only what the importer kept lives on (attachments, images). The names and hashes stay for provenance. Audited.
    /// </summary>
    public async Task DropFilesAsync(Guid itemId, Guid? by, string reason, CancellationToken ct)
    {
        var files = await db.ImportFiles.Where(f => f.ItemId == itemId).ToListAsync(ct);
        foreach (var f in files) await store.DeleteAsync(Key(f.Id), ct);
        foreach (var f in files) f.Text = null;
        await db.SaveChangesAsync(ct);
        if (files.Count > 0)
            await audit.AppendAsync(new AuditEvent(AuditActions.ImportPurge, by, "importItem", itemId.ToString(), Data: new Dictionary<string, string>
            {
                ["reason"] = reason, ["files"] = string.Join("; ", files.Select(f => $"{f.Path} sha256:{f.Sha256}")).Truncate(3000), ["count"] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }), ct);
    }

    /// <summary>
    /// The text the model reads: a line repeated over and over (a log, a pasted runbook) once, with how many times; runs of blank
    /// lines as one. It is what costs the model time and says nothing more.
    /// </summary>
    public static string Compact(string text)
    {
        var sb = new StringBuilder(text.Length);
        string? last = null;
        var repeats = 0;
        var blank = 0;
        void Flush() { if (repeats > 0) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"(the line above {repeats} more time{(repeats == 1 ? "" : "s")})\n"); repeats = 0; }
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0) { Flush(); if (++blank == 1) sb.Append('\n'); last = null; continue; }
            blank = 0;
            if (line == last) { repeats++; continue; }
            Flush();
            sb.Append(line).Append('\n');
            last = line;
        }
        Flush();
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What the model says the files name, kept only where the source says it too (a name, server, version it made up is dropped),
    /// with the change, incident and case numbers the code finds itself. Conflicts are the model's own words, shown as such.
    /// </summary>
    public static JsonObject Found(JsonObject? found, string source)
    {
        JsonArray Present(string key, IEnumerable<string>? extra = null) => new([.. (found?[key] as JsonArray ?? []).Select(x => x?.GetValue<string>()?.Trim() ?? "")
            .Where(x => x.Length > 0 && source.Contains(x, StringComparison.OrdinalIgnoreCase)).Concat(extra ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).Select(x => (JsonNode)x)]);
        return new JsonObject
        {
            ["people"] = Present("people"), ["servers"] = Present("servers"), ["applications"] = Present("applications"),
            ["tickets"] = Present("tickets", Identifier().Matches(source).Select(m => m.Value)), ["versions"] = Present("versions"),
            ["conflicts"] = new JsonArray([.. (found?["conflicts"] as JsonArray ?? []).Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).Take(5).Select(x => (JsonNode)x)]),
        };
    }

    /// <summary>
    /// A page title from a file name: no extension, separators as spaces, a run of one character kept to three, at most 100
    /// characters (cut at a word). The file's own name stays in the import's files, for provenance.
    /// </summary>
    public static string CleanTitle(string name, bool fileName = true)
    {
        var t = fileName ? Path.GetFileNameWithoutExtension(name.Trim()) : name.Trim();
        if (t.Length == 0) t = name.Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[_]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(.)\1{3,}", "$1$1$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length <= 100) return t.Length > 0 ? t : "Untitled";
        var cut = t[..100];
        var space = cut.LastIndexOf(' ');
        return (space >= 60 ? cut[..space] : cut).TrimEnd() + "…";
    }
}

public sealed record AcceptPage(string Title, string ContentJson, IReadOnlyList<string>? Tags);

/// <summary>
/// Pages: what the importer reviewed (title, content, tags). Either UpdatePageId (a new version of that page) or SpaceId and ParentId.
/// Details: Applies to, owner and review cycle to set, or null. AttachOriginals: attach the original files to the first page.
/// NewFolder: a folder to make under ParentId (or at the top of the space) and put the page(s) in.
/// </summary>
public sealed record AcceptRequest(IReadOnlyList<AcceptPage>? Pages, Guid? SpaceId, Guid? ParentId, Guid? UpdatePageId, Kb.Web.Actions.PageDetailsRequest? Details, bool AttachOriginals = false,
    string? NewFolder = null);

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
