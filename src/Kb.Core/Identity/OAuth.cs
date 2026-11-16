using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Kb.Core.Identity;

/// <summary>
/// OAuth 2.0 on the Data Center compatible surface (contracts/behaviour/identity.md, OAuth 2.0). Aerie is the authorization server for
/// clients an administrator registered. Secrets, codes and tokens are stored only as hashes.
/// </summary>
public static class OAuthScopes
{
    public const string Read = "READ", Write = "WRITE", Admin = "ADMIN", SystemAdmin = "SYSTEM_ADMIN";

    /// <summary>The scopes a client may be registered with and ask for, in the order of the implications table.</summary>
    public static readonly IReadOnlyList<string> All = [Read, Write, Admin, SystemAdmin];

    /// <summary>A scope list as sent: names separated by spaces, order and duplicates ignored. Null when a name is not one of <see cref="All"/>.</summary>
    public static SortedSet<string>? Parse(string? text)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!All.Contains(name, StringComparer.Ordinal)) return null;
            set.Add(name);
        }
        return set;
    }

    /// <summary>The scopes the named ones imply (identity.md, Scopes): WRITE implies READ, ADMIN implies WRITE, SYSTEM_ADMIN implies ADMIN.</summary>
    public static HashSet<string> Expand(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var rank = names.Select(n => All.ToList().IndexOf(n)).DefaultIfEmpty(-1).Max();
        return new HashSet<string>(All.Take(rank + 1), StringComparer.Ordinal);
    }

    /// <summary>Space-separated, in a fixed order: how scope lists are stored and sent.</summary>
    public static string Format(IEnumerable<string> names) => string.Join(' ', All.Where(names.Contains));
}

/// <summary>Values aerie makes for OAuth: client ids and secrets, codes and tokens. All from the cryptographic random source.</summary>
public static class OAuthSecrets
{
    public const string AccessPrefix = "kbo_", RefreshPrefix = "kbr_";
    public const int PrefixShown = 12;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>32 characters from [A-Za-z0-9].</summary>
    public static string NewClientId() => Letters(32);

    /// <summary>64 characters from [A-Za-z0-9] (about 381 bits).</summary>
    public static string NewClientSecret() => Letters(64);

    /// <summary>An authorization code: 43 base-62 characters, 256 bits.</summary>
    public static string NewCode() => Base62(RandomNumberGenerator.GetBytes(32));

    public static string NewAccessToken() => AccessPrefix + Base62(RandomNumberGenerator.GetBytes(32));

    public static string NewRefreshToken() => RefreshPrefix + Base62(RandomNumberGenerator.GetBytes(32));

    public static bool IsAccessToken(string? text) => text is { Length: 47 } && text.StartsWith(AccessPrefix, StringComparison.Ordinal);

    public static bool IsRefreshToken(string? text) => text is { Length: 47 } && text.StartsWith(RefreshPrefix, StringComparison.Ordinal);

    /// <summary>SHA-256 of a code or token: they are high-entropy, so a fast hash is enough. Client secrets use the password hasher.</summary>
    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>Constant-time comparison of two strings (PKCE values, secrets in transit).</summary>
    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>BASE64URL(SHA256(verifier)) without padding (RFC 7636 section 4.2).</summary>
    public static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Letters(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    private static string Base62(byte[] bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true);
        var chars = new char[43];
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var digit);
            chars[i] = Alphabet[(int)digit];
        }
        return new string(chars);
    }
}

/// <summary>An application an administrator registered (identity.md, Clients).</summary>
public sealed class OAuthClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The public identifier: 32 characters from [A-Za-z0-9].</summary>
    public required string ClientId { get; set; }
    /// <summary>The secret, hashed with the password hasher. Null for a public client.</summary>
    public string? SecretHash { get; set; }
    public required string Name { get; set; }
    /// <summary>"confidential" or "public".</summary>
    public required string Type { get; set; }
    /// <summary>The registered redirect URIs, a JSON list of strings.</summary>
    public string RedirectUrisJson { get; set; } = "[]";
    /// <summary>The scopes the client may ask for, space-separated (<see cref="OAuthScopes.Format"/>).</summary>
    public string Scopes { get; set; } = "";
    public bool RequirePkce { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SecretRotatedAt { get; set; }

    public bool IsPublic => Type == "public";
}

/// <summary>An authorize request waiting for the user: signed in, then allowed or denied. Lives 10 minutes.</summary>
public sealed class OAuthAuthorizeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    /// <summary>The user, once signed in.</summary>
    public Guid? UserId { get; set; }
    public required string RedirectUri { get; set; }
    public string Scopes { get; set; } = "";
    public string? State { get; set; }
    public string? CodeChallenge { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>"allow" or "deny" once decided.</summary>
    public string? Decision { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>A user's consent to a client: one per user and client. Its scopes only grow with later consents.</summary>
public sealed class OAuthGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
    public string Scopes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>An authorization code. Single use; only its hash is stored.</summary>
public sealed class OAuthCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required byte[] Hash { get; set; }
    public Guid GrantId { get; set; }
    public required string RedirectUri { get; set; }
    public string Scopes { get; set; } = "";
    public string? CodeChallenge { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    /// <summary>The token family the exchange started: revoked if the code is presented again.</summary>
    public Guid? FamilyId { get; set; }
}

/// <summary>The tokens of one code exchange and every refresh after it. Revoking it ends them all; the consent stays.</summary>
public sealed class OAuthTokenFamily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GrantId { get; set; }
    public string Scopes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}

/// <summary>An access token (<c>kbo_</c>). Only its hash is stored.</summary>
public sealed class OAuthAccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required byte[] Hash { get; set; }
    public required string Prefix { get; set; }
    public Guid FamilyId { get; set; }
    public string Scopes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A refresh token (<c>kbr_</c>). Only its hash is stored. Rotated once: presented again after that, it revokes its family.</summary>
public sealed class OAuthRefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required byte[] Hash { get; set; }
    public required string Prefix { get; set; }
    public Guid FamilyId { get; set; }
    /// <summary>The access token issued with it, which a refresh ends too.</summary>
    public Guid AccessTokenId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>The idle limit: issued time plus the refresh lifetime.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
