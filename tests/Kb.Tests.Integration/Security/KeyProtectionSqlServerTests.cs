using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The key rewrite (M3) and the authenticator-key rewrite (I3) on SQL Server, which the other tests run on SQLite only. It needs a SQL
/// Server instance, so it runs only when KB_TEST_SQLSERVER names one (for example ".\SQLEXPRESS", Windows authentication). It makes a
/// database of its own with a unique name that starts with KbKeyTest_ and drops it at the end; it touches no other database.
/// </summary>
public sealed class KeyProtectionSqlServerTests
{
    private const string Password = "user-password-0123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnSqlServer_KeysAndAuthenticatorKeys_AreEncryptedInPlace_AndStillWork_AcrossStarts()
    {
        var server = Environment.GetEnvironmentVariable("KB_TEST_SQLSERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(server), "Set KB_TEST_SQLSERVER to a SQL Server instance to run this.");
        var name = "KbKeyTest_" + Guid.NewGuid().ToString("N");
        string Connection(string database) => $"Server={server};Database={database};Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";
        var path = KbAppFactory.NewDbPath(); // only the folders beside it are used (logs, setup code, plugins)
        KbAppFactory App(bool secret)
        {
            var settings = new Dictionary<string, string> { ["Kb:Database:Provider"] = "SqlServer", ["Kb:Database:ConnectionString"] = Connection(name) };
            if (secret) settings[KeyProtection.SecretName] = "a long secret kept outside the database";
            return new KbAppFactory(path, settings: settings);
        }
        async Task<List<string?>> CodesAsync(KbAppFactory f)
        {
            var values = new List<string?>();
            await f.WithDbAsync(async db => values = await db.UserTokens.Where(t => t.LoginProvider == KbUserStore.Internal).Select(t => t.Value).ToListAsync(Ct));
            return values;
        }
        async Task<string> KeysAsync(KbAppFactory f)
        {
            var xml = "";
            await f.WithDbAsync(async db => xml = string.Concat(await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync(Ct)));
            return xml;
        }

        try
        {
            string sealedText, key;
            // 1. As an install is before the secret: keys and codes in clear.
            await using (var before = App(secret: false))
            {
                await before.SignedInBreakGlassAsync();
                await before.WithDbAsync(async db => Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName));
                await using (var scope = before.Services.CreateAsyncScope())
                    Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(new KbUser { UserName = "gus", DisplayName = "gus", CreatedAt = DateTimeOffset.UtcNow }, Password)).Succeeded);
                var c = before.NewClient();
                (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "gus", password = Password }, Ct)).EnsureSuccessStatusCode();
                key = (await (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).StatusCode);
                sealedText = before.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("the stored secret");
                Assert.Contains("<masterKey", await KeysAsync(before), StringComparison.Ordinal);
                Assert.Contains(key, await CodesAsync(before));
            }

            // 2. and 3. Two starts with the secret: wrapped at the first, read at both, and a sign-in with a code works at both.
            for (var start = 1; start <= 2; start++)
            {
                await using var with = App(secret: true);
                await with.SignedInBreakGlassAsync();
                var keys = await KeysAsync(with);
                Assert.DoesNotContain("<masterKey", keys, StringComparison.Ordinal);
                Assert.Contains(typeof(KeyProtection.Decryptor).FullName!, keys, StringComparison.Ordinal);
                Assert.All(await CodesAsync(with), v => Assert.StartsWith("kbp1:", v, StringComparison.Ordinal));
                Assert.Equal("the stored secret", with.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(sealedText));
                (await Codes.SignInAsync(with, with.NewClient(), "gus", Password)).EnsureSuccessStatusCode();
            }

            // 4. Without the secret the app does not start on this database.
            await using var without = App(secret: false);
            var e = await Assert.ThrowsAnyAsync<Exception>(() => without.NewClient().GetAsync("/health/live", Ct));
            Assert.Contains(KeyProtection.SecretName, e.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            // Only the database this test made, by the name it made up.
            Assert.StartsWith("KbKeyTest_", name, StringComparison.Ordinal);
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(Connection("master"));
            await master.OpenAsync(CancellationToken.None);
            await using var drop = master.CreateCommand();
#pragma warning disable CA2100 // the name is made in this method: a fixed prefix and a GUID
            drop.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
#pragma warning restore CA2100
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
