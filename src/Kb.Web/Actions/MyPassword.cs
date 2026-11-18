using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kb.Web.Actions;

/// <summary>Someone sets a new password for their own account by giving the current one. Core only.</summary>
[Action("me.password.set")]
public sealed record SetMyPassword : IAction<Unit>
{
    [System.Text.Json.Serialization.JsonIgnore] public string Current { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Password { get; init; } = "";

    /// <summary>A record prints every member; the passwords stay out, so a log line of the action cannot hold them.</summary>
    public override string ToString() => "SetMyPassword { }";
}

public sealed record MyPasswordRequest(string? Current, string? Password)
{
    public override string ToString() => "MyPasswordRequest { }";
}

internal sealed class SetMyPasswordHandler(UserManager<KbUser> users, SignInManager<KbUser> signIn, IAuditLog audit) : IActionHandler<SetMyPassword, Unit>
{
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SetMyPassword> ctx)
    {
        if (ctx.Actor.UserId is not { } id) return Problem.Unauthorized;
        // A token cannot change how its owner signs in.
        if (ctx.Actor.TokenId is not null) return Problem.Forbidden;
        return (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetMyPassword> ctx)
    {
        var (u, req, ip) = (_me!, ctx.Action, ctx.Actor.RemoteIp ?? "");
        // The break-glass password is the server's secret; a directory's or a sign-in method's account has its password there; and a
        // shared account (one an Admin exempted from codes, such as a public demo account) is used by many, so one of them must not
        // be able to lock the others out.
        if (u.IsBreakGlass || u.Source != UserSource.Local || u.ExternalIssuer is not null) return Problem.Conflict("This account's password is not set here.");
        if (CodeSignIn.Barred(u)) return Problem.Conflict("A shared account's password is set by an Admin.");
        if (req.Current.Length == 0) return Problem.Invalid("current", "Give your current password.");
        // A wrong current password counts toward the lockout like a wrong password at sign-in, and is audited the same way.
        if (await users.IsLockedOutAsync(u) || !await users.CheckPasswordAsync(u, req.Current))
        {
            if (!await users.IsLockedOutAsync(u)) await users.AccessFailedAsync(u);
            await audit.AppendAsync(new AuditEvent(AuditActions.PasswordSetFailed, u.Id, "user", u.Id.ToString(), Data: new Dictionary<string, string> { ["reason"] = "wrong current password", ["ip"] = ip }));
            return Problem.Invalid("current", "That is not your current password.");
        }
        var result = await users.ChangePasswordAsync(u, req.Current, req.Password);
        if (!result.Succeeded) return new Problem(400, Fields: new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] });
        await users.ResetAccessFailedCountAsync(u);
        await audit.AppendAsync(new AuditEvent(AuditActions.PasswordSet, u.Id, "user", u.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "self", ["ip"] = ip }));
        // The new stamp ends every session of this account; this one stays open.
        await signIn.SignInAsync(u, isPersistent: false);
        return new Unit();
    }
}

/// <summary>
/// Length is the rule for a password (15 characters). This refuses the long passwords that are no better than short ones: one character
/// or a short piece repeated, a straight run of the keyboard or the alphabet, the account's own name, and the most common long choices.
/// </summary>
public sealed class CommonPasswordValidator : IPasswordValidator<KbUser>
{
    private static readonly string[] Runs = ["0123456789012345678901234567890", "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz", "qwertyuiopasdfghjklzxcvbnm", "qwertzuiopasdfghjklyxcvbnm", "1q2w3e4r5t6y7u8i9o0p"];
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "passwordpassword", "password1234567", "password12345678", "passwordpassword1", "letmeinletmeinletmein", "iloveyouiloveyou", "adminadminadminadmin",
        "administrator123", "administrator1234", "changemechangeme", "welcomewelcome123", "correcthorsebatterystaple", "thisismypassword", "mypasswordispassword",
        "qwerty1234567890", "1234567890qwerty", "abc123abc123abc123", "trustno1trustno1", "passw0rdpassw0rd", "p@ssw0rdp@ssw0rd", "summer2026summer", "winter2026winter",
    };

    public Task<IdentityResult> ValidateAsync(UserManager<KbUser> manager, KbUser user, string? password)
    {
        ArgumentNullException.ThrowIfNull(user);
        var p = (password ?? "").ToLowerInvariant();
        var name = (user.UserName ?? "").ToLowerInvariant();
        var weak = p.Length > 0 && (Common.Contains(p) || Repeats(p) || Runs.Any(r => r.Contains(p, StringComparison.Ordinal) || new string([.. r.Reverse()]).Contains(p, StringComparison.Ordinal))
            || (name.Length >= 3 && p.Replace(name, "", StringComparison.Ordinal).Length < 6));
        return Task.FromResult(weak
            ? IdentityResult.Failed(new IdentityError { Code = "PasswordTooCommon", Description = "This password is too easy to guess: a repeated or run-of-the-keyboard pattern, your user name, or a very common choice." })
            : IdentityResult.Success);
    }

    /// <summary>True when the text is one piece of up to four characters said over and over.</summary>
    private static bool Repeats(string p)
    {
        for (var n = 1; n <= 4; n++)
            if (Enumerable.Range(0, p.Length).All(i => p[i] == p[i % n])) return true;
        return false;
    }
}
