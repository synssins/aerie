using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.Web.Pipeline;

/// <summary>Who the current request, or the current piece of background work, acts as.</summary>
public sealed class ActorAccessor(IHttpContextAccessor http) : IActorAccessor
{
    private Actor? _actor;

    public Actor Current => _actor ??= FromRequest();

    /// <summary>Background work sets its actor: a plugin's after handler or job.</summary>
    internal void Use(Actor actor) => _actor = actor;

    private Actor FromRequest()
    {
        var context = http.HttpContext;
        var ip = context?.Connection.RemoteIpAddress?.ToString();
        return Guid.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user)
            ? Actor.User(user, ip, ApiTokenAuth.Current(context)?.Id)
            : Actor.Anonymous(ip);
    }
}

/// <summary>
/// Every action's one handler, and the hooks, filters and after handlers plugins registered for it, in plugin load order.
/// The core's handlers are the <see cref="IActionHandler{TAction, TResult}"/> classes in this assembly; a plugin's come through <c>Handle</c>.
/// </summary>
public sealed class ActionCatalog
{
    private readonly ILookup<Type, (string Owner, Type Handler)> _handlers;
    private readonly ILookup<Type, Registered<BeforeHook>> _before;
    private readonly ILookup<Type, Registered<ResultFilter>> _filters;
    private readonly ILookup<Type, Registered<AfterHandler>> _after;

    public ActionCatalog(IRegistrations registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var core = from type in typeof(ActionCatalog).Assembly.GetTypes()
                   where type is { IsClass: true, IsAbstract: false }
                   from i in type.GetInterfaces()
                   where i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IActionHandler<,>)
                   select (Action: i.GetGenericArguments()[0], Owner: "core", Handler: type);
        var plugins = registrations.All<ActionHandler>().Select(r => (r.Value.Action, Owner: r.PluginId, r.Value.Handler));
        _handlers = core.Concat(plugins).ToLookup(h => h.Action, h => (h.Owner, h.Handler));
        _before = registrations.All<BeforeHook>().ToLookup(r => r.Value.Action);
        _filters = registrations.All<ResultFilter>().ToLookup(r => r.Value.Action);
        _after = registrations.All<AfterHandler>().ToLookup(r => r.Value.Action);
    }

    /// <summary>Every action that has a handler, with how many it has. More than one is a mistake a test catches.</summary>
    public IEnumerable<(Type Action, int Handlers)> Actions => _handlers.Select(g => (g.Key, g.Count()));

    public static ActionAttribute Describe(Type action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.GetCustomAttribute<ActionAttribute>() ?? throw new InvalidOperationException($"{action.FullName} has no [Action] name.");
    }

    public (string Owner, Type Handler) HandlerOf(Type action)
    {
        var handlers = _handlers[action].ToList();
        return handlers.Count == 1 ? handlers[0] : throw new InvalidOperationException($"{Describe(action).Name} has {handlers.Count} handlers; it needs exactly one.");
    }

    public IEnumerable<Registered<BeforeHook>> Before(Type action) => _before[action];
    public IEnumerable<Registered<ResultFilter>> Filters(Type action) => _filters[action];
    public IEnumerable<Registered<AfterHandler>> After(Type action) => _after[action];
}

/// <summary>
/// Runs an action through the steps, in this order. The order is the security model: nothing may change it.
/// 1 find the one handler; 2 the core's permission check, before any plugin code; 3 the lock of a disabled enforcing plugin;
/// 4 before hooks, in load order; 5 a changed payload is authorized again; 6 the handler; 7 result filters, on a read;
/// 8 the audit entries; 9 after handlers. There is no transaction around an action: the handler saves as it goes,
/// and an action sent from inside another runs on its own.
/// </summary>
public sealed class ActionBus(ActionCatalog catalog, IServiceProvider services, IActorAccessor actors, IAuditLog audit, IPluginFailures failures, PluginRegistry registry,
    ActionQueueWorker queue, TimeProvider time) : IActionBus
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> Runners = new();
    private static readonly MethodInfo Runner = typeof(ActionBus).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Task<Outcome<TResult>> SendAsync<TResult>(IAction<TResult> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var run = Runners.GetOrAdd(action.GetType(), type => Runner.MakeGenericMethod(type, typeof(TResult)));
        return (Task<Outcome<TResult>>)run.Invoke(this, [action, ct])!;
    }

    private async Task<Outcome<TResult>> RunAsync<TAction, TResult>(TAction action, CancellationToken ct) where TAction : IAction<TResult>
    {
        var described = ActionCatalog.Describe(typeof(TAction));
        var ctx = new ActionContext<TAction>(described.Name, actors.Current, services, action, ct);
        var outcome = await StepsAsync<TAction, TResult>(ctx, described.Read);
        // Step 8. A handler or a plugin cannot leave these out; when the action failed, only the entries marked evenIfRefused are written.
        foreach (var note in ctx.AuditNotes(outcome.Ok))
            await audit.AppendAsync(new AuditEvent(note.Action, ctx.Actor.UserId, note.TargetType, note.TargetId, note.SpaceId, note.Data), ct);
        if (!outcome.Ok) return outcome;

        // Step 9. After handlers get the action as it was done and its result. They run later, from the queue, as their plugin.
        var (done, result) = (ctx.Action, outcome.Value!);
        foreach (var after in catalog.After(typeof(TAction)))
        {
            var e = new ActionEvent(queue.NextId(), ctx.Name, ctx.Actor.Kind, ctx.Actor.UserId, ctx.Actor.PluginId, time.GetUtcNow());
            var handler = after.Value.Handler;
            queue.Enqueue(after.PluginId, $"{ctx.Name} after handler",
                (scope, stop) => ((IAfterAction<TAction, TResult>)ActivatorUtilities.CreateInstance(scope, handler)).AfterAsync(done, result, e, stop));
        }
        return outcome;
    }

    private async Task<Outcome<TResult>> StepsAsync<TAction, TResult>(ActionContext<TAction> ctx, bool read) where TAction : IAction<TResult>
    {
        var handlerType = catalog.HandlerOf(typeof(TAction)).Handler;
        IActionHandler<TAction, TResult> NewHandler() => (IActionHandler<TAction, TResult>)ActivatorUtilities.CreateInstance(services, handlerType);

        var handler = NewHandler();
        if (await handler.AuthorizeAsync(ctx) is { } denied) return denied;

        var given = ctx.Action;
        foreach (var hook in catalog.Before(typeof(TAction)))
        {
            try
            {
                var before = (IBeforeAction<TAction>)ActivatorUtilities.CreateInstance(services, hook.Value.Hook);
                if (await before.BeforeAsync(ctx) is { } refused) return refused;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ctx.Cancel.IsCancellationRequested)) // a timeout inside a hook is the hook failing
            {
                if (Failed(hook.PluginId, $"{ctx.Name} hook", e, hook.Value.Optional) is { } failed) return failed;
            }
        }
        if (!Equals(given, ctx.Action))
        {
            // A hook replaced the payload: it may now point at something its caller cannot use.
            handler = NewHandler();
            if (await handler.AuthorizeAsync(ctx) is { } deniedAgain) return deniedAgain;
        }

        var result = await handler.HandleAsync(ctx);
        if (!result.Ok || !read) return result;

        var value = result.Value!;
        foreach (var filter in catalog.Filters(typeof(TAction)))
        {
            try { value = await ((IResultFilter<TAction, TResult>)ActivatorUtilities.CreateInstance(services, filter.Value.Filter)).FilterAsync(ctx, value); }
            catch (Exception e) when (!(e is OperationCanceledException && ctx.Cancel.IsCancellationRequested))
            {
                if (Failed(filter.PluginId, $"{ctx.Name} filter", e, filter.Value.Optional) is { } failed) return failed;
            }
        }
        return value;
    }

    /// <summary>A hook or filter that throws is reported. It refuses the action with 422, unless it was registered as optional: then it is skipped.</summary>
    private Problem? Failed(string pluginId, string where, Exception e, bool optional)
    {
        failures.Report(pluginId, where, e);
        return optional ? null : Problem.Refused($"{(registry.Find(pluginId) is { } plugin ? PluginManifests.NameOf(plugin.Manifest) : pluginId)} failed.");
    }
}
