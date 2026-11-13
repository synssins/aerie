using Kb.Plugins;
using Kb.TestPlugin.Lib;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.TestPlugin.Dependent.DependentPlugin))]

namespace Kb.TestPlugin.Dependent;

/// <summary>Requires the test library and implements its contract.</summary>
public sealed class DependentPlugin : IKbPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) => services.AddSingleton<ILibGreeter, DependentGreeter>();
}

public sealed class DependentGreeter : ILibGreeter
{
    public string Greet(string name) => $"Hello, {name}.";
}
