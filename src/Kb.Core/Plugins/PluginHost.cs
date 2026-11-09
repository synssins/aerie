using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Kb.Core.Config;
using Kb.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kb.Core.Plugins;

public enum PluginOrigin { BuiltIn, DropIn }

public sealed record LoadedPlugin(PluginManifest Manifest, PluginOrigin Origin, IKbPlugin Instance, bool Enabled);

public sealed record RefusedPlugin(string Path, string Reason);

/// <summary>Result of discovery: what loaded, what was refused (and why). Registered as a singleton for the admin UI.</summary>
public sealed class PluginRegistry
{
    public PluginRegistry(IReadOnlyList<LoadedPlugin> all, IReadOnlyList<RefusedPlugin> refused)
    {
        All = all;
        Refused = refused;
    }
    public IReadOnlyList<LoadedPlugin> All { get; }
    public IReadOnlyList<RefusedPlugin> Refused { get; }
    public IEnumerable<LoadedPlugin> Enabled => All.Where(p => p.Enabled);
}

/// <summary>
/// Discovers built-in plugins (assemblies with [assembly: KbPlugin]) and drop-in plugins (one folder each under
/// Plugins:Folder). Drop-ins load only when their assembly SHA-256 is in Plugins:AllowedHashes.
/// </summary>
public static class PluginLoader
{
    public const string ManifestFileName = "kb-plugin.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static PluginRegistry Discover(IEnumerable<Assembly> builtInAssemblies, PluginOptions options, string contentRoot, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var loaded = new List<LoadedPlugin>();
        var refused = new List<RefusedPlugin>();
        var disabled = new HashSet<string>(options.Disabled, StringComparer.OrdinalIgnoreCase);

        foreach (var asm in builtInAssemblies.Distinct())
        {
            var attr = asm.GetCustomAttribute<KbPluginAttribute>();
            if (attr is null) continue;
            var manifestPath = Path.Combine(Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory, $"{asm.GetName().Name}.{ManifestFileName}");
            var manifest = ReadManifestFromResourceOrFile(asm, manifestPath);
            if (manifest is null) { refused.Add(new(asm.FullName ?? "?", "missing manifest")); continue; }
            loaded.Add(new(manifest, PluginOrigin.BuiltIn, Create(attr.PluginType), !disabled.Contains(manifest.Id)));
        }

        var folder = Path.IsPathRooted(options.Folder) ? options.Folder : Path.Combine(contentRoot, options.Folder);
        if (Directory.Exists(folder))
        {
            var allowed = new HashSet<string>(options.AllowedHashes, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(folder))
            {
                var manifestFile = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestFile)) { refused.Add(new(dir, "missing manifest")); continue; }
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), Json);
                if (manifest is null) { refused.Add(new(dir, "unreadable manifest")); continue; }
                var dll = Path.Combine(dir, $"{Path.GetFileName(dir)}.dll");
                if (!File.Exists(dll)) { refused.Add(new(dir, $"expected {Path.GetFileName(dll)}")); continue; }
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dll)));
                if (!allowed.Contains(hash))
                {
                    refused.Add(new(dir, $"assembly hash {hash} not in Plugins:AllowedHashes"));
                    log?.LogWarning("Refused drop-in plugin {Dir}: hash {Hash} not allow-listed", dir, hash);
                    continue;
                }
                var alc = new PluginLoadContext(dll);
                var asm = alc.LoadFromAssemblyPath(dll);
                var attr = asm.GetCustomAttribute<KbPluginAttribute>();
                if (attr is null) { refused.Add(new(dir, "no [assembly: KbPlugin] attribute")); continue; }
                loaded.Add(new(manifest, PluginOrigin.DropIn, Create(attr.PluginType), !disabled.Contains(manifest.Id)));
            }
        }

        var dupes = loaded.GroupBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) throw new InvalidOperationException($"Duplicate plugin ids: {string.Join(", ", dupes)}");
        return new PluginRegistry(loaded, refused);
    }

    private static PluginManifest? ReadManifestFromResourceOrFile(Assembly asm, string filePath)
    {
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var s = asm.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize<PluginManifest>(s, Json);
        }
        return File.Exists(filePath) ? JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(filePath), Json) : null;
    }

    private static IKbPlugin Create(Type t) =>
        Activator.CreateInstance(t) as IKbPlugin ?? throw new InvalidOperationException($"{t.FullName} does not implement IKbPlugin");

    /// <summary>Isolated load context; Kb.Plugins.Abstractions and framework assemblies resolve from the host so types unify.</summary>
    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IKbPlugin).Assembly.GetName().Name) return null; // share host copy
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

internal sealed class PluginContext(PluginManifest manifest, IConfiguration config, IPluginSecretStore secrets) : IPluginContext
{
    public PluginManifest Manifest { get; } = manifest;
    public string? GetSetting(string key) => config[$"Kb:Plugins:Settings:{Manifest.Id}:{key}"];
    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => secrets.GetAsync($"{Manifest.Id}--{key}", ct);
}

/// <summary>Host secret store. Default reads configuration (user-secrets / env / Key Vault config provider).</summary>
public interface IPluginSecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);
}

public sealed class ConfigurationSecretStore(IConfiguration config) : IPluginSecretStore
{
    public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult(config[name]);
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

public static class PluginServiceCollectionExtensions
{
    /// <summary>Configures every enabled plugin and registers its guarded HttpClient. Returns the registry.</summary>
    public static PluginRegistry AddKbPlugins(this IServiceCollection services, IConfiguration config, PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.AddSingleton(registry);
        services.AddSingleton<IPluginSecretStore, ConfigurationSecretStore>();
        var secrets = new ConfigurationSecretStore(config);
        foreach (var p in registry.Enabled)
        {
            var hosts = p.Manifest.OutboundHosts.ToArray();
            services.AddHttpClient(PluginHttp.ClientName(p.Manifest.Id))
                .AddHttpMessageHandler(() => new OutboundHostGuard(hosts));
            p.Instance.ConfigureServices(services, new PluginContext(p.Manifest, config, secrets));
        }
        return registry;
    }
}

/// <summary>Picks the configured implementation of a single-provider extension point.</summary>
public sealed class ProviderSelector<T>(IEnumerable<T> all) where T : class, IProvider
{
    public T? Get(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ? null : all.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
}
