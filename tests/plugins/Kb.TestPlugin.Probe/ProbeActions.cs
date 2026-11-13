using System.Collections.Concurrent;
using System.Reflection;
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
    private int _hooks, _handled, _filters, _after, _afterReads;

    public IPluginContext Context { get; } = context;
    public ConcurrentDictionary<string, string> Written { get; } = new();
    public string? LastActor { get; set; }
    public int Hooks => _hooks;
    public int Handled => _handled;
    public int Filters => _filters;
    public int After => _after;
    public int AfterReads => _afterReads;
    public string? LastAfter { get; set; }
    /// <summary>How often the hooks on core actions ran, by "before:page.save", "filter:page.read", "after:page.create".</summary>
    public ConcurrentDictionary<string, int> ByAction { get; } = new();
    /// <summary>The last core action an after handler saw, with the id of its result: "page.create|&lt;id&gt;".</summary>
    public string? LastAfterAny { get; set; }

    public void Count(string kind, string action) => ByAction.AddOrUpdate($"{kind}:{action}", 1, (_, n) => n + 1);

    public void CountHook() => Interlocked.Increment(ref _hooks);
    public void CountHandled() => Interlocked.Increment(ref _handled);
    public void CountFilter() => Interlocked.Increment(ref _filters);
    public void CountAfter() => Interlocked.Increment(ref _after);
    public void CountAfterRead() => Interlocked.Increment(ref _afterReads);
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
        if (state.Setting("FailOn") == "before") throw state.Setting("FailWith") == "timeout" ? new TaskCanceledException("Probe hook timed out.") : new InvalidOperationException("Probe hook failed.");
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

/// <summary>Runs after an echo succeeded: counts it, notes who it ran as, and can wait, write a line to a file, or throw.</summary>
public sealed class EchoAfter(ProbeState state, IActorAccessor actors) : IAfterAction<ProbeEcho, ProbeEchoResult>
{
    public async Task AfterAsync(ProbeEcho action, ProbeEchoResult result, ActionEvent e, CancellationToken ct)
    {
        if (int.TryParse(state.Setting("AfterDelay"), out var delay)) await Task.Delay(delay, ct);
        if (state.Setting("FailOn") == "after") throw new InvalidOperationException("Probe after handler failed.");
        if (state.Setting("AfterFile") is { Length: > 0 } file) await File.AppendAllLinesAsync(file, [result.Text], ct);
        var me = actors.Current;
        state.LastAfter = $"{e.Action}|{action.Target}|{result.Text}|{e.ActorKind}|{me.Kind}:{me.PluginId}:{me.Source}";
        state.CountAfter();
    }
}

public sealed class PeekAfter(ProbeState state) : IAfterAction<ProbePeek, ProbePeekResult>
{
    public Task AfterAsync(ProbePeek action, ProbePeekResult result, ActionEvent e, CancellationToken ct)
    {
        state.CountAfterRead();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hooks on every core action, so a test can refuse, rewrite, shape or watch any of them by name without changing this plugin:
/// Refuse = "&lt;action&gt;"; Rewrite = "&lt;action&gt;:&lt;Property&gt;=&lt;text&gt;"; Blank = "&lt;action&gt;:&lt;Property&gt;" empties a text of a read's result;
/// FailFilter = "&lt;action&gt;" makes the filter throw.
/// </summary>
public static class AnyAction
{
    public static void Register(IPluginApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        foreach (var action in typeof(IAction<>).Assembly.GetTypes())
        {
            if (action.GetCustomAttribute<ActionAttribute>() is not { } described) continue;
            var result = action.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAction<>)).GetGenericArguments()[0];
            app.Add(new BeforeHook(action, typeof(AnyHook<>).MakeGenericType(action), false));
            if (described.Read) app.Add(new ResultFilter(action, result, typeof(AnyFilter<,>).MakeGenericType(action, result), false));
            app.Add(new AfterHandler(action, result, typeof(AnyAfter<,>).MakeGenericType(action, result)));
        }
    }

    /// <summary>A copy of a record with one text property set.</summary>
    internal static T With<T>(T source, string property, string value)
    {
        var copy = (T)typeof(T).GetMethod("<Clone>$")!.Invoke(source, null)!;
        typeof(T).GetProperty(property)!.SetValue(copy, value);
        return copy;
    }
}

public sealed class AnyHook<TAction>(ProbeState state) : IBeforeAction<TAction>
{
    public Task<Problem?> BeforeAsync(ActionContext<TAction> ctx)
    {
        state.Count("before", ctx.Name);
        if (state.Setting("Refuse") == ctx.Name) return Task.FromResult<Problem?>(Problem.Refused("Probe refused."));
        if (state.Setting("Rewrite") is { } rewrite && rewrite.StartsWith(ctx.Name + ":", StringComparison.Ordinal) && rewrite.Split('=', 2) is [var target, var value])
            ctx.Action = AnyAction.With(ctx.Action, target[(ctx.Name.Length + 1)..], value);
        return Task.FromResult<Problem?>(null);
    }
}

public sealed class AnyFilter<TAction, TResult>(ProbeState state) : IResultFilter<TAction, TResult>
{
    public Task<TResult> FilterAsync(ActionContext<TAction> ctx, TResult result)
    {
        state.Count("filter", ctx.Name);
        if (state.Setting("FailFilter") == ctx.Name) throw new InvalidOperationException("Probe filter failed.");
        return Task.FromResult(state.Setting("Blank") is { } blank && blank.StartsWith(ctx.Name + ":", StringComparison.Ordinal)
            ? AnyAction.With(result, blank[(ctx.Name.Length + 1)..], "")
            : result);
    }
}

public sealed class AnyAfter<TAction, TResult>(ProbeState state) : IAfterAction<TAction, TResult>
{
    public Task AfterAsync(TAction action, TResult result, ActionEvent e, CancellationToken ct)
    {
        state.LastAfterAny = $"{e.Action}|{result?.GetType().GetProperty("Id")?.GetValue(result)}";
        state.Count("after", e.Action);
        return Task.CompletedTask;
    }
}

public static class ProbeRoutes
{
    /// <summary>Mapped under /api/v1/plugins/&lt;id&gt;/.</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/echo", (EchoBody body, IActionBus bus, CancellationToken ct) => bus.SendAsync(new ProbeEcho { Target = body.Target, Text = body.Text }, ct).ToHttp());
        group.MapGet("/peek/{target}", (string target, IActionBus bus, CancellationToken ct) => bus.SendAsync(new ProbePeek { Target = target }, ct).ToHttp());
        group.MapGet("/counters", (ProbeState state) => Results.Ok(new { hooks = state.Hooks, handled = state.Handled, filters = state.Filters, after = state.After, afterReads = state.AfterReads, lastAfter = state.LastAfter, lastActor = state.LastActor, written = state.Written, byAction = state.ByAction, lastAfterAny = state.LastAfterAny }));
        // A route that names one action and sends another: the bus must refuse it.
        group.MapGet("/mismatch", (IActionBus bus, CancellationToken ct) => bus.SendAsync(new ProbeEcho { Target = "mismatch", Text = "x" }, ct).ToHttp()).WithAction<ProbePeek>();
        group.MapGet("/hello", () => Results.Ok(new { hello = "anonymous" })).AllowAnonymous();
    }
}
