using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;

namespace Kb.Plugins.Actions;

public enum ActorKind { Anonymous, User, Plugin }

/// <summary>Who an action is done by. Made only by the host: a plugin cannot make one, so it cannot act as someone else.</summary>
public sealed record Actor
{
    internal Actor(ActorKind kind, Guid? userId, string? pluginId, string source, string? remoteIp, Guid? tokenId)
    {
        (Kind, UserId, PluginId, Source, RemoteIp, TokenId) = (kind, userId, pluginId, source, remoteIp, tokenId);
    }

    public ActorKind Kind { get; }
    public Guid? UserId { get; }
    /// <summary>Background work of that plugin; "core" for the core's own.</summary>
    public string? PluginId { get; }
    /// <summary>web, job or queue.</summary>
    public string Source { get; }
    public string? RemoteIp { get; }
    /// <summary>Set when the request was made with an API token. Such an actor is never an Admin and is narrowed by the token's scopes.</summary>
    public Guid? TokenId { get; }

    internal static Actor Anonymous(string? remoteIp = null) => new(ActorKind.Anonymous, null, null, "web", remoteIp, null);
    internal static Actor User(Guid userId, string? remoteIp = null, Guid? tokenId = null) => new(ActorKind.User, userId, null, "web", remoteIp, tokenId);
    internal static Actor Plugin(string pluginId, string source) => new(ActorKind.Plugin, null, pluginId, source, null, null);
}

public interface IActorAccessor
{
    Actor Current { get; }
}

/// <summary>Marks an action and names its result. Every core operation is one action.</summary>
public interface IAction<TResult> { }

/// <summary>The action's name, as in the audit log where one exists ("page.move"). Read: it changes nothing, and result filters run on it.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ActionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public bool Read { get; init; }
}

public abstract class ActionContext
{
    private readonly List<(AuditNote Note, bool EvenIfRefused)> _audit = [];

    private protected ActionContext(string name, Actor actor, IServiceProvider services, CancellationToken cancel)
    {
        (Name, Actor, Services, Cancel) = (name, actor, services, cancel);
    }

    public string Name { get; }
    public Actor Actor { get; }
    public IServiceProvider Services { get; }
    public CancellationToken Cancel { get; }

    /// <summary>
    /// Adds an audit entry. The bus writes it once the action has succeeded; an entry cannot be removed or changed.
    /// <paramref name="evenIfRefused"/> keeps it when the action fails (a failed sign-in).
    /// </summary>
    public void Audit(string action, string? targetType = null, string? targetId = null, Guid? spaceId = null,
        IReadOnlyDictionary<string, string>? data = null, bool evenIfRefused = false) =>
        _audit.Add((new AuditNote(action, targetType, targetId, spaceId, data), evenIfRefused));

    internal IEnumerable<AuditNote> AuditNotes(bool succeeded) => _audit.Where(a => succeeded || a.EvenIfRefused).Select(a => a.Note);
}

/// <summary>An audit entry an action asked for. The actor is added by the bus.</summary>
public sealed record AuditNote(string Action, string? TargetType, string? TargetId, Guid? SpaceId, IReadOnlyDictionary<string, string>? Data);

public sealed class ActionContext<TAction> : ActionContext
{
    internal ActionContext(string name, Actor actor, IServiceProvider services, TAction action, CancellationToken cancel) : base(name, actor, services, cancel) => Action = action;

    /// <summary>The payload. A before hook may replace it; the action is then authorized again.</summary>
    public TAction Action { get; set; }
}

/// <summary>Does one action. Created fresh for each attempt, so it may keep what <see cref="AuthorizeAsync"/> loaded for <see cref="HandleAsync"/>.</summary>
public interface IActionHandler<TAction, TResult> where TAction : IAction<TResult>
{
    /// <summary>The core's permission check. A problem ends the action before any plugin code has run.</summary>
    Task<Problem?> AuthorizeAsync(ActionContext<TAction> ctx);
    Task<Outcome<TResult>> HandleAsync(ActionContext<TAction> ctx);
}

/// <summary>Runs before an action. Null continues; a problem refuses it; setting <c>ctx.Action</c> changes the payload.</summary>
public interface IBeforeAction<TAction>
{
    Task<Problem?> BeforeAsync(ActionContext<TAction> ctx);
}

/// <summary>Shapes the result of a read.</summary>
public interface IResultFilter<TAction, TResult>
{
    Task<TResult> FilterAsync(ActionContext<TAction> ctx, TResult result);
}

public sealed record ActionEvent(long Id, string Action, ActorKind ActorKind, Guid? UserId, string? PluginId, DateTimeOffset At);

/// <summary>Runs after an action succeeded, as the plugin that owns it. It must tolerate being run again, and must not read an upload: the request is over.</summary>
public interface IAfterAction<TAction, TResult>
{
    Task AfterAsync(TAction action, TResult result, ActionEvent e, CancellationToken ct);
}

public interface IActionBus
{
    Task<Outcome<TResult>> SendAsync<TResult>(IAction<TResult> action, CancellationToken ct = default);
}

/// <summary>A file inside an action. Mark the member that holds it <c>[JsonIgnore]</c>, so its content is never written to a log.</summary>
public abstract class UploadContent
{
    public abstract string? FileName { get; }
    public abstract long? Length { get; }
    /// <summary>Every call starts at the first byte.</summary>
    public abstract Task<Stream> OpenReadAsync(CancellationToken ct);
}

/// <summary>A file as an action's result.</summary>
public sealed record FileOut
{
    public required string ContentType { get; init; }
    public string? FileName { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    [JsonIgnore] public required Func<CancellationToken, Task<Stream>> Open { get; init; }
}

public sealed record BeforeHook(Type Action, Type Hook, bool Optional);
public sealed record ResultFilter(Type Action, Type Result, Type Filter, bool Optional);
public sealed record AfterHandler(Type Action, Type Result, Type Handler);
public sealed record ActionHandler(Type Action, Type Result, Type Handler);
/// <summary>A plugin's own routes, mapped under <c>/api/v1/plugins/&lt;id&gt;/</c>. They need sign-in unless a route says AllowAnonymous.</summary>
public sealed record RouteMap(Action<RouteGroupBuilder> Map);

public static class PluginAppExtensions
{
    public static IPluginApp Before<TAction, THook>(this IPluginApp app, bool optional = false) where THook : class, IBeforeAction<TAction>
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new BeforeHook(typeof(TAction), typeof(THook), optional));
    }

    public static IPluginApp Filter<TAction, TResult, TFilter>(this IPluginApp app, bool optional = false)
        where TAction : IAction<TResult> where TFilter : class, IResultFilter<TAction, TResult>
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new ResultFilter(typeof(TAction), typeof(TResult), typeof(TFilter), optional));
    }

    public static IPluginApp After<TAction, TResult, THandler>(this IPluginApp app)
        where TAction : IAction<TResult> where THandler : class, IAfterAction<TAction, TResult>
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new AfterHandler(typeof(TAction), typeof(TResult), typeof(THandler)));
    }

    public static IPluginApp Handle<TAction, TResult, THandler>(this IPluginApp app)
        where TAction : IAction<TResult> where THandler : class, IActionHandler<TAction, TResult>
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new ActionHandler(typeof(TAction), typeof(TResult), typeof(THandler)));
    }

    public static IPluginApp Routes(this IPluginApp app, Action<RouteGroupBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new RouteMap(map));
    }
}
