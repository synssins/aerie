using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Plugins;

/// <summary>Marks an assembly as containing a Kb plugin. Built-in plugins are discovered by this attribute.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class KbPluginAttribute(Type pluginType) : Attribute
{
    public Type PluginType { get; } = pluginType;
}

/// <summary>Entry point every plugin implements. Called once at startup when the plugin is enabled.</summary>
public interface IKbPlugin
{
    void ConfigureServices(IServiceCollection services, IPluginContext context);
}

/// <summary>What the host gives a plugin. Plugins never read host config or secrets directly.</summary>
public interface IPluginContext
{
    PluginManifest Manifest { get; }

    /// <summary>Plugin setting from config section <c>Plugins:Settings:{pluginId}:{key}</c>.</summary>
    string? GetSetting(string key);

    /// <summary>Plugin secret resolved through the host secret store (Key Vault on Azure, protected store on IIS).</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
}

/// <summary>Contents of <c>kb-plugin.json</c>, shipped next to the plugin assembly.</summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Publisher { get; init; }
    public string? Description { get; init; }
    public string MinKbVersion { get; init; } = "0.1.0";
    public IReadOnlyList<string> ExtensionPoints { get; init; } = [];
    /// <summary>Only these hosts may be called through the plugin's HttpClient (<see cref="PluginHttp.ClientName"/>).</summary>
    public IReadOnlyList<string> OutboundHosts { get; init; } = [];
    public IReadOnlyList<PluginSetting> Settings { get; init; } = [];
    public IReadOnlyList<PluginSchedule> Schedules { get; init; } = [];
}

public sealed record PluginSetting(string Key, string Label, bool Secret = false, bool Required = false);

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
