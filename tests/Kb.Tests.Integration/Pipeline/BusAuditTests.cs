using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Plugins;
using Kb.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The audit entries of an action that has succeeded are written whatever happens to the request: a person who closes the tab
/// right after a delete must not be able to make the delete vanish from the audit log.
/// </summary>
public sealed class BusAuditTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Action, bool CanBeCancelled)> _appends = new();
    private readonly KbAppFactory _f;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BusAuditTests() => _f = new(KbAppFactory.NewDbPath(), services: s =>
    {
        // Wraps the real audit log and notes, for each entry, whether the caller could still cancel the write.
        var real = s.Last(d => d.ServiceType == typeof(IAuditLog));
        s.Remove(real);
        s.Add(new ServiceDescriptor(typeof(IAuditLog), sp => new Spy((IAuditLog)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), _appends), real.Lifetime));
    });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private sealed class Spy(IAuditLog inner, ConcurrentQueue<(string, bool)> appends) : IAuditLog
    {
        public Task AppendAsync(AuditEvent e, CancellationToken ct = default)
        {
            appends.Enqueue((e.Action, ct.CanBeCanceled));
            return inner.AppendAsync(e, ct);
        }

        public Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default) => inner.VerifyAsync(ct);
    }

    [Fact]
    public async Task AuditEntriesOfASucceededAction_AreWrittenWithoutTheRequestsCancellation()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Reset password" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);

        // The entries the bus writes for the two write actions above.
        var written = _appends.Where(a => a.Action is "space.create" or "page.delete").ToList();
        Assert.Equal(["space.create", "page.delete"], written.Select(a => a.Action));
        Assert.DoesNotContain(written, a => a.CanBeCancelled);
    }
}
