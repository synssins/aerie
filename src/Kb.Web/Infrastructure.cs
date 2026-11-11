using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Kb.Web;

/// <summary>Ready = database reachable and no drain flag. The load balancer stops sending traffic while App_Data/drain exists.</summary>
public sealed class ReadinessCheck(KbDbContext db, IWebHostEnvironment env) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (File.Exists(Path.Combine(env.ContentRootPath, "App_Data", "drain"))) return HealthCheckResult.Unhealthy("draining");
        return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database unreachable");
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private static readonly string Version =
        typeof(SecurityHeadersMiddleware).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "same-origin";
        // style-src-attr: the editor sets element style attributes (table column widths). Scripts and stylesheets stay 'self'.
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        h["X-Kb-Version"] = Version;
        return next(ctx);
    }
}

public static class UserExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();
}

/// <summary>
/// Optional emergency account. Created only when the secret named in Kb:Auth:BreakGlass:PasswordSecretName is configured;
/// it is never the normal way in (first-run setup creates the first admin). Its sign-ins are audited and alert admins.
/// </summary>
public sealed class BreakGlassService(
    UserManager<KbUser> users,
    IConfiguration config,
    IOptions<KbOptions> options,
    IAuditLog audit,
    INotifier notifier,
    IUserDirectory directory,
    TimeProvider time,
    ILogger<BreakGlassService> log)
{
    public async Task EnsureAccountAsync()
    {
        var o = options.Value.Auth.BreakGlass;
        var user = await users.FindByNameAsync(o.UserName);
        if (user is not null) return;

        var password = config[o.PasswordSecretName];
        if (string.IsNullOrEmpty(password))
        {
            log.LogInformation("No break-glass secret configured; break-glass account not created.");
            return;
        }

        user = new KbUser { UserName = o.UserName, DisplayName = "Break-glass administrator", Source = UserSource.Local, IsBreakGlass = true, IsAdmin = true, CreatedAt = time.GetUtcNow(), LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException("Could not create break-glass account: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    public async Task OnSignInAsync(KbUser user, string? remoteIp)
    {
        ArgumentNullException.ThrowIfNull(user);
        await audit.AppendAsync(new AuditEvent(AuditActions.BreakGlass, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["ip"] = remoteIp ?? "" }));
        await notifier.NotifyAsync(await directory.GetAdminIdsAsync(default), NotificationKinds.SecurityAlert,
            "Break-glass account signed in", $"The break-glass account signed in at {time.GetUtcNow():u} from {remoteIp}.", "/admin/audit", alsoEmail: true);
    }
}

public static class RuntimeSettings
{
    public static async Task<string?> GetAsync(this KbDbContext db, string key, CancellationToken ct = default) =>
        (await db.RuntimeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public static async Task SetAsync(this KbDbContext db, string key, string? value, Guid? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.RuntimeSettings.Add(new RuntimeSetting { Key = key, Value = value, UpdatedAt = now, UpdatedBy = by });
        else { s.Value = value; s.UpdatedAt = now; s.UpdatedBy = by; }
        await db.SaveChangesAsync(ct);
    }
}
