using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.TestPlugin.Lib.LibPlugin))]

namespace Kb.TestPlugin.Lib;

/// <summary>A contract another plugin implements. It is shared because this plugin exports its assembly.</summary>
public interface ILibGreeter
{
    string Greet(string name);
}

/// <summary>A library plugin: it brings a contract and no behaviour of its own.</summary>
public sealed class LibPlugin : IKbPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) { }
}
