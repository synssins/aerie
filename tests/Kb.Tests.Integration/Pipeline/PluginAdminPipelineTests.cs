using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The plugin administration routes go through the action bus: each answers with its action's name, and only an Admin gets past the handler.
/// These actions are core only: no plugin can hook them, so there is no refusal by the probe to test.
/// </summary>
public sealed class PluginAdminPipelineTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins", Probe = Url + "/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync(); // the probe's settings can be read once it is known to the running app
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    /// <summary>Every route of the slice, sent well formed, with the name of its action. The order matters: the probe is removed near the end.</summary>
    private static async Task<List<(string Name, HttpResponseMessage Answer)>> AllAsync(HttpClient c) =>
    [
        ("plugin.list", await c.GetAsync(Url, Ct)),
        ("plugin.settings.read", await c.GetAsync($"{Probe}/settings", Ct)),
        ("plugin.settings.save", await c.PutAsJsonAsync($"{Probe}/settings", new { values = new Dictionary<string, string> { ["Greeting"] = "Hello" } }, Ct)),
        ("plugin.lock.release", await c.PostAsync($"{Probe}/release-lock", null, Ct)),
        ("plugin.state.set", await c.PutAsJsonAsync($"{Probe}/state", new { enabled = false }, Ct)),
        ("plugin.install", await c.PostAsync(Url, new ByteArrayContent(TestPlugins.Package(TestPlugins.Lib)), Ct)),
        ("plugin.remove", await c.DeleteAsync($"{Probe}?deleteData=false", Ct)),
        ("plugin.data.delete", await c.DeleteAsync($"{Probe}/data", Ct)),
    ];

    [Fact]
    public async Task EveryPluginAdminRoute_AnswersWithItsActionName()
    {
        var answers = await AllAsync(_admin);

        Assert.All(answers, a => Assert.Equal(a.Name, Action(a.Answer)));
        // The answers are what they were. A refusal by the core (404: the probe has no tables, so its removal left nothing to delete) names the action too.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound],
            answers.Select(a => a.Answer.StatusCode));
        var list = await answers[0].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["plugins", "safeMode"], list.EnumerateObject().Select(p => p.Name));
        var probe = list.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.test.probe");
        Assert.Equal(["id", "name", "version", "publisher", "description", "origin", "state", "reason", "locked", "loaded", "source", "requires", "requiredBy", "extensionPoints",
            "outboundHosts", "permissions", "enforcing", "settings", "replaces", "removed", "importFileKinds", "signInMethods"], probe.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["values", "secretsSet"], (await answers[1].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(p => p.Name));
        Assert.Equal("""{"restart":false}""", await answers[2].Answer.Content.ReadAsStringAsync(Ct));
        Assert.Equal("""{"id":"kb.test.lib","version":"1.0.0","restart":true}""", await answers[5].Answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EveryPluginAdminRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var user = _f.NewClient();
        (await user.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        var answers = await AllAsync(user);

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        await _f.WithDbAsync(async db =>
        {
            var status = Assert.Single(await db.PluginStatuses.AsNoTracking().Where(s => s.PluginId.StartsWith("kb.test.")).ToListAsync(Ct));
            Assert.Equal(("kb.test.probe", PluginState.Enabled, false), (status.PluginId, status.State, status.Removed));
            Assert.False(await db.PluginSettingValues.AnyAsync(Ct));
        });
    }
}
