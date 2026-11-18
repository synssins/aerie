using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings M3 and M9 of the code review of 2026-11-17, and the rest of M11: the keys in the database are stored encrypted once a secret
/// is set in configuration; an audit entry of a sensitive space leaves the database without the space's own words; a service token that
/// names a sensitive space and was not made by someone who manages it ends at the next start.
/// </summary>
public sealed class KeysAndLogsTests
{
    private const string Password = "user-password-0123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Dictionary<string, string> Secret(string value) => new() { [KeyProtection.SecretName] = value };

    // ---- M3 ----

    [Fact]
    public async Task Keys_AreStoredEncrypted_OnceTheSecretIsSet_AndStillOpenWhatTheyProtected()
    {
        var path = KbAppFactory.NewDbPath();
        string sealedText;
        await using (var before = new KbAppFactory(path))
        {
            await before.SignedInBreakGlassAsync();
            sealedText = before.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("the stored secret");
            await before.WithDbAsync(async db => Assert.Contains("requiresEncryption", (await db.DataProtectionKeys.SingleAsync(Ct)).Xml, StringComparison.Ordinal));
        }

        await using (var with = new KbAppFactory(path, settings: Secret("a long secret kept outside the database")))
        {
            await with.SignedInBreakGlassAsync();
            // The key that was in clear is wrapped in place, and what it protected is still read.
            await with.WithDbAsync(async db =>
            {
                var xml = string.Concat(await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync(Ct));
                Assert.DoesNotContain("requiresEncryption", xml, StringComparison.Ordinal);
                Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
                Assert.Contains(typeof(KeyProtection.Decryptor).FullName!, xml, StringComparison.Ordinal);
            });
            Assert.Equal("the stored secret", with.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(sealedText));
        }

        // Another secret opens nothing, and says so instead of failing somewhere else.
        await using (var wrong = new KbAppFactory(path, settings: Secret("another secret")))
            Assert.ThrowsAny<CryptographicException>(() => wrong.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(sealedText));

        // Without the secret the app does not start on a new key that reads nothing: it stops and names the setting.
        await using var without = new KbAppFactory(path);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => without.NewClient().GetAsync("/health/live", Ct));
        Assert.Contains(KeyProtection.SecretName, e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_MadeWithTheSecretSet_AreEncryptedFromTheStart()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: Secret("a long secret kept outside the database"));
        await f.SignedInBreakGlassAsync();
        await f.WithDbAsync(async db =>
        {
            var xml = string.Concat(await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync(Ct));
            Assert.NotEmpty(xml);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        });
    }

    // ---- I3 ----

    [Fact]
    public async Task AuthenticatorKeysAndRecoveryCodes_AreStoredEncrypted_OnceTheSecretIsSet_AndStillWork()
    {
        var path = KbAppFactory.NewDbPath();
        async Task<List<string?>> StoredAsync(KbAppFactory f)
        {
            var values = new List<string?>();
            await f.WithDbAsync(async db => values = await db.UserTokens.Where(t => t.LoginProvider == KbUserStore.Internal).Select(t => t.Value).ToListAsync(Ct));
            return values;
        }
        string key;
        string[] recovery;
        await using (var before = new KbAppFactory(path))
        {
            await using (var scope = before.Services.CreateAsyncScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(new KbUser { UserName = "gus", DisplayName = "gus", CreatedAt = DateTimeOffset.UtcNow }, Password)).Succeeded);
            var c = before.NewClient();
            (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "gus", password = Password }, Ct)).EnsureSuccessStatusCode();
            key = (await (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
            var enrolled = await (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            recovery = [.. enrolled.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!)];
            Assert.Contains(key, await StoredAsync(before)); // as it always was: readable in the table
        }

        await using var with = new KbAppFactory(path, settings: Secret("a long secret kept outside the database"));
        await with.SignedInBreakGlassAsync();
        var stored = await StoredAsync(with);
        Assert.NotEmpty(stored);
        Assert.All(stored, v => Assert.StartsWith("kbp1:", v, StringComparison.Ordinal));
        Assert.DoesNotContain(stored, v => v!.Contains(key, StringComparison.Ordinal) || v.Contains(recovery[0], StringComparison.Ordinal));

        // The same key and the same recovery codes still sign the person in.
        (await Codes.SignInAsync(with, with.NewClient(), "gus", Password)).EnsureSuccessStatusCode();
        var other = with.NewClient();
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "gus", password = Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/v1/auth/local/code", new { code = recovery[0] }, Ct)).StatusCode);
        Assert.All(await StoredAsync(with), v => Assert.StartsWith("kbp1:", v, StringComparison.Ordinal)); // the used code was taken out of an encrypted list
    }

    // ---- M9 ----

    [Fact]
    public async Task LogShipping_SendsNoWordsOfASensitiveSpace()
    {
        var path = KbAppFactory.NewDbPath();
        // The background shipper is taken out, so the test decides when entries leave.
        await using var f = new KbAppFactory(path, services: s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList()) s.Remove(d);
        });
        var admin = await f.SignedInBreakGlassAsync();
        async Task<Guid> SpaceAsync(string name) => (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        const string doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x"}]}]}""";
        var (payroll, desk) = (await SpaceAsync("Payroll"), await SpaceAsync("Desk"));
        // Deleting a page writes its title into the audit entry.
        foreach (var (space, title) in new[] { (payroll, "Salary bands 2027"), (desk, "Printer queue") })
        {
            var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = doc }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            (await admin.DeleteAsync($"/api/v1/pages/{page}", Ct)).EnsureSuccessStatusCode();
        }
        (await admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{payroll}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();

        Assert.True(await f.Services.GetRequiredService<LogShipperService>().RunOnceAsync(Ct) > 0);

        var lines = Directory.GetFiles(path + ".logs", "*", SearchOption.AllDirectories).SelectMany(File.ReadAllLines).ToList();
        var shipped = string.Join('\n', lines);
        Assert.DoesNotContain("Salary bands", shipped, StringComparison.Ordinal);
        Assert.Contains("Printer queue", shipped, StringComparison.Ordinal); // a normal space is shipped whole
        // The entries of the sensitive space are still there: that something happened, where, and by whom.
        Assert.Contains(lines, l => l.Contains(payroll.ToString(), StringComparison.OrdinalIgnoreCase) && l.Contains("page.delete", StringComparison.Ordinal));
    }

    // ---- M11, the tokens from before ----

    [Fact]
    public async Task ServiceToken_NamingASensitiveSpace_NotMadeByItsManager_EndsAtTheFirstStartWithTheRule_AndOnlyThen()
    {
        var path = KbAppFactory.NewDbPath();
        HttpClient Bearer(KbAppFactory on, string token)
        {
            var c = on.NewClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }
        string strayToken, keptToken;
        Guid vault, mine;
        await using (var f = new KbAppFactory(path))
        {
            var admin = await f.SignedInBreakGlassAsync();
            async Task<Guid> SpaceAsync(string name) => (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            async Task<string> TokenAsync(string name, Guid space) => (await (await admin.PostAsJsonAsync("/api/v1/tokens",
                new { name, kind = "Service", scopes = new[] { new { spaceId = space, access = "Read" } }, days = 30 }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
            (vault, mine) = (await SpaceAsync("Vault"), await SpaceAsync("Mine"));
            var me = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
            (await admin.PutAsJsonAsync($"/api/v1/spaces/{mine}/roles", new { principalType = "User", principalId = me.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();
            (strayToken, keptToken) = (await TokenAsync("Old sync", vault), await TokenAsync("My sync", mine));
            Assert.Equal(HttpStatusCode.OK, (await Bearer(f, strayToken).GetAsync($"/api/v1/spaces/{vault}", Ct)).StatusCode);
            // Both spaces are sensitive from here on, as on a site where they were marked before this rule existed.
            await f.WithDbAsync(async db =>
            {
                foreach (var s in await db.Spaces.Where(s => s.Id == vault || s.Id == mine).ToListAsync(Ct)) s.IsSensitive = true;
                // A database from before the update has no record that the clean-up ran.
                db.RuntimeSettings.RemoveRange(db.RuntimeSettings.Where(s => s.Key == ServiceTokenRules.DoneKey));
                await db.SaveChangesAsync(Ct);
            });
        }

        await using (var restarted = new KbAppFactory(path))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(restarted, strayToken).GetAsync($"/api/v1/spaces/{vault}", Ct)).StatusCode);
            // The one its maker manages is kept.
            Assert.Equal(HttpStatusCode.OK, (await Bearer(restarted, keptToken).GetAsync($"/api/v1/spaces/{mine}", Ct)).StatusCode);
            // Later its maker loses the role in the space. The token was made rightly and is not for a later start to end.
            await restarted.WithDbAsync(async db =>
            {
                db.SpaceRoleAssignments.RemoveRange(db.SpaceRoleAssignments.Where(a => a.SpaceId == mine));
                await db.SaveChangesAsync(Ct);
            });
        }

        await using var again = new KbAppFactory(path);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(again, keptToken).GetAsync($"/api/v1/spaces/{mine}", Ct)).StatusCode);
        await again.WithDbAsync(async db => Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == Kb.Core.Audit.AuditActions.TokenRevoke, Ct)));
    }
}
