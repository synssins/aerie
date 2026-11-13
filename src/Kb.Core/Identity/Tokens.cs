using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kb.Core.Identity;

/// <summary>Personal: acts as the person who made it. Service: acts as a service account, for another system that reaches in.</summary>
public enum TokenKind { Personal, Service }

public enum TokenAccess { Read, Write }

/// <summary>What a token may reach. A null space means every space (never a sensitive one for a service token).</summary>
public sealed record TokenScope(Guid? SpaceId, [property: JsonConverter(typeof(JsonStringEnumConverter))] TokenAccess Access);

/// <summary>An API token. Only the SHA-256 hash of its text is stored; the text is shown once, when it is made.</summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The user the token acts as: its maker for a personal token, a service account for a service token.</summary>
    public Guid OwnerUserId { get; set; }
    public TokenKind Kind { get; set; }
    public required string Name { get; set; }
    public required byte[] Hash { get; set; }
    /// <summary>The first 12 characters of the text, to tell tokens apart on screen.</summary>
    public required string Prefix { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public IReadOnlyList<TokenScope> Scopes() => JsonSerializer.Deserialize<List<TokenScope>>(ScopesJson, TokenFormat.Json) ?? [];

    /// <summary>The widest access the scopes give to a space, or null when none names it.</summary>
    public TokenAccess? AccessTo(Guid spaceId, bool includeGlobal = true)
    {
        TokenAccess? best = null;
        foreach (var s in Scopes())
            if ((s.SpaceId == spaceId || (includeGlobal && s.SpaceId is null)) && (best is null || s.Access > best)) best = s.Access;
        return best;
    }
}

/// <summary>
/// Token text: <c>kbt_</c>, 43 base-62 characters (32 random bytes), <c>_</c>, a 6-character checksum.
/// The prefix lets secret scanners find a leaked token; the checksum rejects a mistyped one without a database call.
/// </summary>
public static class TokenFormat
{
    public const string Prefix = "kbt_";
    public const int PrefixShown = 12;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly SearchValues<char> Letters = SearchValues.Create(Alphabet);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Generate()
    {
        var body = Base62(new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true), 43);
        return Prefix + body + "_" + Checksum(body);
    }

    public static bool IsWellFormed(string? token) =>
        token is { Length: 54 } && token.StartsWith(Prefix, StringComparison.Ordinal) && token[47] == '_'
        && token.AsSpan(4, 43).IndexOfAnyExcept(Letters) < 0 && Checksum(token.Substring(4, 43)) == token[48..];

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Checksum(string body) => Base62(Crc32(Encoding.ASCII.GetBytes(body)), 6);

    private static string Base62(BigInteger value, int length)
    {
        var chars = new char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var digit);
            chars[i] = Alphabet[(int)digit];
        }
        return new string(chars);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
