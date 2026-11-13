using System.Collections.Concurrent;
using Kb.Plugins;
using Kb.Plugins.Actions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kb.TestPlugin.Probe;

/// <summary>A write action of the probe's own: stores a text under a target.</summary>
[Action("probe.echo")]
public sealed record ProbeEcho : IAction<ProbeEchoResult>
{
    public required string Target { get; init; }
    public required string Text { get; init; }
}

public sealed record ProbeEchoResult(string Target, string Text);

/// <summary>A read action of the probe's own.</summary>
[Action("probe.peek", Read = true)]
public sealed record ProbePeek : IAction<ProbePeekResult>
{
    public required string Target { get; init; }
}

public sealed record ProbePeekResult(string Target, string? Text);

public sealed record EchoBody(string Target, string Text);

/// <summary>What the probe counted and stored. One per app, so a test reads what happened in the app it drove.</summary>
public sealed class ProbeState(IPluginContext context)
{
    private int _hooks, _handled, _filters, _after;

    public IPluginContext Context { get; } = context;
    public ConcurrentDictionary<string, string> Written { get; } = new();
    public string? LastActor { get; set; }
    public int Hooks => _hooks;
    public int Handled => _handled;
    public int Filters => _filters;
    public int After => _after;

    public void CountHook() => Interlocked.Increment(ref _hooks);
    public void CountHandled() => Interlocked.Increment(ref _handled);
    public void CountFilter() => Interlocked.Increment(ref _filters);
    public void CountAfter() => Interlocked.Increment(ref _after);
    public string? Setting(string key) => Context.GetSetting(key);
}

public sealed class EchoHandler(ProbeState state) : IActionHandler<ProbeEcho, ProbeEchoResult>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ProbeEcho> ctx)
    {
        ctx.Audit("probe.attempt", "probe", ctx.Action.Target, evenIfRefused: true);
        if (ctx.Actor.Kind == ActorKind.Anonymous) return Task.FromResult<Problem?>(Problem.Unauthorized);
        var denied = state.Setting("Deny") == "true" || ctx.Action.Target == "forbidden";
        return Task.FromResult(denied ? Problem.Forbidden : null);
    }

    public async Task<Outcome<ProbeEchoResult>> HandleAsync(ActionContext<ProbeEcho> ctx)
    {
        state.CountHandled();
        state.LastActor = $"{ctx.Actor.Kind}:{ctx.Actor.UserId}";
        var action = ctx.Action;
        if (state.Setting("Nested") == "true" && action.Target != "nested")
        {
            var bus = (IActionBus)ctx.Services.GetService(typeof(IActionBus))!;
            await bus.SendAsync(new ProbeEcho { Target = "nested", Text = "inner" }, ctx.Cancel);
        }
        ctx.Audit("probe.echo", "probe", action.Target);
        if (action.Text == "fail") return Problem.Conflict("Echo failed.");
        state.Written[action.Target] = action.Text;
        return new ProbeEchoResult(action.Target, action.Text);
    }
}

public sealed class PeekHandler(ProbeState state) : IActionHandler<ProbePeek, ProbePeekResult>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ProbePeek> ctx) =>
        Task.FromResult(ctx.Actor.Kind == ActorKind.Anonymous ? Problem.Unauthorized : null);

    public Task<Outcome<ProbePeekResult>> HandleAsync(ActionContext<ProbePeek> ctx) =>
        Task.FromResult<Outcome<ProbePeekResult>>(new ProbePeekResult(ctx.Action.Target, state.Written.GetValueOrDefault(ctx.Action.Target)));
}

/// <summary>Does to an echo what the settings say: refuse it, rewrite its text, point it at another target, or throw.</summary>
public sealed class EchoHook(ProbeState state) : IBeforeAction<ProbeEcho>
{
    public Task<Problem?> BeforeAsync(ActionContext<ProbeEcho> ctx)
    {
        state.CountHook();
        if (state.Setting("FailOn") == "before") throw new InvalidOperationException("Probe hook failed.");
        if (state.Setting("Refuse") == ctx.Name) return Task.FromResult<Problem?>(Problem.Refused("Probe refused."));
        if (state.Setting("Rewrite") is { } rewrite && rewrite.StartsWith(ctx.Name + ":Text=", StringComparison.Ordinal))
            ctx.Action = ctx.Action with { Text = rewrite[(ctx.Name.Length + 6)..] };
        if (state.Setting("SwapTarget") == "true") ctx.Action = ctx.Action with { Target = "forbidden" };
        return Task.FromResult<Problem?>(null);
    }
}

public sealed class PeekFilter(ProbeState state) : IResultFilter<ProbePeek, ProbePeekResult>
{
    public Task<ProbePeekResult> FilterAsync(ActionContext<ProbePeek> ctx, ProbePeekResult result)
    {
        state.CountFilter();
        if (state.Setting("FailOn") == "filter") throw new InvalidOperationException("Probe filter failed.");
        return Task.FromResult(result with { Text = result.Text?.ToUpperInvariant() });
    }
}

public static class ProbeRoutes
{
    /// <summary>Mapped under /api/v1/plugins/&lt;id&gt;/.</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/echo", (EchoBody body, IActionBus bus, CancellationToken ct) => bus.SendAsync(new ProbeEcho { Target = body.Target, Text = body.Text }, ct).ToHttp());
        group.MapGet("/peek/{target}", (string target, IActionBus bus, CancellationToken ct) => bus.SendAsync(new ProbePeek { Target = target }, ct).ToHttp());
        group.MapGet("/counters", (ProbeState state) => Results.Ok(new { hooks = state.Hooks, handled = state.Handled, filters = state.Filters, after = state.After, lastActor = state.LastActor, written = state.Written }));
        group.MapGet("/hello", () => Results.Ok(new { hello = "anonymous" })).AllowAnonymous();
    }
}
