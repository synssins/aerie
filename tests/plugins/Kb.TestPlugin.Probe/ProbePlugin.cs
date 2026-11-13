using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.TestPlugin.Probe.ProbePlugin))]

namespace Kb.TestPlugin.Probe;

/// <summary>
/// Does what its settings say, so a test can make a plugin fail at a named point.
/// It reads its id from the manifest, never from a constant, so a test can package it under another id.
/// </summary>
public sealed class ProbePlugin : IKbPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // A test proves the plugin is loaded by finding this sink in the container.
        services.AddSingleton<IAuditSink>(new ProbeSink(context));
        if (context.GetSetting("FailOn") == "services") throw new InvalidOperationException("Probe failed in ConfigureServices.");
    }

    public void Configure(IPluginApp app, IPluginContext context)
    {
        if (context.GetSetting("FailOn") == "configure") throw new InvalidOperationException("Probe failed in Configure.\nSecond line of the error.");
    }
}

public sealed class ProbeSink(IPluginContext context) : IAuditSink
{
    public string ProviderId => context.Manifest.Id;

    public Task ForwardAsync(AuditRecord record, CancellationToken ct) =>
        context.GetSetting("FailOn") == "sink" ? throw new InvalidOperationException("Probe sink failed.") : Task.CompletedTask;
}
