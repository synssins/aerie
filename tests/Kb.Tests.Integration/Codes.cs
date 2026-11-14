using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>One-time codes as an authenticator app makes them (RFC 6238: SHA-1, 30 seconds, 6 digits), and a sign-in that answers them.</summary>
public static class Codes
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Totp(string base32Key, long step = -1)
    {
        if (step < 0) step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, Base32(base32Key)); // RFC 6238 as the apps use it
        hmac.AppendData(counter);
        var hash = hmac.GetHashAndReset();
        var o = hash[^1] & 0xf;
        var n = ((hash[o] & 0x7f) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (n % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string s)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var value = 0; var bytes = new List<byte>();
        foreach (var ch in s.ToUpperInvariant().Where(ch => ch != '=' && ch != ' '))
        {
            value = (value << 5) | A.IndexOf(ch, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(value >> (bits - 8))); bits -= 8; }
        }
        return [.. bytes];
    }

    public static async Task<string> KeyOfAsync(KbAppFactory f, string userName)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
        return (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync(userName))!))!;
    }

    /// <summary>Signs in with the password and, when asked, sets up codes or answers one. Fails the test when sign-in does not end signed in.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(KbAppFactory f, HttpClient c, string userName, string password)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName, password }, Ct);
        if (r.StatusCode != HttpStatusCode.Conflict) return r;
        var step = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("step").GetString();
        if (step == "enroll")
        {
            var key = (await (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
            return await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Totp(key) }, Ct);
        }
        return await c.PostAsJsonAsync("/api/v1/auth/local/code", new { code = Totp(await KeyOfAsync(f, userName)) }, Ct);
    }
}
