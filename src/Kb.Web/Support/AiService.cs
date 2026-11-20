using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Kb.Web;

/// <summary>What one AI call gave back. ReasoningEmpty: the model returned no reasoning text (thinking was off).</summary>
/// <summary>Cut: the model stopped at the token limit and <see cref="Output"/> is what a caller's salvage kept of the answer.</summary>
public sealed record AiResult(JsonNode Output, string Model, int PromptTokens, int CompletionTokens, long Milliseconds, bool ReasoningEmpty, bool Cut = false);

/// <summary>Calls in the last minute, per person and per API token, on this server.</summary>
public sealed class AiRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _calls = new();

    // shortcut: counted per server, so N servers allow N times as much; move it to the database if that matters.
    /// <summary>Whether a call would be allowed now, without counting one: background work waits for a free slot.</summary>
    public bool Free(string key, int perMinute)
    {
        if (!_calls.TryGetValue(key, out var q)) return true;
        lock (q) { var now = time.GetUtcNow(); return q.Count(t => now - t < TimeSpan.FromMinutes(1)) < perMinute; }
    }

    public bool Allow(string key, int perMinute)
    {
        var now = time.GetUtcNow();
        var q = _calls.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= perMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// The AI service: an OpenAI-compatible chat completions client held to <see cref="AiGuard"/>. Features call
/// <see cref="RunAsync"/>; nothing it returns is run or saved by it. Every call, refused or not, is audited (ai.call).
/// </summary>
public sealed class AiService(KbDbContext db, IAccess access, IHttpClientFactory http, IHttpContextAccessor web, IAuditLog audit,
    IDataProtectionProvider protection, AiRateLimiter limiter, IEnumerable<IOutboundRequestDecorator> decorators, TimeProvider time, ILogger<AiService> log)
{
    public const string SettingsKey = "Ai.Settings", KeyKey = "Ai.ApiKey", ClientName = "kb.ai", TestFeature = "test";
    /// <summary>How the message of a refusal starts when the model stopped at the token limit, whether the server said so in its answer or in an error.</summary>
    public const string CutOff = "The model's reply was cut off at the limit";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly IDataProtector _key = protection.CreateProtector("kb.ai.key");

    public async Task<AiSettings> SettingsAsync(CancellationToken ct)
    {
        var json = await db.GetAsync(SettingsKey, ct);
        AiSettings s;
        try { s = json is null ? new AiSettings() : JsonSerializer.Deserialize<AiSettings>(json, Json) ?? new AiSettings(); }
        catch (JsonException) { s = new AiSettings(); }
        // A feature added later starts Off.
        foreach (var f in AiFeatures.All) s.Features.TryAdd(f, AiAccess.Off);
        // A prompt that is an earlier default was never customized: it is the default of now.
        if (AiGuard.IsPreviousDefault(s.Ruleset)) s = s with { Ruleset = AiGuard.DefaultRuleset };
        return s;
    }

    public async Task SaveAsync(AiSettings s, Guid? by, CancellationToken ct) => await db.SetAsync(SettingsKey, JsonSerializer.Serialize(s, Json), by, time.GetUtcNow(), ct);

    public async Task<bool> KeySetAsync(CancellationToken ct) => !string.IsNullOrEmpty(await db.GetAsync(KeyKey, ct));

    /// <summary>Null clears it. The key is kept protected (data protection) and only ever read here, to call the model.</summary>
    public async Task SetKeyAsync(string? key, Guid? by, CancellationToken ct) =>
        await db.SetAsync(KeyKey, string.IsNullOrEmpty(key) ? null : _key.Protect(key), by, time.GetUtcNow(), ct);

    /// <summary>
    /// The model ids a server lists at {base}/v1/models, through the outbound hook like every AI call. The key is the one
    /// typed (not yet saved) or else the saved one. Only ids come back, never the server's text.
    /// </summary>
    public async Task<Outcome<string[]>> ListModelsAsync(string baseUrl, string? typedKey, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return Problem.Invalid("baseUrl", "An http or https address.");
        var root = u.ToString().TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1") + "/models");
        if ((string.IsNullOrEmpty(typedKey) ? await KeyAsync(ct) : typedKey) is { Length: > 0 } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            foreach (var d in decorators) await d.DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, request), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Problem(502, $"A plugin that adds to the AI's requests failed ({e.GetType().Name}).");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.CreateClient(ClientName).SendAsync(request, timeout.Token);
            if (Redirect(response) is { } moved) return new Problem(502, moved);
            if (!response.IsSuccessStatusCode) return new Problem(502, $"The server answered {(int)response.StatusCode} at /v1/models.");
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (text.Length > 1_000_000) return new Problem(502, "The model list is too large.");
            var ids = (JsonNode.Parse(text)?["data"] as JsonArray ?? [])
                .Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Where(id => id.Length is > 0 and <= 200)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
            if (ids.Length == 0) return new Problem(502, "The server lists no models at /v1/models.");
            return ids;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return new Problem(502, e is OperationCanceledException ? "No answer from the server in 15 seconds." : "Could not reach the server, or it did not answer with a model list.");
        }
    }

    private async Task<string?> KeyAsync(CancellationToken ct) => await db.GetAsync(KeyKey, ct) is { Length: > 0 } p ? _key.Unprotect(p) : null;

    /// <summary>Who may use a feature: Off nobody, Admins only Admins, Everyone anyone signed in. The test is an Admin's.</summary>
    public async Task<bool> MayUseAsync(Actor actor, string feature, AiSettings s, CancellationToken ct)
    {
        if (actor.UserId is null) return false;
        if (feature == TestFeature) return await access.IsAdminAsync(actor, ct);
        return s.Features.GetValueOrDefault(feature) switch
        {
            AiAccess.Everyone => true,
            AiAccess.Admins => await access.IsAdminAsync(actor, ct),
            _ => false,
        };
    }

    /// <summary>
    /// One call. task: the feature's instruction (server text). data: the content, as named fields; never instructions.
    /// schema: what the answer must be. Refused before the call: AI off, the feature not open to the caller, too much data, a
    /// tripwire pattern in the data, too many calls. Refused after: an answer that is not JSON matching the schema.
    /// </summary>
    /// <param name="salvage">
    /// For an answer that is not JSON matching the schema (one cut off at the token limit): what of it the caller can still use, or null.
    /// What it keeps is returned, marked <see cref="AiResult.Cut"/> when the model stopped at the limit.
    /// </param>
    public async Task<Outcome<AiResult>> RunAsync(Actor actor, string feature, string task, IReadOnlyDictionary<string, string> data, JsonObject schema, CancellationToken ct,
        IReadOnlyList<AiGuard.Picture>? pictures = null, Func<string, JsonNode?>? salvage = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var s = await SettingsAsync(ct);
        async Task<Problem> RefuseAsync(string outcome, Problem p, string? detail = null)
        {
            await AuditAsync(actor, feature, outcome, 0, 0, 0, detail);
            return p;
        }

        if (!s.Enabled || string.IsNullOrWhiteSpace(s.BaseUrl) || string.IsNullOrWhiteSpace(s.Model)) return await RefuseAsync("off", Problem.Conflict("AI is off."));
        if (!await MayUseAsync(actor, feature, s, ct)) return await RefuseAsync("denied", new Problem(403, "This AI feature is not open to you."));
        if (data.Values.Sum(v => (long)v.Length) > s.MaxInputChars) return await RefuseAsync("too-large", Problem.Invalid("data", $"{s.MaxInputChars} characters at most."));
        if (AiGuard.Tripwire(data) is { } found)
            return await RefuseAsync("refused", Problem.Refused("The content holds text addressed to the AI, so it was not sent."), found.Length > 80 ? found[..80] : found);
        if (!limiter.Allow($"u:{actor.UserId}", s.UserPerMinute) || (actor.TokenId is { } token && !limiter.Allow($"t:{token}", s.TokenPerMinute)))
            return await RefuseAsync("limited", new Problem(429, "Too many AI calls. Wait a minute."));

        if (pictures?.Sum(p => (long)p.Data.Length) > 32L * 1024 * 1024) return await RefuseAsync("too-large", Problem.Invalid("pictures", "32 MB of pictures at most."));
        var body = AiGuard.Request(s, AiGuard.SystemMessage(s.Ruleset, task, schema), AiGuard.UserMessage(data), schema, pictures);
        JsonNode? answer = null, output = null;
        string content = "", reasoning = "", model = s.Model;
        int inTokens = 0, outTokens = 0;
        long ms = 0;
        var cutOff = false;
        // A reply in JSON that misses the structure (not cut off, and nothing a caller's salvage keeps) is answered once with where it
        // misses, in the app's words, and asked again: some servers do not hold the model to the schema.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (sent, failed, took) = await SendAsync(s, body, feature, RefuseAsync, ct);
            if (failed is not null) return failed;
            answer = sent;
            ms += took;
            var message = answer?["choices"]?[0]?["message"];
            content = message?["content"]?.GetValue<string>() ?? "";
            reasoning = message?["reasoning_content"]?.GetValue<string>() ?? message?["reasoning"]?.GetValue<string>() ?? "";
            inTokens += answer?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
            outTokens += answer?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
            model = answer?["model"]?.GetValue<string>() ?? s.Model;
            try { output = JsonNode.Parse(StripFence(content)); }
            catch (JsonException) { output = null; }
            cutOff = answer?["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "length";
            if (attempt > 0 || output is null || cutOff || AiGuard.SchemaError(output, schema) is not { } miss || salvage?.Invoke(content) is not null) break;
            await AuditAsync(actor, feature, "retry", inTokens, outTokens, took, miss);
            body["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            body["messages"]!.AsArray().Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"That reply does not match the required structure ({miss}). Answer again with the whole reply in the required structure.",
            });
        }
        if ((output is null || AiGuard.SchemaError(output, schema) is not null) && salvage?.Invoke(content) is { } kept)
        {
            await AuditAsync(actor, feature, "partial", inTokens, outTokens, ms, cutOff ? "cut at the token limit; complete items kept" : "not the schema; complete items kept");
            return new AiResult(kept, model, inTokens, outTokens, ms, string.IsNullOrWhiteSpace(reasoning), cutOff);
        }
        if (output is null || AiGuard.SchemaError(output, schema) is { } bad)
        {
            await AuditAsync(actor, feature, "invalid-output", inTokens, outTokens, ms, output is null ? "not JSON" : AiGuard.SchemaError(output, schema));
            var cut = answer?["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "length";
            return new Problem(502, cut ? $"{CutOff} of {s.MaxTokens} tokens (Max tokens in Administration > AI)."
                : output is null ? "The model's reply was not JSON." : "The model's reply didn't match the required structure.");
        }
        await AuditAsync(actor, feature, "ok", inTokens, outTokens, ms, null);
        return new AiResult(output, model, inTokens, outTokens, ms, string.IsNullOrWhiteSpace(reasoning));
    }

    /// <summary>
    /// Sends one request to the model server: the Admin's address and key, the plugins that add headers, the timeout. The answer, or the
    /// problem (already audited through <paramref name="refuse"/>), and how long it took.
    /// </summary>
    private async Task<(JsonNode? Answer, Problem? Problem, long Ms)> SendAsync(AiSettings s, JsonObject body, string feature,
        Func<string, Problem, string?, Task<Problem>> refuse, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var root = s.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1") + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (await KeyAsync(ct) is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // A plugin that adds headers (an access gateway) and fails: the call is refused, never sent without them.
            try
            {
                foreach (var d in decorators) await d.DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, request), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (null, await refuse("error", new Problem(502, $"A plugin that adds to the AI's requests failed ({e.GetType().Name})."), e.Message), 0);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 5, 600)));
            using var response = await http.CreateClient(ClientName).SendAsync(request, timeout.Token);
            if (Redirect(response) is { } moved) return (null, await refuse("error", new Problem(502, moved), moved), 0);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // What the server said, shortened, so the next failure names its cause. It is the server's own error text: no key and no data of ours.
                var said = text.ReplaceLineEndings(" ");
                log.LogWarning("The model server answered {Status} for {Feature} after {Ms} ms: {Said}", (int)response.StatusCode, feature, clock.ElapsedMilliseconds, said.Length > 300 ? said[..300] : said);
                // Some servers answer an error when the reply reached the token limit before the structure was complete.
                if (text.Contains("finish_reason=length", StringComparison.Ordinal) || text.Contains("\"finish_reason\": \"length\"", StringComparison.Ordinal))
                    return (null, await refuse("error", new Problem(502, $"{CutOff} of {s.MaxTokens} tokens (Max tokens in Administration > AI)."), text.Length > 200 ? text[..200] : text), 0);
                return (null, await refuse("error", new Problem(502, $"The model server answered {(int)response.StatusCode} ({response.ReasonPhrase}): {ServerReason(text) ?? "it may be overloaded, or the content too large for it."}"),
                    text.Length > 200 ? text[..200] : text), 0);
            }
            return (JsonNode.Parse(text), null, clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return (null, await refuse("error", new Problem(502, e is OperationCanceledException ? $"The model did not answer within {Math.Clamp(s.TimeoutSeconds, 5, 600)} seconds (the timeout in Administration > AI)." : "The model server could not be reached."), e.Message), 0);
        }
    }

    /// <summary>One call of a tool by the model: its name, and its arguments as the model wrote them (untrusted).</summary>
    public sealed record ToolCall(string Name, string Arguments);

    /// <summary>What a tool conversation did: the model calls, their tokens and time, the tool calls answered, and whether a limit ended it.</summary>
    public sealed record ToolRun(int ModelCalls, int PromptTokens, int CompletionTokens, long Milliseconds, int ToolCalls, bool LimitReached);

    /// <summary>The app's reply to one tool call, and whether the call reached the tool (counted against the calls a plugin answers) or was refused before.</summary>
    public sealed record ToolAnswer(string Reply, bool Reached);

    /// <summary>
    /// 0.11.0 (draft). A conversation in which the model may call the given tools (function tools of the chat API). The checks of
    /// <see cref="RunAsync"/> come first. Each tool call goes to <paramref name="answer"/>, whose reply is what the model gets back: server
    /// text, never a plugin's. <paramref name="tools"/> is asked before each model call, so a tool that is done is no longer offered. It
    /// ends when the model calls no tool, when no tool is left to offer, after <paramref name="maxToolCalls"/> calls that reached a tool, or
    /// after <paramref name="maxRefused"/> calls refused before that. A server or model that cannot call tools gives a problem at the first
    /// call; the caller goes on without tools.
    /// </summary>
    public async Task<Outcome<ToolRun>> RunToolsAsync(Actor actor, string feature, string task, IReadOnlyDictionary<string, string> data, Func<JsonArray> tools,
        Func<ToolCall, Task<ToolAnswer>> answer, int maxToolCalls, int maxRefused, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(answer);
        var s = await SettingsAsync(ct);
        async Task<Problem> RefuseAsync(string outcome, Problem p, string? detail = null)
        {
            await AuditAsync(actor, feature, outcome, 0, 0, 0, detail);
            return p;
        }

        if (!s.Enabled || string.IsNullOrWhiteSpace(s.BaseUrl) || string.IsNullOrWhiteSpace(s.Model)) return await RefuseAsync("off", Problem.Conflict("AI is off."));
        if (!await MayUseAsync(actor, feature, s, ct)) return await RefuseAsync("denied", new Problem(403, "This AI feature is not open to you."));
        if (data.Values.Sum(v => (long)v.Length) > s.MaxInputChars) return await RefuseAsync("too-large", Problem.Invalid("data", $"{s.MaxInputChars} characters at most."));
        if (AiGuard.Tripwire(data) is { } found)
            return await RefuseAsync("refused", Problem.Refused("The content holds text addressed to the AI, so it was not sent."), found.Length > 80 ? found[..80] : found);

        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = $"{s.Ruleset.Trim()}\n\nTask:\n{task.Trim()}" },
            new JsonObject { ["role"] = "user", ["content"] = AiGuard.UserMessage(data) });
        var (calls, inTokens, outTokens, ms, answered, reached, refused) = (0, 0, 0, 0L, 0, 0, 0);
        while (true)
        {
            var offered = tools();
            if (offered.Count == 0) return new ToolRun(calls, inTokens, outTokens, ms, answered, false);
            if (!limiter.Allow($"u:{actor.UserId}", s.UserPerMinute) || (actor.TokenId is { } token && !limiter.Allow($"t:{token}", s.TokenPerMinute)))
                return calls == 0 ? await RefuseAsync("limited", new Problem(429, "Too many AI calls. Wait a minute.")) : new ToolRun(calls, inTokens, outTokens, ms, answered, true);
            var (reply, failed, took) = await SendAsync(s, AiGuard.ToolRequest(s, messages, offered), feature, RefuseAsync, ct);
            if (failed is not null) return calls == 0 ? failed : new ToolRun(calls, inTokens, outTokens, ms, answered, false);
            calls++;
            ms += took;
            var (i, o) = (reply?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, reply?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0);
            (inTokens, outTokens) = (inTokens + i, outTokens + o);
            var toolCalls = (reply?["choices"]?[0]?["message"]?["tool_calls"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            await AuditAsync(actor, feature, "ok", i, o, took, $"tool calls: {toolCalls.Count}");
            if (toolCalls.Count == 0) return new ToolRun(calls, inTokens, outTokens, ms, answered, false);
            // The model's turn goes back rebuilt from each call's id, name and arguments; each call gets the server's reply.
            var made = new JsonArray();
            var replies = new List<JsonObject>();
            foreach (var c in toolCalls)
            {
                var id = c["id"]?.GetValue<string>() ?? $"call_{answered}";
                var name = c["function"]?["name"]?.GetValue<string>() ?? "";
                var args = c["function"]?["arguments"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : c["function"]?["arguments"]?.ToJsonString() ?? "";
                made.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } });
                var said = "Refused: the limit of tool calls for this import is reached.";
                if (reached < maxToolCalls && refused < maxRefused)
                {
                    var got = await answer(new ToolCall(name, args));
                    said = got.Reply;
                    if (got.Reached) reached++;
                    else refused++;
                }
                answered++;
                replies.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = said });
            }
            if (reached >= maxToolCalls || refused >= maxRefused) return new ToolRun(calls, inTokens, outTokens, ms, answered, true);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = made });
            foreach (var r in replies) messages.Add(r);
        }
    }

    /// <summary>
    /// A redirect is not followed: the request carries headers plugins added, which .NET would send on to the new host.
    /// The Admin sets the final address instead.
    /// </summary>
    private static string? Redirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400
            ? $"The model server answered a redirect ({(int)response.StatusCode}){(response.Headers.Location is { } to ? $" to {(to.IsAbsoluteUri ? to.GetLeftPart(UriPartial.Path) : to.OriginalString)}" : "")}. Redirects are not followed: set the final address in Administration > AI."
            : null;

    /// <summary>The message of an OpenAI-style error answer ({"error": {"message": ...}}), shortened; null when it has none.</summary>
    private static string? ServerReason(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } m ? (m.Length > 160 ? m[..160] : m).ReplaceLineEndings(" ") : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    // Some servers wrap JSON in a ``` fence even when asked not to.
    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var start = t.IndexOf('\n');
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return start > 0 && end > start ? t[(start + 1)..end].Trim() : t;
    }

    /// <summary>A refusal a feature decides before calling (the page it would send holds text addressed to the AI): audited as ai.refused.</summary>
    public Task RefusedAsync(Actor actor, string feature, string detail) => AuditAsync(actor, feature, "refused", 0, 0, 0, detail.Length > 300 ? detail[..300] : detail);

    /// <summary>
    /// Page content is input to the AI as much as anything pasted: a flagged page, or content with instruction-like text that is not
    /// what an Owner marked reviewed, refuses the call before it is made. Null when the content may go.
    /// </summary>
    public async Task<Problem?> PageRefusalAsync(Actor actor, string feature, Kb.Core.Content.Page? page, string contentJson)
    {
        var findings = PageScan.Scan(contentJson);
        if (findings.Count > 0 && (page?.AiReviewed is null || page.AiReviewed != PageScan.Hash(findings)))
        {
            var where = PageScan.Describe(findings);
            await RefusedAsync(actor, feature, $"page: {where}");
            return Problem.Refused($"The page holds text addressed to the AI ({where}), so it was not sent. Remove it, or an Owner marks the page reviewed.");
        }
        return null;
    }

    private async Task AuditAsync(Actor actor, string feature, string outcome, int inTokens, int outTokens, long ms, string? detail)
    {
        var data = new Dictionary<string, string>
        {
            ["feature"] = feature, ["outcome"] = outcome,
            ["promptTokens"] = inTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["completionTokens"] = outTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ms"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (detail is not null) data["detail"] = detail;
        await audit.AppendAsync(new AuditEvent(outcome == "refused" ? AuditActions.AiRefused : AuditActions.AiCall, actor.UserId, "ai", feature,
            Data: AuditData.WithToken(data, actor, web.HttpContext)), CancellationToken.None);
    }
}
