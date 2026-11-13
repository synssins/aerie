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
}

public sealed class BrandOptions
{
    public string ProductName { get; set; } = "Aerie";
    public string LogoLightPath { get; set; } = "/brand/logo-light.svg";
    public string LogoDarkPath { get; set; } = "/brand/logo-dark.svg";
    public string PrimaryColor { get; set; } = "#14304f";
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
    /// <summary>Sign-in attempts one address may make in a minute, on one server. Bounds password guessing and the audit entries it writes.</summary>
    public int SignInsPerMinutePerAddress { get; set; } = 30;
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
}

public sealed class LogOptions
{
    /// <summary>Folder of the local audit log file, read once: at first start it becomes the "Local log file" target. Relative paths are under the content root.</summary>
    public string Folder { get; set; } = "App_Data/logs";
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
        if (o.Auth.Entra is { } e && (string.IsNullOrWhiteSpace(e.TenantId) || string.IsNullOrWhiteSpace(e.ClientId)))
            errors.Add("Kb:Auth:Entra requires TenantId and ClientId.");
        var dupes = o.Hostnames.GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) errors.Add($"Kb:Hostnames has duplicate Host values: {string.Join(", ", dupes)}.");
        if (o.Retention.RecycleDays < 1) errors.Add("Kb:Retention:RecycleDays must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
