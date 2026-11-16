namespace Kb.Web.DataCenter;

/// <summary>The redirect URI rules of identity.md, "Redirect URI rules" (RFC 9700 section 2.1, RFC 6749 section 3.1.2, RFC 8252 section 7.3).</summary>
public static class OAuthRedirects
{
    private static bool IsLoopback(string host) => host is "127.0.0.1" or "[::1]" or "::1" or "localhost";

    /// <summary>Why a URI cannot be registered, or null when it can.</summary>
    public static string? Invalid(string? uri, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 2000) return "a redirect URI is 1 to 2000 characters";
        if (uri.Contains('#', StringComparison.Ordinal)) return "a redirect URI has no fragment";
        if (uri.Contains('*', StringComparison.Ordinal) || uri.Any(char.IsWhiteSpace)) return "a redirect URI is exact: no patterns or spaces";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return "a redirect URI is absolute";
        var scheme = u.Scheme;
        if (scheme == Uri.UriSchemeHttps) return u.Host.Length > 0 ? null : "an https redirect URI names a host";
        if (scheme == Uri.UriSchemeHttp) return IsLoopback(u.Host) || allowHttp ? null : "http is allowed only on a loopback host (127.0.0.1, [::1], localhost)";
        // A private-use scheme for a native app has a dot in it (RFC 8252 section 7.1), so javascript:, data: and the like are never accepted.
        return scheme.Contains('.', StringComparison.Ordinal) ? null : "use https, http on a loopback host, or a private-use scheme with a dot";
    }

    /// <summary>
    /// True when a presented redirect URI is a registered one: character for character, after only the scheme and host are lowercased. For an
    /// http loopback URI the port is ignored (RFC 8252 section 7.3); everything else must still match.
    /// </summary>
    public static bool Matches(string registered, string? presented)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (presented is null) return false;
        if (Split(registered) is not { } r || Split(presented) is not { } p) return false;
        if (r.Scheme != p.Scheme || r.Host != p.Host || r.Tail != p.Tail) return false;
        return r.Port == p.Port || (r.Scheme == "http" && IsLoopback(r.Host));
    }

    /// <summary>Scheme and host lowercased, the port as written, and the rest exactly as written.</summary>
    private static (string Scheme, string Host, string Port, string Tail)? Split(string uri)
    {
        var colon = uri.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;
        var scheme = uri[..colon].ToLowerInvariant();
        var after = uri[(colon + 1)..];
        if (!after.StartsWith("//", StringComparison.Ordinal)) return (scheme, "", "", after);
        var authority = after[2..];
        var end = authority.IndexOfAny(['/', '?']);
        var (hostPort, rest) = end < 0 ? (authority, "") : (authority[..end], authority[end..]);
        if (hostPort.Contains('@', StringComparison.Ordinal)) return null; // no user info in a redirect URI
        var portAt = hostPort.StartsWith('[') ? hostPort.IndexOf("]:", StringComparison.Ordinal) is var b and >= 0 ? b + 1 : -1 : hostPort.LastIndexOf(':');
        var (host, port) = portAt < 0 ? (hostPort, "") : (hostPort[..portAt], hostPort[(portAt + 1)..]);
        return (scheme, host.ToLowerInvariant(), port, rest);
    }
}
