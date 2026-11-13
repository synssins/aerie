using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Kb.Web;

/// <summary>First-run setup: creates the first regular admin. See <see cref="SetupOptions"/>.</summary>
public sealed class SetupService(
    UserManager<KbUser> users,
    KbDbContext db,
    IConfiguration config,
    IOptions<KbOptions> options,
    IHostEnvironment env,
    IAuditLog audit,
    TimeProvider time,
    ILogger<SetupService> log)
{
    // One setup at a time in this process; the re-check inside the lock stops a second admin being created.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private SetupOptions O => options.Value.Setup;
    private string CodePath => Path.IsPathRooted(O.CodeFile) ? O.CodeFile : Path.Combine(env.ContentRootPath, O.CodeFile);

    public async Task<bool> IsRequiredAsync(CancellationToken ct = default) =>
        !await db.Users.AnyAsync(u => u.IsAdmin && !u.IsBreakGlass && u.DeactivatedAt == null, ct);

    /// <summary>Requests from the server itself skip the setup code.</summary>
    public static bool IsLocal(HttpContext? http) => http?.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public async Task OnStartupAsync()
    {
        if (!await IsRequiredAsync())
        {
            File.Delete(CodePath);
            return;
        }

        var password = config[O.AdminPasswordSecretName];
        if (!string.IsNullOrWhiteSpace(O.AdminUserName) && !string.IsNullOrEmpty(password))
        {
            var (_, result) = await CreateAdminAsync(O.AdminUserName, O.AdminDisplayName, password, "installer");
            if (!result.Succeeded) throw new InvalidOperationException("Installer admin could not be created: " + string.Join("; ", result.Errors.Select(e => e.Description)));
            log.LogInformation("First admin {User} created from installer settings.", O.AdminUserName);
            return;
        }

        if (!File.Exists(CodePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CodePath)!);
            await File.WriteAllTextAsync(CodePath, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        }
        log.LogInformation("Setup required. Remote setup code: {File}", CodePath);
    }

    public bool CodeMatches(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !File.Exists(CodePath)) return false;
        var expected = Encoding.UTF8.GetBytes(File.ReadAllText(CodePath).Trim());
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(code.Trim()));
    }

    public async Task<(KbUser? User, IdentityResult Result)> CreateAdminAsync(string userName, string displayName, string password, string method)
    {
        var user = new KbUser
        {
            UserName = userName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            Source = UserSource.Local,
            IsAdmin = true,
            CreatedAt = time.GetUtcNow(),
            LockoutEnabled = true,
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) return (null, result);
        await audit.AppendAsync(new AuditEvent(AuditActions.SetupAdminCreate, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = method }));
        File.Delete(CodePath);
        return (user, result);
    }

    /// <summary><paramref name="local"/>: the request came from the server itself, so it needs no setup code.</summary>
    public async Task<Outcome<MeResponse>> RunAsync(RunSetup req, bool local, SignInManager<KbUser> signIn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(signIn);
        await Gate.WaitAsync(ct);
        try
        {
            if (!await IsRequiredAsync(ct)) return new Problem(409);
            if (!local && !CodeMatches(req.Code)) return Problem.Forbidden;
            if (string.IsNullOrWhiteSpace(req.UserName)) return Problem.Invalid("userName", "Required.");

            var (user, result) = await CreateAdminAsync(req.UserName, req.DisplayName, req.Password, local ? "setup-local" : "setup-code");
            if (user is null) return new Problem(400, Fields: new Dictionary<string, string[]> { ["password"] = result.Errors.Select(e => e.Description).ToArray() });

            await signIn.SignInAsync(user, isPersistent: false);
            return new MeResponse(user.Id, user.UserName!, user.DisplayName, user.IsAdmin, user.IsBreakGlass, user.IsAdmin);
        }
        finally { Gate.Release(); }
    }
}
