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

// #region guide:providers
/// <summary>
/// The check on the providers an Admin adds on this plugin's page (0.9.0). The app reads each provider from its settings and checks its
/// required claim; this runs after, on every provider of the plugin, and is told which one. The sample refuses an account its provider
/// marks as a test account.
/// </summary>
public sealed class SampleProviderCheck : ISignInProviderCheck
{
    public Task<string?> CheckAsync(string providerId, SignInClaims claims, CancellationToken ct) =>
        Task.FromResult(claims.All.TryGetValue("test_account", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True
            ? "Test accounts cannot sign in here."
            : null);
}
// #endregion
