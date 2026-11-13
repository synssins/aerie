using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>Sign-in, sign-out, the caller's own record and settings, and first-run setup go through the action bus.</summary>
public sealed class AuthPipelineTests : IAsyncLifetime
{
    private const string Pw = "first-admin-password-123";
    private const string SignInUrl = "/api/v1/auth/local/signin";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static object Settings(string theme) => new { theme, width = "boxed", startPage = "last", codeLanguage = "plaintext" };
    private static (HttpStatusCode, string?) Answer(HttpResponseMessage r) => (r.StatusCode, r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null);

    [Fact]
    public async Task EveryAuthRoute_AnswersWithItsActionName()
    {
        var c = _f.NewClient();
        Assert.Equal((HttpStatusCode.OK, "setup.status"), Answer(await c.GetAsync("/api/v1/setup/status", Ct)));
        // A refusal by the core names the action too.
        Assert.Equal((HttpStatusCode.Forbidden, "setup.run"), Answer(await c.PostAsJsonAsync("/api/v1/setup", new { userName = "admin", displayName = "First Admin", password = Pw }, Ct)));
        var code = (await File.ReadAllTextAsync(_f.SetupCodeFile, Ct)).Trim();
        Assert.Equal((HttpStatusCode.OK, "setup.run"), Answer(await c.PostAsJsonAsync("/api/v1/setup", new { userName = "admin", displayName = "First Admin", password = Pw, code }, Ct)));

        // Setup signed the new admin in.
        Assert.Equal((HttpStatusCode.OK, "me.read"), Answer(await c.GetAsync("/api/v1/me", Ct)));
        Assert.Equal((HttpStatusCode.OK, "me.settings.read"), Answer(await c.GetAsync("/api/v1/me/settings", Ct)));
        Assert.Equal((HttpStatusCode.OK, "me.settings.save"), Answer(await c.PutAsJsonAsync("/api/v1/me/settings", Settings("dark"), Ct)));
        Assert.Equal((HttpStatusCode.NoContent, "auth.signout"), Answer(await c.PostAsync("/api/v1/auth/signout", null, Ct)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);

        Assert.Equal((HttpStatusCode.Unauthorized, "auth.signin"), Answer(await c.PostAsJsonAsync(SignInUrl, new { userName = "admin", password = "nope" }, Ct)));
        var signedIn = await c.PostAsJsonAsync(SignInUrl, new { userName = "admin", password = Pw }, Ct);
        Assert.Equal((HttpStatusCode.OK, "auth.signin"), Answer(signedIn));
        Assert.Equal("First Admin", (await signedIn.Content.ReadFromJsonAsync<MeResponse>(Ct))!.DisplayName);
    }

    [Fact]
    public async Task SaveSettings_RefusedByHook_Is422_SettingsUnchanged()
    {
        await SetAsync("Refuse", "me.settings.save");

        var r = await _admin.PutAsJsonAsync("/api/v1/me/settings", Settings("dark"), Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal("system", (await _admin.GetFromJsonAsync<UserSettings>("/api/v1/me/settings", Ct))!.Theme);
    }

    [Fact]
    public async Task Me_FilterTrimsTheResult()
    {
        await SetAsync("Blank", "me.read:DisplayName");

        var me = await _admin.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);

        Assert.Equal(("breakglass", ""), (me!.UserName, me.DisplayName));
    }

    /// <summary>A plugin's refusal comes before the account is looked up, so it does not tell a real user name from a made-up one.</summary>
    [Fact]
    public async Task SignIn_RefusedByHook_Is422_ForAKnownAndAnUnknownUser_AndSignsNobodyIn()
    {
        await SetAsync("Refuse", "auth.signin");
        var c = _f.NewClient();

        var known = await c.PostAsJsonAsync(SignInUrl, new { userName = "breakglass", password = KbAppFactory.BreakGlassPassword }, Ct);
        var unknown = await c.PostAsJsonAsync(SignInUrl, new { userName = "nobody-here", password = "x" }, Ct);

        Assert.Equal(((HttpStatusCode)422, (HttpStatusCode)422), (known.StatusCode, unknown.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public void PasswordAndSetupCode_AreNotSerialized()
    {
        var json = JsonSerializer.Serialize(new SignIn { UserName = "u", Password = "pw-secret" })
            + JsonSerializer.Serialize(new RunSetup { UserName = "u", DisplayName = "U", Password = "pw-secret", Code = "code-secret" });

        Assert.DoesNotContain("secret", json);
    }
}
