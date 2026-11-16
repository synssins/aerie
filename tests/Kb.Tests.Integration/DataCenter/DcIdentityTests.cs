using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 1: how a request on the compatible surface becomes a caller (contracts/behaviour/identity.md, identity-plan.md).</summary>
public sealed class DcIdentityTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A personal token of an account, written straight to the store, with the given expiry and revocation.</summary>
    private async Task<string> TokenAsync(string owner, DateTimeOffset? expires = null, bool revoked = false)
    {
        var text = TokenFormat.Generate();
        await _f.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.UserName == owner, Ct);
            db.ApiTokens.Add(new ApiToken
            {
                OwnerUserId = user.Id, Name = "Id " + Guid.NewGuid().ToString("N")[..6], Kind = TokenKind.Personal, Prefix = text[..TokenFormat.PrefixShown], Hash = TokenFormat.Hash(text),
                CreatedBy = user.Id, ScopesJson = JsonSerializer.Serialize(new[] { new TokenScope(null, TokenAccess.Write) }, Web),
                CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddDays(30), RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
            });
            await db.SaveChangesAsync(Ct);
        });
        return text;
    }

    private HttpClient With(string scheme, string? value)
    {
        var c = _f.NewClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", value is null ? scheme : $"{scheme} {value}");
        return c;
    }

    // auth.header.malformed: a header aerie cannot use is 401, never anonymous, even where anonymous callers are let in.
    [Theory]
    [InlineData("Bearer", null)]
    [InlineData("Digest", "username=\"ravi\"")]
    [InlineData("Basic", "!!not-base64!!")]
    [InlineData("Basic", "bm9jb2xvbg==")] // "nocolon"
    [InlineData("Negotiate", "abc")]
    public async Task UnusableAuthorization_Is401_NeverAnonymous(string scheme, string? value)
    {
        var r = await With(scheme, value).GetAsync("/rest/api/user/anonymous", Ct);
        await r.ErrorAsync(HttpStatusCode.Unauthorized);
        Assert.DoesNotContain(r.Headers.WwwAuthenticate, h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
    }

    // auth.pat.bearer.ok / revoked / expired, and the RFC 6750 answer for a token aerie does not accept.
    [Fact]
    public async Task Bearer_Ok_Revoked_Expired_Unknown()
    {
        await UserAsync(_f, "ravi");
        Assert.Equal("ravi", (await With("Bearer", await TokenAsync("ravi")).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        string[] refused = [await TokenAsync("ravi", revoked: true), await TokenAsync("ravi", expires: DateTimeOffset.UtcNow.AddMinutes(-1)), TokenFormat.Generate(), "kbt_short"];
        foreach (var token in refused)
        {
            var r = await With("Bearer", token).GetAsync("/rest/api/user/current", Ct);
            await r.ErrorAsync(HttpStatusCode.Unauthorized);
            Assert.Equal("Bearer error=\"invalid_token\"", r.Headers.WwwAuthenticate.ToString());
        }
        // The two that exist are audited by id and prefix only; the token text is nowhere in the log.
        await _f.WithDbAsync(async db =>
        {
            var denied = await db.AuditEntries.Where(a => a.Action == AuditActions.TokenUseDenied).ToListAsync(Ct);
            Assert.Equal(["expired", "revoked"], denied.Select(a => JsonDocument.Parse(a.DataJson).RootElement.GetProperty("reason").GetString()).Order());
            var log = string.Join('|', await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct));
            Assert.All(refused, t => Assert.DoesNotContain(t, log, StringComparison.Ordinal));
        });
    }

    // auth.no_cookie_set: Basic and Bearer callers get no session.
    [Fact]
    public async Task BasicAndBearer_SetNoCookie()
    {
        await UserAsync(_f, "ravi");
        foreach (var c in new[] { Basic(_f, "ravi"), With("Bearer", await TokenAsync("ravi")) })
        {
            var r = await c.GetAsync("/rest/api/user/current", Ct);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.False(r.Headers.Contains("Set-Cookie"));
            var w = await c.PostJsonAsync("/rest/api/space", new { key = "N" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), name = "N" });
            Assert.False(w.Headers.Contains("Set-Cookie"));
        }
    }

    // auth.cookie.write_needs_xsrf: a cookie caller's write needs X-Atlassian-Token whatever the body; Basic and Bearer callers do not.
    [Fact]
    public async Task CookieWrites_NeedTheXsrfHeader_WhateverTheBody()
    {
        var session = await _f.SignedInBreakGlassAsync();
        session.DefaultRequestHeaders.Remove("X-Atlassian-Token");
        await (await session.PostJsonAsync("/rest/api/space", new { key = "XS", name = "X" })).ErrorAsync(HttpStatusCode.Forbidden);
        await (await session.DeleteAsync("/rest/api/space/XS", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/rest/api/space", Ct)).StatusCode); // reads need nothing
        await _f.WithDbAsync(async db => Assert.False(await db.Spaces.AnyAsync(s => s.Key == "XS", Ct)));
        foreach (var value in new[] { "no-check", "nocheck" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/rest/api/space") { Content = JsonContent.Create(new { key = "X" + value.Length, name = "X" }) };
            req.Headers.Add("X-Atlassian-Token", value);
            Assert.Equal(HttpStatusCode.OK, (await session.SendAsync(req, Ct)).StatusCode);
        }
        await UserAsync(_f, "ravi");
        // Basic callers are not held to it: whatever ravi may or may not do, the answer is not the XSRF refusal.
        Assert.DoesNotContain("X-Atlassian-Token", await (await Basic(_f, "ravi").PostJsonAsync("/rest/api/space", new { key = "BX", name = "B" })).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // Password Basic can be turned off for everyone; tokens in Basic keep working.
    [Fact]
    public async Task PasswordBasic_CanBeTurnedOff_TokenBasicStays()
    {
        var id = await UserAsync(_f, "ravi");
        var token = await TokenAsync("ravi");
        await _f.WithDbAsync(async db =>
        {
            db.RuntimeSettings.Add(new RuntimeSetting { Key = RuntimeSettingKeys.CompatBasicPasswordEnabled, Value = "false", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        var right = await Basic(_f, "ravi").GetAsync("/rest/api/user/current", Ct);
        var wrong = await Basic(_f, "ravi", "not-the-password").GetAsync("/rest/api/user/current", Ct);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await right.Content.ReadAsStringAsync(Ct));
        await right.ErrorAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("ravi", (await Basic(_f, "ravi", token).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BasicRefused && a.ActorId == id && a.DataJson.Contains("password Basic turned off"), Ct)));
    }

    // auth.basic.lockout, per account: a locked account gets 429 with Retry-After and its password is not checked.
    [Fact]
    public async Task LockedAccount_Gets429_AndThePasswordIsNotChecked()
    {
        var id = await UserAsync(_f, "ravi");
        for (var i = 0; i < 5; i++) await (await Basic(_f, "ravi", "wrong-" + i).GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        var r = await Basic(_f, "ravi").GetAsync("/rest/api/user/current", Ct);
        await r.ErrorAsync(HttpStatusCode.TooManyRequests);
        Assert.True(r.Headers.RetryAfter?.Delta is { TotalSeconds: > 60 and <= 15 * 60 }, r.Headers.RetryAfter?.ToString());
        await _f.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BasicRefused && a.ActorId == id && a.DataJson.Contains("locked out"), Ct));
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BasicUse && a.ActorId == id, Ct));
        });
    }

    // auth.basic.token_in_password.ok for an account with codes on, and .wrong_user.
    [Fact]
    public async Task TokenInBasic_WorksWithCodesOn_AndNeedsTheOwnersName()
    {
        await UserAsync(_f, "coded", u => u.CodesRequired = true);
        await UserAsync(_f, "other");
        var token = await TokenAsync("coded");
        Assert.Equal("coded", (await Basic(_f, "CODED", token).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        await (await Basic(_f, "other", token).GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
    }

    // The first credential decides: an Authorization header wins over a session cookie, and a bad one is not rescued by it.
    [Fact]
    public async Task AuthorizationHeader_WinsOverTheCookie()
    {
        var session = await _f.SignedInBreakGlassAsync();
        session.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + TokenFormat.Generate());
        await (await session.GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        _ = _admin;
    }
}
