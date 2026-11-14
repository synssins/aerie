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
public sealed record AiResult(JsonNode Output, string Model, int PromptTokens, int CompletionTokens, long Milliseconds, bool ReasoningEmpty);

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
    IDataProtectionProvider protection, AiRateLimiter limiter, IEnumerable<IOutboundRequestDecorator> decorators, TimeProvider time)
{
    public const string SettingsKey = "Ai.Settings", KeyKey = "Ai.ApiKey", ClientName = "kb.ai", TestFeature = "test";
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
        return s;
    }

    public async Task SaveAsync(AiSettings s, Guid? by, CancellationToken ct) => await db.SetAsync(SettingsKey, JsonSerializer.Serialize(s, Json), by, time.GetUtcNow(), ct);

    public async Task<bool> KeySetAsync(CancellationToken ct) => !string.IsNullOrEmpty(await db.GetAsync(KeyKey, ct));

    /// <summary>Null clears it. The key is kept protected (data protection) and only ever read here, to call the model.</summary>
    public async Task SetKeyAsync(string? key, Guid? by, CancellationToken ct) =>
        await db.SetAsync(KeyKey, string.IsNullOrEmpty(key) ? null : _key.Protect(key), by, time.GetUtcNow(), ct);

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
    public async Task<Outcome<AiResult>> RunAsync(Actor actor, string feature, string task, IReadOnlyDictionary<string, string> data, JsonObject schema, CancellationToken ct,
        IReadOnlyList<AiGuard.Picture>? pictures = null)
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
        var clock = Stopwatch.StartNew();
        JsonNode? answer;
        try
        {
            var root = s.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1") + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (await KeyAsync(ct) is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            foreach (var d in decorators) await d.DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, request), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 5, 600)));
            using var response = await http.CreateClient(ClientName).SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return await RefuseAsync("error", new Problem(502, $"The model server answered {(int)response.StatusCode}."), text.Length > 200 ? text[..200] : text);
            answer = JsonNode.Parse(text);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return await RefuseAsync("error", new Problem(502, e is OperationCanceledException ? "The model server did not answer in time." : "The model server could not be reached."), e.Message);
        }
        var ms = clock.ElapsedMilliseconds;
        var message = answer?["choices"]?[0]?["message"];
        var content = message?["content"]?.GetValue<string>() ?? "";
        var reasoning = message?["reasoning_content"]?.GetValue<string>() ?? message?["reasoning"]?.GetValue<string>() ?? "";
        var (inTokens, outTokens) = (answer?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, answer?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0);
        var model = answer?["model"]?.GetValue<string>() ?? s.Model;

        JsonNode? output;
        try { output = JsonNode.Parse(StripFence(content)); }
        catch (JsonException) { output = null; }
        if (output is null || AiGuard.SchemaError(output, schema) is { } bad)
        {
            await AuditAsync(actor, feature, "invalid-output", inTokens, outTokens, ms, output is null ? "not JSON" : AiGuard.SchemaError(output, schema));
            return new Problem(502, "The model's answer did not match what was asked for.");
        }
        await AuditAsync(actor, feature, "ok", inTokens, outTokens, ms, null);
        return new AiResult(output, model, inTokens, outTokens, ms, string.IsNullOrWhiteSpace(reasoning));
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
