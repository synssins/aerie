using Kb.Plugins;
using Kb.Plugins.Actions;
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
        services.AddSingleton(new ProbeState(context));
        // A test proves the plugin is loaded by finding this sink in the container.
        services.AddSingleton<IAuditSink>(new ProbeSink(context));
        if (context.GetSetting("FailOn") == "services") throw new InvalidOperationException("Probe failed in ConfigureServices.");
        if (context.GetSetting("Register") == "filestore") services.AddSingleton<IFileStore, ProbeFileStore>(); // a service the core has
        if (context.GetSetting("Register") == "kinds")
        {
            services.AddSingleton<IFileStoreKind, ProbeStoreKind>();
            services.AddSingleton<ILogShipperKind, ProbeLogKind>();
        }
        if (context.GetSetting("Register") == "hosted") services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, ProbeHosted>();
    }

    public void Configure(IPluginApp app, IPluginContext context)
    {
        if (context.GetSetting("FailOn") == "configure")
            throw context.GetSetting("FailWith") == "timeout" ? new TaskCanceledException("Probe timed out in Configure.") : new InvalidOperationException("Probe failed in Configure.\nSecond line of the error.");
        // How a hook is registered is fixed here; what it does is read from the settings each time it runs.
        var optional = context.GetSetting("Optional") == "true";
        app.Handle<ProbeEcho, ProbeEchoResult, EchoHandler>()
            .Handle<ProbePeek, ProbePeekResult, PeekHandler>()
            .Before<ProbeEcho, EchoHook>(optional)
            .Filter<ProbePeek, ProbePeekResult, PeekFilter>(optional)
            .After<ProbeEcho, ProbeEchoResult, EchoAfter>()
            .After<ProbePeek, ProbePeekResult, PeekAfter>()
            .Routes(ProbeRoutes.Map);
        AnyAction.Register(app);
    }
}

public sealed class ProbeSink(IPluginContext context) : IAuditSink
{
    public string ProviderId => context.Manifest.Id;

    public Task ForwardAsync(AuditRecord record, CancellationToken ct) =>
        context.GetSetting("FailOn") != "sink" ? Task.CompletedTask
        : throw (context.GetSetting("FailWith") == "timeout" ? new TaskCanceledException("Probe sink timed out.") : new InvalidOperationException("Probe sink failed."));
}

public sealed class ProbeFileStore : IFileStore
{
    public Task SaveAsync(string key, byte[] data, CancellationToken ct) => throw new NotSupportedException();
    public Task<long> SaveAsync(string key, Stream source, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
    public Task<Stream?> OpenAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> SizeAsync(string key, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class ProbeHosted : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Probe hosted service failed.");
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A storage kind and a log kind only this plugin supplies. Both keep what they get in memory.</summary>
public sealed class ProbeStoreKind : IFileStoreKind
{
    public string Kind => "probe-store";
    public string Label => "Probe store";
    public IReadOnlyList<FileStoreField> Fields => [];
    public IFileStore Open(IReadOnlyDictionary<string, string?> settings) => new MemoryStore();

    private sealed class MemoryStore : IFileStore
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files = new();
        public Task SaveAsync(string key, byte[] data, CancellationToken ct) { Files[key] = data; return Task.CompletedTask; }
        public async Task<long> SaveAsync(string key, Stream source, long maxBytes, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms, ct);
            if (ms.Length > maxBytes) return -1;
            Files[key] = ms.ToArray();
            return ms.Length;
        }
        public Task<Stream?> OpenAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(Files.TryGetValue(key, out var data) ? new MemoryStream(data) : null);
        public Task DeleteAsync(string key, CancellationToken ct) { Files.TryRemove(key, out _); return Task.CompletedTask; }
        public Task<long?> SizeAsync(string key, CancellationToken ct) => Task.FromResult<long?>(Files.TryGetValue(key, out var data) ? data.Length : null);
    }
}

public sealed class ProbeLogKind : ILogShipperKind
{
    public string Kind => "probe-log";
    public string Label => "Probe log";
    public IReadOnlyList<LogShipField> Fields => [];
    public ILogShipper Open(IReadOnlyDictionary<string, string?> settings) => new Discard();

    private sealed class Discard : ILogShipper
    {
        public Task SendAsync(IReadOnlyList<LogEvent> events, CancellationToken ct) => Task.CompletedTask;
    }
}
