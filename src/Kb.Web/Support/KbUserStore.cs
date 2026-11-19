using System.Security.Cryptography;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// The user store, with one difference: an account's authenticator key and its recovery codes are stored encrypted when the
/// installation protects its keys (<see cref="KeyProtection.SecretName"/> is set). Then a copy of the database alone gives neither.
/// Without the secret the keys that would encrypt them lie beside them in the same database, so nothing is gained and they are stored
/// as before. A value stored before, or while the secret was not set, is still read.
/// </summary>
public sealed class KbUserStore(KbDbContext context, IdentityErrorDescriber describer, IDataProtectionProvider protection, IConfiguration config)
    : UserStore<KbUser, KbRole, KbDbContext, Guid>(context, describer)
{
    /// <summary>The provider name the framework keeps an account's authenticator key and recovery codes under.</summary>
    public const string Internal = "[AspNetUserStore]";
    private const string Mark = "kbp1:";
    private readonly IDataProtector _protector = protection.CreateProtector("kb.user.codes");

    private bool Protects => KeyProtection.SecretIn(config) is not null;

    public override Task SetTokenAsync(KbUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name, value is not null && loginProvider == Internal && Protects ? Mark + _protector.Protect(value) : value, cancellationToken);

    public override async Task<string?> GetTokenAsync(KbUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        if (value is null || loginProvider != Internal || !value.StartsWith(Mark, StringComparison.Ordinal)) return value;
        // Unreadable (the keys were lost): the same as none. The person cannot use codes until an Admin resets them.
        try { return _protector.Unprotect(value[Mark.Length..]); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>At start, with the secret set: encrypts the keys and codes that were stored before it was.</summary>
    public static async Task ProtectStraysAsync(KbDbContext db, IDataProtectionProvider protection, string? secret)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protection);
        if (string.IsNullOrEmpty(secret)) return;
        var protector = protection.CreateProtector("kb.user.codes");
        var open = await db.UserTokens.Where(t => t.LoginProvider == Internal && t.Value != null && !t.Value.StartsWith(Mark)).ToListAsync();
        foreach (var token in open) token.Value = Mark + protector.Protect(token.Value!);
        if (open.Count > 0) await db.SaveChangesAsync();
    }
}
