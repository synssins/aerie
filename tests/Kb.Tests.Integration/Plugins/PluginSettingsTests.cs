using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

public sealed class PluginSettingsTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins/kb.test.probe/settings";
    private const string Secret = "probe-token-value";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Probe, installed and loaded, with two more settings: a required one and a number.</summary>
    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe,
            m => m with { Settings = [.. m.Settings, new PluginSetting("Must", "Must", Required: true), new PluginSetting("Count", "Count", Type: "number"), new PluginSetting("On", "On", Type: "toggle"), new PluginSetting("Hosts", "Hosts", Type: "multiline")] }));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Sends the properties as a dictionary, so each key keeps its exact spelling (setting keys are case-sensitive).</summary>
    private Task<HttpResponseMessage> SaveAsync(object values, HttpClient? c = null) =>
        (c ?? _admin).PutAsJsonAsync(Url, new { values = values.GetType().GetProperties().ToDictionary(p => p.Name, p => (string?)p.GetValue(values)) }, Ct);
    private PluginSettingsStore Store(KbAppFactory? f = null) => (f ?? _f).Services.GetRequiredService<PluginSettingsStore>();

    [Fact]
    public async Task Settings_SaveAndRead_RoundTrip()
    {
        var r = await SaveAsync(new { Must = "yes", Greeting = "Hello", Count = "3", On = "true" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("restart").GetBoolean());

        var got = await _admin.GetFromJsonAsync<JsonElement>(Url, Ct);
        Assert.Equal(("yes", "Hello", "3", "true"), (got.GetProperty("values").GetProperty("Must").GetString(), got.GetProperty("values").GetProperty("Greeting").GetString(),
            got.GetProperty("values").GetProperty("Count").GetString(), got.GetProperty("values").GetProperty("On").GetString()));
        Assert.Empty(got.GetProperty("secretsSet").EnumerateArray());

        // Leaving a value out keeps it; sending it empty clears it.
        await SaveAsync(new { Must = "yes", Greeting = "" });
        got = await _admin.GetFromJsonAsync<JsonElement>(Url, Ct);
        Assert.False(got.GetProperty("values").TryGetProperty("Greeting", out _));
        Assert.Equal("3", got.GetProperty("values").GetProperty("Count").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/v1/admin/plugins/kb.nothing/settings", Ct)).StatusCode);
    }

    [Fact]
    public async Task Multiline_ReachesThePlugin_WithLfLineEnds()
    {
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(new { Must = "yes", Hosts = "a.example.com\r\nb.example.com\rc.example.com" })).StatusCode);
        Assert.Equal("a.example.com\nb.example.com\nc.example.com", Store().Get("kb.test.probe", "Hosts"));
    }

    [Fact]
    public async Task Secret_IsNeverReturned_AndNotStoredAsPlainText()
    {
        var put = await SaveAsync(new { Must = "yes", Token = Secret });
        var get = await _admin.GetStringAsync(Url, Ct);
        Assert.DoesNotContain(Secret, await put.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(Secret, get);
        Assert.DoesNotContain(Secret, await _admin.GetStringAsync("/api/v1/admin/plugins", Ct));
        Assert.Equal("Token", JsonDocument.Parse(get).RootElement.GetProperty("secretsSet").EnumerateArray().Single().GetString());

        await _f.WithDbAsync(async db =>
        {
            var row = await db.PluginSettingValues.SingleAsync(v => v.Key == "Token", Ct);
            Assert.True(row.Secret);
            Assert.DoesNotContain(Secret, row.Value);
        });
        Assert.Equal(Secret, await Store().GetSecretAsync("kb.test.probe", "Token", Ct)); // the plugin itself can read it
        Assert.Null(Store().Get("kb.test.probe", "Token"));                               // and only as a secret
    }

    [Fact]
    public async Task Secret_NullKeeps_EmptyClears()
    {
        await SaveAsync(new { Must = "yes", Token = Secret });
        await SaveAsync(new { Must = "yes", Token = (string?)null });
        Assert.Equal(Secret, await Store().GetSecretAsync("kb.test.probe", "Token", Ct));
        await SaveAsync(new { Must = "yes", Token = "" });
        Assert.Null(await Store().GetSecretAsync("kb.test.probe", "Token", Ct));
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("secretsSet").EnumerateArray());
    }

    [Fact]
    public async Task Setting_AppliesWithoutRestart()
    {
        await using var second = _f.SecondInstance();
        await second.SignedInBreakGlassAsync();
        Assert.Null(Store(second).Get("kb.test.probe", "Greeting"));

        await SaveAsync(new { Must = "yes", Greeting = "Hello" });
        Assert.Equal("Hello", Store().Get("kb.test.probe", "Greeting")); // on this server at once
        Assert.Empty(_f.Restarter.Reasons);

        for (var waited = 0; Store(second).Get("kb.test.probe", "Greeting") is null && waited < 6000; waited += 100) await Task.Delay(100, Ct);
        Assert.Equal("Hello", Store(second).Get("kb.test.probe", "Greeting"));
        Assert.Empty(second.Restarter.Reasons);
    }

    [Fact]
    public async Task Setting_MarkedRestart_AsksForRestart()
    {
        var r = await SaveAsync(new { Must = "yes", Mode = "fast" });
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("restart").GetBoolean());
        Assert.Single(_f.Restarter.Reasons);

        r = await SaveAsync(new { Must = "yes", Mode = "fast", Greeting = "Hi" }); // Mode did not change
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("restart").GetBoolean());
        Assert.Single(_f.Restarter.Reasons);
    }

    [Fact]
    public async Task Settings_Invalid_400_NamesTheField()
    {
        async Task<(string Field, string Message)> FieldAsync(object values)
        {
            var r = await SaveAsync(values);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            var error = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateObject().Single();
            return (error.Name, error.Value[0].GetString()!);
        }

        Assert.Equal(("Must", "Required."), await FieldAsync(new { Greeting = "Hello" }));
        Assert.Equal(("Nope", "Unknown setting."), await FieldAsync(new { Must = "yes", Nope = "x" }));
        Assert.Equal(("Count", "A number."), await FieldAsync(new { Must = "yes", Count = "three" }));
        Assert.Equal(("FailOn", "Not one of the choices."), await FieldAsync(new { Must = "yes", FailOn = "banana" }));
        Assert.Equal(("On", "true or false."), await FieldAsync(new { Must = "yes", On = "maybe" }));
        await _f.WithDbAsync(async db => Assert.False(await db.PluginSettingValues.AnyAsync(Ct))); // nothing of a refused save is kept
    }

    [Fact]
    public async Task Settings_Change_IsAudited_WithoutValues()
    {
        await SaveAsync(new { Must = "yes", Greeting = "Hello there", Token = Secret });
        await SaveAsync(new { Must = "yes", Greeting = "Hello there" }); // nothing changed: no entry
        await _f.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.PluginSettingsChange, Ct);
            Assert.Equal("kb.test.probe", entry.TargetId);
            Assert.Equal("Greeting, Must, Token", JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DataJson)!["keys"]);
            Assert.DoesNotContain("Hello there", entry.DataJson);
            Assert.DoesNotContain(Secret, entry.DataJson);
        });
    }

    [Fact]
    public async Task Settings_NotAdmin_403()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<Kb.Core.Identity.KbUser>>().CreateAsync(new() { UserName = "tech", DisplayName = "Tech", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        var user = _f.NewClient();
        (await user.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "tech", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(new { Must = "yes" }, user)).StatusCode);
    }

    [Fact]
    public async Task ShippedManifests_HaveNoHintText_AndTypedSettings()
    {
        var plugins = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/plugins", Ct)).GetProperty("plugins").EnumerateArray().ToList();
        var smtp = plugins.Single(p => p.GetProperty("id").GetString() == "kb.email.smtp").GetProperty("settings").EnumerateArray()
            .ToDictionary(s => s.GetProperty("key").GetString()!, s => (s.GetProperty("label").GetString(), s.GetProperty("type").GetString()));
        Assert.Equal(("Port", "number"), smtp["Port"]);
        Assert.Equal(("User name", "text"), smtp["UserName"]);
        Assert.Equal(("Use STARTTLS", "toggle"), smtp["StartTls"]);
        Assert.Equal(("Password", "secret"), smtp["Password"]);
        Assert.All(plugins.SelectMany(p => p.GetProperty("settings").EnumerateArray()), s => Assert.DoesNotContain("(", s.GetProperty("label").GetString()));
    }

    // Finding 8: a value stored in clear for a setting an update made secret is treated as the secret it now is.
    [Fact]
    public async Task ValueStoredInClear_ForASettingThatIsNowSecret_IsNotReturned_AndIsProtectedOnSave()
    {
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Token", Value = Secret, Secret = false, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        var get = await _admin.GetStringAsync(Url, Ct);
        Assert.DoesNotContain(Secret, get);
        Assert.Equal("Token", JsonDocument.Parse(get).RootElement.GetProperty("secretsSet").EnumerateArray().Single().GetString());
        Assert.Equal(Secret, await Store().GetSecretAsync("kb.test.probe", "Token", Ct));

        await SaveAsync(new { Must = "yes", Token = Secret }); // the same value again: it is stored protected now
        await _f.WithDbAsync(async db =>
        {
            var row = await db.PluginSettingValues.SingleAsync(v => v.Key == "Token", Ct);
            Assert.True(row.Secret);
            Assert.DoesNotContain(Secret, row.Value);
        });
        Assert.Equal(Secret, await Store().GetSecretAsync("kb.test.probe", "Token", Ct));
    }

    // Such a value does not wait for an Admin to save it again: the app protects it when it starts.
    [Fact]
    public async Task ValueStoredInClear_ForASettingThatIsNowSecret_IsProtectedAtTheNextStart()
    {
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Token", Value = Secret, Secret = false, UpdatedAt = DateTimeOffset.UtcNow });
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Greeting", Value = "Hello", Secret = false, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();

        await _f.WithDbAsync(async db =>
        {
            var row = await db.PluginSettingValues.SingleAsync(v => v.Key == "Token", Ct);
            Assert.True(row.Secret);
            Assert.DoesNotContain(Secret, row.Value);
            Assert.Equal(("Hello", false), ((await db.PluginSettingValues.SingleAsync(v => v.Key == "Greeting", Ct)) is var g ? (g.Value, g.Secret) : default)); // a plain setting stays plain
        });
        Assert.Equal(Secret, await Store().GetSecretAsync("kb.test.probe", "Token", Ct));
        Assert.Null(Store().Get("kb.test.probe", "Token")); // not among the plain values a plugin reads without asking for a secret
    }
}
