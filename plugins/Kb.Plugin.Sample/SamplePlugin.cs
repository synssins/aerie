using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.Plugin.Sample.SamplePlugin))]

namespace Kb.Plugin.Sample;

/// <summary>Reference plugin for the developer guide: one page action that echoes its input.</summary>
public sealed class SamplePlugin : IKbPlugin
{
    public const string Id = "kb.sample";

    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.AddSingleton<IPageAction, EchoPageAction>();
}

public sealed class EchoPageAction : IPageAction
{
    public string ProviderId => SamplePlugin.Id;
    public string Label => "Echo (sample)";
    public IReadOnlyList<string> InputFields => ["note"];

    public Task<PageActionResult> RunAsync(PageActionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var note = context.Input.TryGetValue("note", out var n) ? n : "";
        return Task.FromResult(new PageActionResult(true, $"{context.PageTitle}: {note}"));
    }
}
