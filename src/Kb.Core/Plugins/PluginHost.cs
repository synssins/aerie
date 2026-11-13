using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kb.Core.Plugins;

public enum PluginOrigin { BuiltIn, Uploaded }

/// <summary>One plugin as the app sees it: what it is, its state, and whether this server loaded it.</summary>
public sealed record PluginInfo(PluginManifest Manifest, PluginOrigin Origin, PluginState State, string? Reason, bool Locked, bool Loaded, string Source)
{
    /// <summary>The actions it has before hooks or result filters on, as recorded at its last successful start.</summary>
    public IReadOnlyList<string> Guards { get; init; } = [];
}

/// <summary>What a plugin guards, as stored in <see cref="PluginStatus.GuardsJson"/>: <c>{ "actions": ["page.save", ...] }</c>.</summary>
public static class PluginGuards
{
    private sealed record Stored(IReadOnlyList<string>? Actions);
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Write(IEnumerable<string> actions) => System.Text.Json.JsonSerializer.Serialize(new Stored([.. actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]), Json);

    public static IReadOnlyList<string> Read(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<Stored>(json, Json)?.Actions ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }
}

/// <summary>Every plugin known at start, loaded or not. A singleton; a change restarts the app, so it never changes while the app runs.</summary>
public sealed class PluginRegistry(IReadOnlyList<PluginInfo> all, bool safeMode, long generation, IReadOnlyDictionary<string, Assembly>? assemblies = null)
{
    private readonly IReadOnlyDictionary<string, Assembly> _assemblies = assemblies ?? new Dictionary<string, Assembly>();

    public IReadOnlyList<PluginInfo> All { get; } = all;
    public IEnumerable<PluginInfo> Loaded => All.Where(p => p.Loaded);
    /// <summary>Safe Mode: uploaded plugins are listed and none is loaded.</summary>
    public bool SafeMode { get; } = safeMode;
    /// <summary>The plugin generation this server started with. When the database holds a newer one, this server restarts.</summary>
    public long Generation { get; } = generation;

    public PluginInfo? Find(string id) => All.FirstOrDefault(p => p.Manifest.Id == id);

    /// <summary>The main assembly of a loaded plugin.</summary>
    public Assembly? AssemblyOf(string id) => _assemblies.GetValueOrDefault(id);
}

/// <summary>
/// The current plugin settings that are not secret, kept in memory. A plugin's context reads it, so a change made in the app
/// applies without a restart: the settings store swaps the values in one step.
/// </summary>
public sealed class PluginSettingsSnapshot
{
    private volatile IReadOnlyDictionary<(string PluginId, string Key), string?> _values = new Dictionary<(string, string), string?>();

    public string? Get(string pluginId, string key) => _values.GetValueOrDefault((pluginId, key));

    /// <summary>Reads a plugin's secret (plugin id, key). Set by the settings store once the app has started; null before that.</summary>
    public Func<string, string, CancellationToken, Task<string?>>? SecretReader { get; set; }

    public void Replace(IEnumerable<(string PluginId, string Key, string? Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToDictionary(v => (v.PluginId, v.Key), v => v.Value);
    }
}

/// <summary>Everything plugins registered through <see cref="IPluginApp"/>, in load order.</summary>
public sealed class PluginRegistrations : IRegistrations
{
    private readonly List<(string PluginId, object Value)> _all = [];

    public void Add(string pluginId, object value) => _all.Add((pluginId, value));

    public IReadOnlyList<Registered<T>> All<T>() where T : class =>
        [.. _all.Where(r => r.Value is T).Select(r => new Registered<T>(r.PluginId, (T)r.Value))];
}

/// <summary>What one plugin asked for while it was configured. Nothing of it reaches the app unless both of its methods returned.</summary>
public sealed record ConfiguredPlugin(IReadOnlyList<ServiceDescriptor> Services, IReadOnlyList<object> Registrations);

public static class PluginRuntime
{
    /// <summary>Built-in plugins: the <c>Kb.Plugin.*.dll</c> files shipped in the app folder that carry <see cref="KbPluginAttribute"/> and a manifest.</summary>
    public static IReadOnlyList<(PluginManifest Manifest, Assembly Assembly)> BuiltIns(string appFolder)
    {
        var found = new List<(PluginManifest, Assembly)>();
        foreach (var file in Directory.GetFiles(appFolder, "Kb.Plugin.*.dll").Order(StringComparer.Ordinal))
        {
            var assembly = Assembly.LoadFrom(file);
            if (assembly.GetCustomAttribute<KbPluginAttribute>() is null) continue;
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(PluginManifests.FileName, StringComparison.OrdinalIgnoreCase));
            if (resource is null) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            if (PluginManifests.Parse(reader.ReadToEnd()) is { } manifest) found.Add((manifest, assembly));
        }
        return found;
    }

    /// <summary>
    /// Loads an uploaded plugin's main assembly from its unpacked folder, in its own load context.
    /// <paramref name="exports"/> maps an assembly name to the context of the required plugin that shares it.
    /// </summary>
    public static Assembly LoadUploaded(PluginManifest manifest, string folder, IReadOnlyDictionary<string, AssemblyLoadContext> exports, out AssemblyLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var own = new PluginLoadContext(manifest.Id, folder, exports);
        context = own;
        return own.LoadFile(Path.Combine(folder, PluginPackageReader.MainAssembly(manifest)));
    }

    /// <summary>
    /// Runs the plugin's <c>ConfigureServices</c> and <c>Configure</c> against scratch lists and returns them.
    /// It throws what the plugin throws; the caller then keeps nothing of it.
    /// </summary>
    public static ConfiguredPlugin Configure(Assembly assembly, PluginManifest manifest, PluginSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var type = assembly.GetCustomAttribute<KbPluginAttribute>()?.PluginType ?? throw new InvalidOperationException("The assembly names no plugin class.");
        var plugin = Activator.CreateInstance(type) as IKbPlugin ?? throw new InvalidOperationException($"{type.FullName} is not a plugin.");
        var context = new PluginContext(manifest, settings);
        var services = new ServiceCollection();
        var app = new ScratchApp();
        plugin.ConfigureServices(services, context);
        plugin.Configure(app, context);
        // A hosted service that throws stops the whole app. Background work comes with jobs.
        if (services.Any(d => d.ServiceType == typeof(IHostedService))) throw new InvalidOperationException("Hosted services are not supported.");
        return new ConfiguredPlugin([.. services], app.Items);
    }

    private sealed class ScratchApp : IPluginApp
    {
        public List<object> Items { get; } = [];

        public IPluginApp Add<T>(T registration) where T : class
        {
            ArgumentNullException.ThrowIfNull(registration);
            Items.Add(registration);
            return this;
        }
    }

    /// <summary>
    /// One per uploaded plugin, never unloaded. An assembly name resolves in this order: the host's copy when the host has one
    /// (.NET, ASP.NET Core, EF Core, Kb.*), so types are the same everywhere; an assembly a required plugin shares; the plugin's own folder.
    /// Assemblies are read into memory, so the folder is never locked and can be replaced by the next version.
    /// </summary>
    private sealed class PluginLoadContext(string name, string folder, IReadOnlyDictionary<string, AssemblyLoadContext> exports) : AssemblyLoadContext("kb-plugin:" + name, isCollectible: false)
    {
        private static readonly ConcurrentDictionary<string, bool> InHost = new(StringComparer.OrdinalIgnoreCase);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var simple = assemblyName.Name;
            if (simple is null || InHost.GetOrAdd(simple, HostHas)) return null; // null: the default context answers
            if (Assemblies.FirstOrDefault(a => a.GetName().Name == simple) is { } loaded) return loaded;
            if (exports.TryGetValue(simple, out var owner)) return owner.LoadFromAssemblyName(assemblyName);
            var path = Path.Combine(folder, simple + ".dll");
            return File.Exists(path) ? LoadFile(path) : null;
        }

        public Assembly LoadFile(string path)
        {
            using var assembly = new MemoryStream(File.ReadAllBytes(path));
            var symbols = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(symbols)) return LoadFromStream(assembly);
            using var pdb = new MemoryStream(File.ReadAllBytes(symbols));
            return LoadFromStream(assembly, pdb);
        }

        private static bool HostHas(string simpleName)
        {
            try { return Default.LoadFromAssemblyName(new AssemblyName(simpleName)) is not null; }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { return false; }
        }
    }
}

/// <summary>What a plugin sees of the host: its manifest, and its settings as they are set in the app right now.</summary>
internal sealed class PluginContext(PluginManifest manifest, PluginSettingsSnapshot settings) : IPluginContext
{
    public PluginManifest Manifest { get; } = manifest;
    public string? GetSetting(string key) => settings.Get(Manifest.Id, key);

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) =>
        settings.SecretReader is { } read ? read(Manifest.Id, key, ct) : throw new InvalidOperationException("Secrets are read after startup, not while a plugin is configured.");
}

/// <summary>Blocks plugin HTTP calls to hosts not declared in the manifest.</summary>
public sealed class OutboundHostGuard(IReadOnlyCollection<string> allowedHosts) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host;
        if (host is null || !allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Plugin outbound call to '{host}' blocked: host not declared in manifest OutboundHosts.");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Picks the configured implementation of a single-provider extension point.</summary>
public sealed class ProviderSelector<T>(IEnumerable<T> all) where T : class, IProvider
{
    public T? Get(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ? null : all.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
}
