using Kb.Plugins;
using Kb.Plugins.Actions;
using Kb.TestPlugin.Lib;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.TestPlugin.Dependent.DependentPlugin))]

namespace Kb.TestPlugin.Dependent;

/// <summary>Requires the test library: implements its contract, adds to its extension point and hooks its action.</summary>
public sealed class DependentPlugin : IKbPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) => services.AddSingleton<ILibGreeter, DependentGreeter>();

    public void Configure(IPluginApp app, IPluginContext context) =>
        app.Add(new LibThing("from the dependent")).Before<LibGreet, ShoutHook>();
}

public sealed class DependentGreeter : ILibGreeter
{
    public string Greet(string name) => $"Hello, {name}.";
}

/// <summary>Changes the library's action before it runs.</summary>
public sealed class ShoutHook : IBeforeAction<LibGreet>
{
    public Task<Problem?> BeforeAsync(ActionContext<LibGreet> ctx)
    {
        ctx.Action = ctx.Action with { Name = ctx.Action.Name.ToUpperInvariant() };
        return Task.FromResult<Problem?>(null);
    }
}
