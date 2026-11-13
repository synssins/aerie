using Kb.Plugins;
using Kb.Plugins.Actions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.TestPlugin.Lib.LibPlugin))]

namespace Kb.TestPlugin.Lib;

/// <summary>A contract another plugin implements. It is shared because this plugin exports its assembly.</summary>
public interface ILibGreeter
{
    string Greet(string name);
}

/// <summary>An extension point this plugin defines: another plugin adds one with <c>app.Add</c>, as it would add one of the core's.</summary>
public sealed record LibThing(string Name);

/// <summary>An action this plugin defines. Another plugin hooks it as it would hook one of the core's.</summary>
[Action("lib.greet", Read = true)]
public sealed record LibGreet : IAction<LibGreeting>
{
    public required string Name { get; init; }
}

/// <summary>By: the user the action ran as, so a test can see that an action sent from another plugin's route still runs as the caller.</summary>
public sealed record LibGreeting(string Text, Guid? By = null);

/// <summary>A library plugin: it brings a contract, an extension point and an action, and leaves the behaviour to the plugins that require it.</summary>
public sealed class LibPlugin : IKbPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) { }

    public void Configure(IPluginApp app, IPluginContext context) =>
        app.Handle<LibGreet, LibGreeting, LibGreetHandler>().Routes(group =>
        {
            group.MapGet("/greet/{name}", (string name, IActionBus bus, CancellationToken ct) => bus.SendAsync(new LibGreet { Name = name }, ct).ToHttp()).WithAction<LibGreet>();
            group.MapGet("/things", (IRegistrations registrations) => Results.Ok(registrations.All<LibThing>().Select(t => new { plugin = t.PluginId, name = t.Value.Name })));
        });
}

public sealed class LibGreetHandler(IEnumerable<ILibGreeter> greeters) : IActionHandler<LibGreet, LibGreeting>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<LibGreet> ctx) => Task.FromResult(ctx.Actor.Kind == ActorKind.Anonymous ? Problem.Unauthorized : null);

    public Task<Outcome<LibGreeting>> HandleAsync(ActionContext<LibGreet> ctx) =>
        Task.FromResult<Outcome<LibGreeting>>(greeters.FirstOrDefault() is { } greeter ? new LibGreeting(greeter.Greet(ctx.Action.Name), ctx.Actor.UserId) : Problem.NotFound);
}
