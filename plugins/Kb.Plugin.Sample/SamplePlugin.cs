using Kb.Plugins;
using Kb.Plugins.Actions;
using Kb.Plugins.Data;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.Plugin.Sample.SamplePlugin))]

namespace Kb.Plugin.Sample;

/// <summary>
/// The reference plugin: readers acknowledge a page, and people who review a space see who has.
/// It uses only the contract assemblies (Kb.Plugins.Abstractions, Kb.Plugins.Data), as a plugin from outside this repository would.
/// </summary>
public sealed class SamplePlugin : IKbPlugin
{
    // #region guide:configure
    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.AddSingleton(new SampleSettings(context));

    public void Configure(IPluginApp app, IPluginContext context) => app
        .AddData<SampleData>()                                     // its own table
        .Handle<Acknowledge, AckCount, AcknowledgeHandler>()       // its own actions
        .Handle<CountAcks, AckCount, CountAcksHandler>()
        .Handle<ReadAcks, AckList, ReadAcksHandler>()
        .Before<SavePage, MajorNeedsNote>()                        // refuse a core action
        .After<SavePage, PageDto, ClearOnMajor>()                  // react once a core action has succeeded
        .Job<PurgeJob>("purge")                                    // the schedule "purge" of the manifest
        .HealthCheck<TableCheck>("table")
        .Routes(SampleRoutes.Map);                                 // under /api/v1/plugins/kb.sample/
    // #endregion
}

// #region guide:settings
/// <summary>The plugin's settings as an Admin set them in the app. They are read each time, so a change applies without a restart.</summary>
public sealed class SampleSettings(IPluginContext context)
{
    public bool RequireNoteOnMajor => context.GetSetting("RequireNoteOnMajor") == "true";
    public bool ResetOnMajor => context.GetSetting("ResetOnMajor") == "true";
    public int KeepDays => int.TryParse(context.GetSetting("KeepDays"), out var days) && days >= 0 ? days : 365;
}
// #endregion
