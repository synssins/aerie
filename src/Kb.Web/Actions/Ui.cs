using Kb.Core.Plugins;
using Kb.Web.Pipeline;

namespace Kb.Web.Actions;

// What the browser needs to load plugin scripts. Core only: these are the app's own plumbing, not something a plugin hooks.

public sealed record UiPluginDto(string Id, string Version, string Script);
public sealed record UiPluginsDto(string UiApiVersion, IReadOnlyList<UiPluginDto> Plugins);

/// <summary>The loaded plugins that have a script for the browser, and where each script is.</summary>
[Action("ui.plugins", Read = true)]
public sealed record ListUiPlugins : IAction<UiPluginsDto>;

/// <summary>A plugin's script failed in someone's browser.</summary>
[Action("ui.error.report")]
public sealed record ReportUiError : IAction<Unit>
{
    public required string Plugin { get; init; }
    public string? Where { get; init; }
    public string? Message { get; init; }
}

internal sealed class ListUiPluginsHandler(PluginRegistry registry) : IActionHandler<ListUiPlugins, UiPluginsDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListUiPlugins> ctx) =>
        Task.FromResult(ctx.Actor is { Kind: ActorKind.User } ? null : Problem.Unauthorized);

    public Task<Outcome<UiPluginsDto>> HandleAsync(ActionContext<ListUiPlugins> ctx) =>
        Task.FromResult<Outcome<UiPluginsDto>>(new UiPluginsDto(KbHost.UiApiVersion, [.. registry.Loaded
            .Select(p => (Plugin: p, Script: PluginStatic.ScriptOf(registry, p))).Where(p => p.Script is not null)
            .Select(p => new UiPluginDto(p.Plugin.Manifest.Id, p.Plugin.Manifest.Version, p.Script!))]));
}

/// <summary>
/// The error is written to the server's log, with who reported it. It is never counted as a failure of the plugin:
/// any signed-in person can send one, so counting them would let a user switch a plugin off.
/// </summary>
internal sealed class ReportUiErrorHandler(PluginRegistry registry, UiErrorLimit limit, ILogger<ReportUiErrorHandler> log) : IActionHandler<ReportUiError, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReportUiError> ctx) =>
        Task.FromResult(ctx.Actor is { Kind: ActorKind.User, UserId: not null } ? null : Problem.Unauthorized);

    public Task<Outcome<Unit>> HandleAsync(ActionContext<ReportUiError> ctx)
    {
        if (registry.Find(ctx.Action.Plugin) is not { Loaded: true }) return Task.FromResult<Outcome<Unit>>(Problem.Invalid("plugin", "Not a loaded plugin."));
        if (!limit.Allow(ctx.Actor.UserId!.Value)) return Task.FromResult<Outcome<Unit>>(new Problem(429));
        log.LogWarning("Plugin script error. Plugin {Plugin}, at {Where}, reported by {User}: {Message}", ctx.Action.Plugin, Line(ctx.Action.Where, 100), ctx.Actor.UserId, Line(ctx.Action.Message, 500));
        return Task.FromResult<Outcome<Unit>>(new Unit());
    }

    /// <summary>One line of bounded length: what a browser sends must not be able to forge or flood log lines.</summary>
    private static string Line(string? text, int max)
    {
        var clean = new string([.. (text ?? "").Select(ch => char.IsControl(ch) ? ' ' : ch)]).Trim();
        return clean[..Math.Min(clean.Length, max)];
    }
}
