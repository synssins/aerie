using Kb.Plugins;

namespace Kb.Plugin.Sample;

// #region guide:signin
/// <summary>
/// A sign-in method: "Sign in with Example ID" on the sign-in screen. The app runs the whole OpenID Connect sign-in; this class says
/// where the provider is, and refuses people outside the domain an Admin set. Its hosts are in the manifest's outboundHosts.
/// </summary>
public sealed class SampleSignIn(SampleSettings settings) : ISignInMethod
{
    public OidcProvider Provider { get; } = new() { Issuer = "https://id.example.com" };

    public Task<string?> CheckAsync(SignInClaims claims, CancellationToken ct) =>
        Task.FromResult(settings.SignInDomain is { } domain && !(claims.EmailVerified && claims.Email!.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase))
            ? $"Sign in with your account at {domain}."
            : null);
}
// #endregion
