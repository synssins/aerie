using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kb.Core.Ai;
using Kb.Core.Audit;

namespace Kb.Web.Actions;

// Administration › AI. Core only: the settings name the model server and carry its key.

/// <summary>The settings as an Admin reads them. ApiKeySet: a key is stored (it is never returned). DefaultRuleset: the prompt Restore puts back.</summary>
public sealed record AiSettingsDto(AiSettings Settings, bool ApiKeySet, bool RulesetIsDefault, string DefaultRuleset = AiGuard.DefaultRuleset);

/// <summary>ApiKey: null keeps the stored key, "" clears it, anything else replaces it.</summary>
public sealed record AiSettingsRequest(AiSettings? Settings, string? ApiKey);

/// <summary>Model: what the server says answered. ReasoningEmpty: no reasoning text came back.</summary>
public sealed record AiTestDto(bool Ok, string? Model, long Milliseconds, bool ReasoningEmpty, string? Error);

[Action("ai.settings.read", Read = true)]
public sealed record ReadAiSettings : IAction<AiSettingsDto>;

[Action("ai.settings.save")]
public sealed record SaveAiSettings : IAction<AiSettingsDto>
{
    public required AiSettingsRequest Request { get; init; }
    public override string ToString() => "SaveAiSettings";
}

/// <summary>Sends a fixed probe through the AI service and answers what came back.</summary>
[Action("ai.test")]
public sealed record TestAi : IAction<AiTestDto>;

internal sealed class ReadAiSettingsHandler(AiService ai, IAccess access) : IActionHandler<ReadAiSettings, AiSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadAiSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<AiSettingsDto>> HandleAsync(ActionContext<ReadAiSettings> ctx) =>
        await AiDto.OfAsync(ai, await ai.SettingsAsync(ctx.Cancel), ctx.Cancel);
}

internal sealed class SaveAiSettingsHandler(AiService ai, IAccess access) : IActionHandler<SaveAiSettings, AiSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveAiSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<AiSettingsDto>> HandleAsync(ActionContext<SaveAiSettings> ctx)
    {
        var req = ctx.Action.Request;
        if (req.Settings is not { } s) return Problem.Invalid("settings", "Required.");
        var url = s.BaseUrl.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))) return Problem.Invalid("baseUrl", "An http or https address.");
        if (s.Enabled && (url.Length == 0 || string.IsNullOrWhiteSpace(s.Model))) return Problem.Invalid("model", "An address and a model are needed to turn AI on.");
        if (s.Model.Length > 200) return Problem.Invalid("model", "200 characters at most.");
        if (s.TimeoutSeconds is < 5 or > 600) return Problem.Invalid("timeoutSeconds", "5-600.");
        if (s.MaxTokens is < 16 or > 200_000) return Problem.Invalid("maxTokens", "16-200000.");
        if (s.Temperature is < 0 or > 2) return Problem.Invalid("temperature", "0-2.");
        if (s.MaxInputChars is < 1000 or > 5_000_000) return Problem.Invalid("maxInputChars", "1000-5000000.");
        if (s.UserPerMinute is < 1 or > 1000 || s.TokenPerMinute is < 1 or > 1000) return Problem.Invalid("perMinute", "1-1000.");
        if (string.IsNullOrWhiteSpace(s.Ruleset) || s.Ruleset.Length > 20_000) return Problem.Invalid("ruleset", "1-20000 characters.");
        if (s.Features.Keys.Any(k => !AiFeatures.All.Contains(k)) || s.Features.Values.Any(v => !Enum.IsDefined(v))) return Problem.Invalid("features", "Unknown feature or access.");
        if ((req.ApiKey?.Length ?? 0) > 2000) return Problem.Invalid("apiKey", "2000 characters at most.");

        var before = await ai.SettingsAsync(ctx.Cancel);
        var clean = s with { BaseUrl = url, Model = s.Model.Trim(), Ruleset = s.Ruleset.Trim() };
        await ai.SaveAsync(clean, ctx.Actor.UserId, ctx.Cancel);
        if (req.ApiKey is not null) await ai.SetKeyAsync(req.ApiKey.Trim(), ctx.Actor.UserId, ctx.Cancel);
        // The audit entry names what changed; the ruleset by its fingerprint (the text itself is on the settings), the key never.
        ctx.Audit(AuditActions.AiSettingsChange, "setting", AiService.SettingsKey, data: new Dictionary<string, string>
        {
            ["enabled"] = clean.Enabled.ToString(), ["baseUrl"] = clean.BaseUrl, ["model"] = clean.Model, ["reasoning"] = clean.Reasoning.ToString(),
            ["rulesetChanged"] = (before.Ruleset != clean.Ruleset).ToString(), ["rulesetIsDefault"] = AiGuard.IsDefault(clean.Ruleset).ToString(), ["rulesetSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean.Ruleset)))[..16],
            ["apiKey"] = req.ApiKey is null ? "kept" : req.ApiKey.Length == 0 ? "cleared" : "replaced",
            ["features"] = string.Join(",", clean.Features.Select(f => $"{f.Key}:{f.Value}")),
        });
        return await AiDto.OfAsync(ai, clean, ctx.Cancel);
    }
}

internal static class AiDto
{
    internal static async Task<AiSettingsDto> OfAsync(AiService ai, AiSettings s, CancellationToken ct) =>
        new(s, await ai.KeySetAsync(ct), AiGuard.IsDefault(s.Ruleset));
}

internal sealed class TestAiHandler(AiService ai, IAccess access) : IActionHandler<TestAi, AiTestDto>
{
    private static readonly JsonObject Schema = JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""")!.AsObject();

    public async Task<Problem?> AuthorizeAsync(ActionContext<TestAi> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<AiTestDto>> HandleAsync(ActionContext<TestAi> ctx)
    {
        var r = await ai.RunAsync(ctx.Actor, AiService.TestFeature, "Set ok to true.", new Dictionary<string, string> { ["probe"] = "connection test" }, Schema, ctx.Cancel);
        return r.Problem is { } p
            ? new AiTestDto(false, null, 0, false, p.Message ?? $"Failed ({p.Status}).")
            : new AiTestDto(r.Value!.Output["ok"]?.GetValue<bool>() == true, r.Value.Model, r.Value.Milliseconds, r.Value.ReasoningEmpty, null);
    }
}
