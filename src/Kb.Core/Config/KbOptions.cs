using Microsoft.Extensions.Options;

namespace Kb.Core.Config;

/// <summary>Root of all Kb configuration. Section name: "Kb". Schema: config/kb.schema.json.</summary>
public sealed class KbOptions
{
    public const string Section = "Kb";
    public BrandOptions Brand { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public SetupOptions Setup { get; set; } = new();
    public List<HostnameOptions> Hostnames { get; set; } = [];
    public EmailOptions Email { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public PluginOptions Plugins { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public TokenOptions Tokens { get; set; } = new();
    public LogOptions Logs { get; set; } = new();
    public DataCenterOptions DataCenter { get; set; } = new();
    public ProxyOptions Proxy { get; set; } = new();
}

/// <summary>The reverse proxies in front of the app. Only they are believed when they say who the client is.</summary>
public sealed class ProxyOptions
{
    /// <summary>
    /// The addresses the proxy connects from, as the app sees them: single addresses ("10.0.0.5") or networks ("172.18.0.0/16").
    /// X-Forwarded-For and X-Forwarded-Proto are read only on a connection from one of them. Empty: the headers are ignored.
    /// </summary>
    public List<string> Trusted { get; set; } = [];

    /// <summary>The entries of <see cref="Trusted"/> as networks. Throws <see cref="FormatException"/> for an entry that is neither.</summary>
    public IEnumerable<System.Net.IPNetwork> Networks() => Trusted.Select(t =>
        t.Contains('/', StringComparison.Ordinal) ? System.Net.IPNetwork.Parse(t.Trim())
        : System.Net.IPAddress.Parse(t.Trim()) is var a ? new System.Net.IPNetwork(a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128) : default);
}

public sealed class BrandOptions
{
    public string ProductName { get; set; } = "Aerie";
    public string LogoLightPath { get; set; } = "/brand/logo-light.svg";
    public string LogoDarkPath { get; set; } = "/brand/logo-dark.svg";
    public string PrimaryColor { get; set; } = "#14304f";
    // A line shown under the sign-in fields, such as a public demo's login. Empty shows nothing.
    public string SignInNote { get; set; } = "";
}

public enum DatabaseProvider { SqlServer, Sqlite }

public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
    public string ConnectionString { get; set; } = "";
}

public sealed class AuthOptions
{
    public bool LocalEnabled { get; set; } = true;
    public EntraOptions? Entra { get; set; }
    public BreakGlassOptions BreakGlass { get; set; } = new();
    public List<string> AllowedGroupIds { get; set; } = [];
    public TimeSpan EmergencyAccessDuration { get; set; } = TimeSpan.FromHours(4);
    /// <summary>How long a session lasts at most, however much it is used. Without use it ends after 8 hours.</summary>
    public TimeSpan SessionMaxAge { get; set; } = TimeSpan.FromHours(12);
    /// <summary>Sign-in attempts one address may make in a minute, on one server. Bounds password guessing and the audit entries it writes.</summary>
    public int SignInsPerMinutePerAddress { get; set; } = 30;
    /// <summary>Requests to the API by someone not signed in (reading Anyone spaces), per address.</summary>
    public int AnonymousRequestsPerMinutePerAddress { get; set; } = 120;
    /// <summary>Requests to the OAuth 2.0 token and revoke endpoints in a minute, per client and per address, on one server.</summary>
    public int OAuthTokenRequestsPerMinute { get; set; } = 60;
}

public sealed class EntraOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool AllowGuests { get; set; }
}

public sealed class BreakGlassOptions
{
    public string UserName { get; set; } = "breakglass";
    /// <summary>Name of the secret (Key Vault / user-secrets / env) holding the break-glass password. Never the password itself.</summary>
    public string PasswordSecretName { get; set; } = "Kb--BreakGlassPassword";
}

/// <summary>
/// First-run setup. Until a regular (non-break-glass) admin exists, the site offers a setup page that creates one.
/// Requests from the server itself need no code; other requests need the one-time code written to CodeFile.
/// An installer can instead supply AdminUserName plus the secret named AdminPasswordSecretName; that admin is
/// created at startup and the setup page never appears.
/// </summary>
public sealed class SetupOptions
{
    public string AdminUserName { get; set; } = "";
    public string AdminDisplayName { get; set; } = "Administrator";
    public string AdminPasswordSecretName { get; set; } = "Kb--SetupAdminPassword";
    /// <summary>Relative paths resolve against the content root.</summary>
    public string CodeFile { get; set; } = "App_Data/setup-code.txt";
}

public enum HostRole { Full, PublicOnly }

public sealed class HostnameOptions
{
    public string Host { get; set; } = "";
    public HostRole Role { get; set; } = HostRole.Full;
}

public sealed class EmailOptions
{
    /// <summary>Plugin id of the active email sender, e.g. "kb.email.smtp". Empty = log-only (dev).</summary>
    public string Provider { get; set; } = "";
    public string From { get; set; } = "";
}

public sealed class RetentionOptions
{
    public int RecycleDays { get; set; } = 90;
    public int AuditMinDays { get; set; } = 2555;
}

public sealed class StorageOptions
{
    /// <summary>Folder for uploaded files (images); relative paths are under the content root.</summary>
    public string Folder { get; set; } = "App_Data/files";
    /// <summary>The most megabytes the attachments of one space may add up to; 0 for no limit. Removed attachments still count until they are purged.</summary>
    public int SpaceQuotaMb { get; set; }
}

public sealed class LogOptions
{
    /// <summary>Folder of the local audit log file, read once: at first start it becomes the "Local log file" target. Relative paths are under the content root.</summary>
    public string Folder { get; set; } = "App_Data/logs";
}

/// <summary>The Data Center compatible surface under /rest/api (contracts/datacenter/openapi.yaml).</summary>
public sealed class DataCenterOptions
{
    /// <summary>Prefix the surface is served under, such as "/kb"; empty for the root. Starts with "/" and has no trailing "/".</summary>
    public string ContextPath { get; set; } = "";
    /// <summary>The site URL clients see, with the context prefix. Empty: taken from each request.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>The most items one page of a list returns, whatever limit the caller asks for.</summary>
    public int MaxLimit { get; set; } = 200;
    /// <summary>The most pages one CQL search examines. A search runs in memory over every page its caller can see; past this it answers 400.</summary>
    public int MaxSearchPages { get; set; } = 50_000;
    /// <summary>The Data Center version aerie presents itself as.</summary>
    public string Version { get; set; } = "9.2.4";
    /// <summary>Build numbers reported with the version; 0 derives them from it. See contracts/behaviour/common.md (Server information).</summary>
    public int BuildNumber { get; set; }
    public int MarketplaceBuildNumber { get; set; }
    public string BuildDate { get; set; } = "2026-01-01T00:00:00.000Z";
}

public sealed class TokenOptions
{
    /// <summary>Requests one API token may make in a minute, on one server.</summary>
    public int RequestsPerMinute { get; set; } = 600;
}

public sealed class PluginOptions
{
    /// <summary>Where this server unpacks installed plugin packages. Relative paths are under the content root. The packages themselves are in the database.</summary>
    public string WorkFolder { get; set; } = "App_Data/plugins";
    /// <summary>Safe Mode: uploaded plugins are listed and none is loaded. For starting the app when a plugin stops it from starting.</summary>
    public bool SafeMode { get; set; }
}

public sealed class KbOptionsValidator : IValidateOptions<KbOptions>
{
    public ValidateOptionsResult Validate(string? name, KbOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Brand.ProductName)) errors.Add("Kb:Brand:ProductName is required.");
        if (string.IsNullOrWhiteSpace(o.Database.ConnectionString)) errors.Add("Kb:Database:ConnectionString is required.");
        if (string.IsNullOrWhiteSpace(o.Auth.BreakGlass.PasswordSecretName)) errors.Add("Kb:Auth:BreakGlass:PasswordSecretName is required.");
        if (string.IsNullOrWhiteSpace(o.Auth.BreakGlass.UserName)) errors.Add("Kb:Auth:BreakGlass:UserName is required.");
        if (o.Auth.EmergencyAccessDuration <= TimeSpan.Zero) errors.Add("Kb:Auth:EmergencyAccessDuration must be greater than zero.");
        if (o.Auth.SessionMaxAge < TimeSpan.FromMinutes(15)) errors.Add("Kb:Auth:SessionMaxAge must be at least 15 minutes.");
        if (o.Auth.Entra is { } e && (string.IsNullOrWhiteSpace(e.TenantId) || string.IsNullOrWhiteSpace(e.ClientId)))
            errors.Add("Kb:Auth:Entra requires TenantId and ClientId.");
        var dupes = o.Hostnames.GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) errors.Add($"Kb:Hostnames has duplicate Host values: {string.Join(", ", dupes)}.");
        if (o.DataCenter.ContextPath.Length > 0 && (!o.DataCenter.ContextPath.StartsWith('/') || o.DataCenter.ContextPath.EndsWith('/')))
            errors.Add("Kb:DataCenter:ContextPath must start with \"/\" and not end with one.");
        if (o.DataCenter.MaxLimit < 1) errors.Add("Kb:DataCenter:MaxLimit must be at least 1.");
        try { _ = o.Proxy.Networks().Count(); }
        catch (FormatException) { errors.Add("Kb:Proxy:Trusted holds addresses (10.0.0.5) and networks (172.18.0.0/16) only."); }
        if (o.Retention.RecycleDays < 1) errors.Add("Kb:Retention:RecycleDays must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
