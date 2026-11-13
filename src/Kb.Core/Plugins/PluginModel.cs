namespace Kb.Core.Plugins;

/// <summary>Enabled: loaded at start. Disabled: an Admin turned it off. DisabledByCore: it failed, and the core turned it off with a reason.</summary>
public enum PluginState { Enabled, Disabled, DisabledByCore }

/// <summary>An installed package, kept in the database so every server loads the same bytes. Key: (PluginId, Version).</summary>
public sealed class PluginPackage
{
    public required string PluginId { get; set; }
    public required string Version { get; set; }
    public required byte[] Bytes { get; set; }
    public required string Sha256 { get; set; }
    public required string ManifestJson { get; set; }
    /// <summary>"upload", or a repository address later.</summary>
    public required string Source { get; set; }
    public Guid? InstalledBy { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>The state of a plugin. Key: PluginId. Built-in plugins have a row too.</summary>
public sealed class PluginStatus
{
    public required string PluginId { get; set; }
    public PluginState State { get; set; }
    /// <summary>The installed version in use. Null for a built-in plugin.</summary>
    public string? ActiveVersion { get; set; }
    /// <summary>Why the core disabled it.</summary>
    public string? Reason { get; set; }
    /// <summary>Set when the core disabled an enforcing plugin: what it guarded stays refused until an Admin releases the lock.</summary>
    public DateTimeOffset? LockedAt { get; set; }
    /// <summary>What it guards, as JSON. Filled by later tasks.</summary>
    public string GuardsJson { get; set; } = "{}";
    /// <summary>The package is gone and its data is kept.</summary>
    public bool Removed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One setting of a plugin, set in the app. Key: (PluginId, Key).</summary>
public sealed class PluginSettingValue
{
    public required string PluginId { get; set; }
    public required string Key { get; set; }
    /// <summary>The value; protected text when Secret.</summary>
    public string? Value { get; set; }
    public bool Secret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
