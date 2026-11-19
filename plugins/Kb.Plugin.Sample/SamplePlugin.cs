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
        .Handle<ReadAckTotals, AckTotals, ReadAckTotalsHandler>()
        .Before<SavePage, MajorNeedsNote>()                        // refuse a core action
        .After<SavePage, PageDto, ClearOnMajor>()                  // react once a core action has succeeded
        .Job<PurgeJob>("purge")                                    // the schedule "purge" of the manifest
        .HealthCheck<TableCheck>("table")
        .EditorNode(Notice.Renderer)                               // how the server reads its editor block
        .ImportContributor<NoticeFromFile>()                       // add to the drafts of AI imports
        .DraftContributor<NoticeFromDraft>()                       // ... from the text the AI drafted
        .DraftContributor<DiagramOutline>()                        // ... from the diagrams the AI read in its pictures
        .ImportFileKinds(".notice")                                // a kind the app does not read: drafted by this plugin
        .ImportTool<BoxesAndLines>(BoxesAndLines.Name, BoxesAndLines.Description, BoxesAndLines.Schema) // a tool the AI may call while drafting (0.11.0 draft)
        .SignInMethod<SampleSignIn>("example", "Sign in with Example ID") // a button on the sign-in screen
        .SignInProviders<SampleProviderCheck>() // providers an Admin adds on this plugin's page, each with its own button
        .Routes(SampleRoutes.Map);                                 // under /api/v1/plugins/kb.sample/
    // #endregion
}

// #region guide:settings
/// <summary>The plugin's settings as an Admin set them in the app. They are read each time, so a change applies without a restart.</summary>
public sealed class SampleSettings(IPluginContext context)
{
    public bool RequireNoteOnMajor => context.GetSetting("RequireNoteOnMajor") == "true";
    public bool ResetOnMajor => context.GetSetting("ResetOnMajor") == "true";
    // The manifest gives KeepDays a default (365) and bounds (0 to 3650): the app returns the default while none is saved and refuses a
    // value out of bounds, so the value is always a number here.
    public int KeepDays => int.Parse(context.GetSetting("KeepDays")!, System.Globalization.CultureInfo.InvariantCulture);
    public string? SignInDomain => context.GetSetting("SignInDomain")?.Trim() is { Length: > 0 } d ? d : null;
}
// #endregion
