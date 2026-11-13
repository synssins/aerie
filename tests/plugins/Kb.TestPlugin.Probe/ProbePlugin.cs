using Kb.Plugins;
using Kb.Plugins.Actions;
using Kb.Plugins.Data;
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
        if (context.Manifest.Schedules.Any(s => s.Name == "tick") || context.GetSetting("Register") == "job") app.Job<ProbeJob>("tick");
        if (context.GetSetting("Register") == "corehook" && Type.GetType("Kb.Web.Actions.CreateToken, Kb.Web") is { } coreOnly)
            app.Add(new BeforeHook(coreOnly, typeof(AnyHook<>).MakeGenericType(coreOnly), false)); // an action that is not in the contract assembly
        if (context.GetSetting("Register") == "dupehandler") app.Handle<ReadPage, PageDto, StolenReadPage>(); // an action that already has a handler
        if (context.GetSetting("Register") == "health") app.HealthCheck<ProbeHealth>("remote");
        if (context.GetSetting("Register") == "data") app.AddData<ProbeData>().Routes(ProbeDataRoutes.Map);
        if (context.GetSetting("Register") == "baddata") app.AddData<ProbeBrokenData>();
        // An editor node of the probe's own. What it gives is unlike the placeholder, so a test can tell who rendered the node.
        if (context.GetSetting("Register") == "nodes")
            app.EditorNode(new NodeRenderer
            {
                Type = "probeBox",
                Plain = n => "probeword" + Label(context, n),
                MarkdownLines = n => ["> [box] " + Label(context, n)],
                Blocks = n => [new DocParagraph([new DocRun("BOX " + Label(context, n))])],
            });
        if (context.GetSetting("Register") == "corenode") app.EditorNode(new NodeRenderer { Type = "paragraph", Plain = _ => "taken over" }); // a node the editor has by itself
    }

    private static string Label(IPluginContext context, System.Text.Json.JsonElement node) =>
        context.GetSetting("FailOn") == "node" ? throw new InvalidOperationException("Probe renderer failed.") : node.GetProperty("attrs").GetProperty("label").GetString()!;
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
