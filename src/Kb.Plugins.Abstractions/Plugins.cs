using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Plugins;

/// <summary>Marks an assembly as containing a Kb plugin. Built-in plugins are discovered by this attribute.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class KbPluginAttribute(Type pluginType) : Attribute
{
    public Type PluginType { get; } = pluginType;
}

/// <summary>Entry point every plugin implements. Both methods run once, before the web host is built, when the plugin is enabled.</summary>
public interface IKbPlugin
{
    void ConfigureServices(IServiceCollection services, IPluginContext context);

    /// <summary>Registers what the plugin adds to the app (hooks, routes, jobs and the rest) through <see cref="IPluginApp"/>.</summary>
    void Configure(IPluginApp app, IPluginContext context) { }
}

/// <summary>What the host gives a plugin. Plugins never read host config or secrets directly.</summary>
public interface IPluginContext
{
    PluginManifest Manifest { get; }

    /// <summary>The current value of a plugin setting. A change made in the app applies without a restart.</summary>
    string? GetSetting(string key);

    /// <summary>A plugin secret. Only after startup, never inside <c>ConfigureServices</c> or <c>Configure</c>.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
}

/// <summary>The one registration mechanism. Hooks, routes, jobs and the rest are extension methods that call <see cref="Add{T}"/>.</summary>
/// <summary>How a plugin's tables are named.</summary>
public static class PluginTables
{
    /// <summary>
    /// The prefix of every table of a plugin: <c>plg_&lt;id with dots and hyphens as underscores&gt;__</c>.
    /// </summary>
    public static string Prefix(string pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        return "plg_" + pluginId.Replace('.', '_').Replace('-', '_') + "__";
    }
}

public interface IPluginApp
{
    IPluginApp Add<T>(T registration) where T : class;
}

/// <summary>A registration and the plugin that made it. The core's own registrations carry "core".</summary>
public sealed record Registered<T>(string PluginId, T Value) where T : class;

/// <summary>Everything registered through <see cref="IPluginApp"/>, in plugin load order.</summary>
public interface IRegistrations
{
    IReadOnlyList<Registered<T>> All<T>() where T : class;
}

/// <summary>
/// Where a failure of a plugin is reported: a hook, a sink, a job or a sender that threw. Enough failures in a short time
/// and the core turns the plugin off. The core's own code and plugins that host other plugins' code call it.
/// </summary>
public interface IPluginFailures
{
    void Report(string pluginId, string where, Exception? exception = null);
}

/// <summary>Contents of <c>kb-plugin.json</c>, shipped next to the plugin assembly.</summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Publisher { get; init; }
    /// <summary>The name people see. The id is shown when there is none.</summary>
    public string? Name { get; init; }
    public string? Description { get; init; }
    /// <summary>The main assembly file. Default: <c>&lt;Id&gt;.dll</c>.</summary>
    public string? Assembly { get; init; }
    public string MinKbVersion { get; init; } = "0.1.0";
    /// <summary>The version of Kb.Plugins.Abstractions the plugin was built against.</summary>
    public string AbstractionsVersion { get; init; } = "0.1.0";
    /// <summary>The version of the plugin UI kit its script was built against. Null: the plugin has no script.</summary>
    public string? UiApiVersion { get; init; }
    /// <summary>True when the plugin enforces a rule. If the core disables it, what it guarded stays refused until an Admin releases the lock.</summary>
    public bool Enforcing { get; init; }
    public IReadOnlyList<PluginRequirement> Requires { get; init; } = [];
    /// <summary>Assembly names shared with plugins that require this one.</summary>
    public IReadOnlyList<string> Exports { get; init; } = [];
    public IReadOnlyList<string> ExtensionPoints { get; init; } = [];
    /// <summary>Only these hosts may be called through the plugin's HttpClient (<see cref="PluginHttp.ClientName"/>).</summary>
    public IReadOnlyList<string> OutboundHosts { get; init; } = [];
    public IReadOnlyList<PluginSetting> Settings { get; init; } = [];
    public IReadOnlyList<PluginPermission> Permissions { get; init; } = [];
    public IReadOnlyList<PluginSchedule> Schedules { get; init; } = [];
    public PluginUi? Ui { get; init; }
}

public sealed record PluginRequirement(string Id, string MinVersion);

/// <summary>Type: text, secret, number, toggle, select (with Options) or multiline. Restart: a change restarts the app.</summary>
public sealed record PluginSetting(string Key, string Label, bool Secret = false, bool Required = false,
    string Type = "text", IReadOnlyList<string>? Options = null, bool Restart = false);

/// <summary>Scope: space or global.</summary>
public sealed record PluginPermission(string Key, string Label, string Scope, string DefaultRole = "Owner");

public sealed record PluginUi
{
    /// <summary>The script of the plugin's screens, for example "ui/index.js".</summary>
    public string? Script { get; init; }
    /// <summary>The core components its script may wrap or replace.</summary>
    public IReadOnlyList<string> Replaces { get; init; } = [];
}

public sealed record PluginSchedule(string Name, [property: JsonPropertyName("everyMinutes")] int EveryMinutes);

public static class PluginHttp
{
    /// <summary>Named HttpClient per plugin: <c>kb-plugin:{id}</c>. Its handler enforces <see cref="PluginManifest.OutboundHosts"/>.</summary>
    public static string ClientName(string pluginId) => $"kb-plugin:{pluginId}";
}

/// <summary>Well-known extension point names used in manifests.</summary>
public static class ExtensionPointNames
{
    public const string EmailSender = "email-sender";
    public const string MapProvider = "map-provider";
    public const string SearchProvider = "search-provider";
    public const string AiProvider = "ai-provider";
    public const string RecordImporter = "record-importer";
    public const string PageAction = "page-action";
    public const string ExportFormat = "export-format";
    public const string AuditSink = "audit-sink";
    public const string MalwareScanner = "malware-scanner";
    public const string DnsProvider = "dns-provider";
}
