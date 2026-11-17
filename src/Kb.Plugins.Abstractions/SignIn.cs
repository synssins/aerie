using System.Text.Json;

namespace Kb.Plugins;

/// <summary>
/// An OpenID Connect provider, as a sign-in method describes it. The app runs the whole sign-in with it (authorization code flow with
/// PKCE, state and nonce, the ID token's signature, issuer, audience, expiry and nonce): the plugin only says where the provider is and
/// which claims hold what.
/// </summary>
public sealed record OidcProvider
{
    /// <summary>The issuer, exactly as the provider's ID tokens and discovery document give it: an https address with no query, at most 400 characters.</summary>
    public required string Issuer { get; init; }

    /// <summary>The discovery document's address. Null: the issuer followed by <c>/.well-known/openid-configuration</c>. An https address.</summary>
    public string? DiscoveryAddress { get; init; }

    /// <summary>The scopes asked for; <c>openid</c> must be one of them.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "email", "profile"];

    /// <summary>The claim that names the person at the provider, for good. The app links the account by issuer plus this value.</summary>
    public string SubjectClaim { get; init; } = "sub";

    public string EmailClaim { get; init; } = "email";

    /// <summary>The claim that is <c>true</c> when the provider verified the email address. Without it the address counts as not verified.</summary>
    public string EmailVerifiedClaim { get; init; } = "email_verified";

    public string NameClaim { get; init; } = "name";
}

/// <summary>
/// The claims of an ID token the app has validated: its issuer, the person's subject, email (and whether the provider verified it) and
/// name as the provider description maps them, and every claim of the token as it came. No token and no code is ever handed over.
/// </summary>
public sealed record SignInClaims(string Issuer, string Subject, string? Email, bool EmailVerified, string? Name, IReadOnlyDictionary<string, JsonElement> All);

/// <summary>
/// A sign-in method: a button on the sign-in screen that signs a person in through an OpenID Connect provider. The app runs the sign-in;
/// the plugin describes the provider and may refuse a person on the validated claims. It never opens a session, never sees the
/// authorization code or a token, and cannot act as the person. Made for each sign-in, with the plugin's services.
/// </summary>
public interface ISignInMethod
{
    /// <summary>The provider. Asked at each sign-in, so it may depend on the plugin's settings (a tenant, an organisation's address).</summary>
    OidcProvider Provider { get; }

    /// <summary>
    /// Runs on the validated claims before the app links or creates an account. Null lets the person on; a text refuses them, and is
    /// shown to them on the sign-in screen (at most 200 characters). A call that throws or runs past 10 seconds refuses too.
    /// </summary>
    Task<string?> CheckAsync(SignInClaims claims, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>A sign-in method a plugin registered: its id (unique within the plugin), the text of its button, and its type.</summary>
public sealed record SignInMethodReg(string Id, string Name, Type Method);

public static class SignInExtensions
{
    /// <summary>
    /// Registers a sign-in method. <paramref name="id"/>: 1 to 32 lowercase letters, digits and hyphens, starting with a letter or digit,
    /// unique within the plugin; it is part of the method's redirect address, so changing it means registering the app with the provider
    /// again. <paramref name="name"/>: the button's text, 1 to 60 characters. The app adds the method's client id, client secret and
    /// "new accounts" switch to the plugin's settings; the plugin cannot read them. Throws on an id or name that is not of that form.
    /// </summary>
    public static IPluginApp SignInMethod<TMethod>(this IPluginApp app, string id, string name) where TMethod : class, ISignInMethod
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!IsId(id)) throw new ArgumentException($"\"{id}\" is not a sign-in method id: 1 to 32 lowercase letters, digits and hyphens, starting with a letter or digit.", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) throw new ArgumentException("A sign-in method's name is 1 to 60 characters.", nameof(name));
        return app.Add(new SignInMethodReg(id, name.Trim(), typeof(TMethod)));
    }

    public static bool IsId(string? id) =>
        id is { Length: >= 1 and <= 32 } && id[0] != '-' && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
