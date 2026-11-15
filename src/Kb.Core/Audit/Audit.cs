using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kb.Plugins;

namespace Kb.Core.Audit;

public sealed record AuditEvent(
    string Action,
    Guid? ActorId,
    string? TargetType = null,
    string? TargetId = null,
    Guid? SpaceId = null,
    IReadOnlyDictionary<string, string>? Data = null);

public interface IAuditLog
{
    Task AppendAsync(AuditEvent e, CancellationToken ct = default);
    Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default);
}

public static class AuditActions
{
    public const string SignIn = "auth.signin";
    public const string SignInFailed = "auth.signin.failed";
    public const string SignOut = "auth.signout";
    public const string CodeFailed = "auth.code.failed";
    public const string CodesEnroll = "auth.codes.enroll";
    public const string CodesOff = "auth.codes.off";
    public const string UserCodesReset = "user.codes.reset";
    public const string UserCodesExempt = "user.codes.exempt";
    public const string BreakGlass = "auth.breakglass";
    public const string SetupAdminCreate = "setup.admin.create";
    public const string BrandUpdate = "brand.update";
    public const string BrandTheme = "brand.theme";
    public const string PageMove = "page.move";
    public const string PageDelete = "page.delete";
    public const string PageRestore = "page.restore";
    public const string PageVersionRestore = "page.version.restore";
    public const string PageExport = "page.export";
    public const string BinEmpty = "bin.empty";
    public const string AttachmentAdd = "attachment.add";
    public const string AttachmentRemove = "attachment.remove";
    public const string AnnouncementPost = "announcement.post";
    public const string AnnouncementRemove = "announcement.remove";
    public const string AnnouncerSet = "user.announcer.set";
    public const string LocalDisabled = "auth.local.disable";
    public const string UserInvite = "user.invite";
    public const string UserDelete = "user.delete";
    public const string UserPasswordLink = "user.password.link";
    public const string PasswordSet = "auth.password.set";
    public const string PasswordSetFailed = "auth.password.set.failed";
    public const string SpaceCreate = "space.create";
    public const string SpaceUpdate = "space.update";
    public const string SpaceDelete = "space.delete";
    public const string SpaceRestore = "space.restore";
    public const string SpaceSensitiveSet = "space.sensitive.set";
    public const string SpaceRoleGrant = "space.role.grant";
    public const string SpaceRoleRevoke = "space.role.revoke";
    public const string RoleCreate = "role.create";
    public const string AiCall = "ai.call";
    public const string ImportAccept = "import.accept";
    public const string ImportQuarantine = "import.quarantine";
    public const string ImportPurge = "import.purge";
    public const string ImportReject = "import.reject";
    public const string PageFormat = "page.format";
    public const string AiRefused = "ai.refused";
    public const string AiSettingsChange = "ai.settings.change";
    public const string InventoryAppCreate = "inventory.app.create";
    public const string InventoryAppUpdate = "inventory.app.update";
    public const string InventoryAppDelete = "inventory.app.delete";
    public const string InventoryInstallSet = "inventory.install.set";
    public const string InventoryInstallRemove = "inventory.install.remove";
    public const string InventoryImport = "inventory.import";
    public const string InventorySync = "inventory.sync";
    public const string InventoryAccessSet = "inventory.access.set";
    public const string PageDetailsSet = "page.details.set";
    public const string RoleUpdate = "role.update";
    public const string RoleDelete = "role.delete";
    public const string AccessRequest = "access.request";
    public const string AccessDecide = "access.decide";
    public const string EmergencyAccess = "space.sensitive.emergency";
    public const string TokenCreate = "token.create";
    public const string TokenRevoke = "token.revoke";
    public const string TokenUse = "token.use";
    public const string TokenRefused = "token.refused";
    public const string SettingsChange = "settings.change";
    public const string PluginEnable = "plugin.enable";
    public const string PluginDisable = "plugin.disable";
    public const string PluginInstall = "plugin.install";
    public const string PluginUpdate = "plugin.update";
    public const string PluginRemove = "plugin.remove";
    public const string PluginDataDelete = "plugin.data.delete";
    public const string PluginDisabledByCore = "plugin.disable.core";
    public const string PluginLockRelease = "plugin.lock.release";
    public const string PluginSettingsChange = "plugin.settings.change";
    public const string PluginPermissionsChange = "plugin.permissions.change";
    public const string UserPermissionsSet = "user.permissions.set";
    public const string GroupCreate = "group.create";
    public const string GroupRename = "group.rename";
    public const string GroupDelete = "group.delete";
    public const string GroupMemberAdd = "group.member.add";
    public const string GroupMemberRemove = "group.member.remove";
    public const string StoragePlaceAdd = "storage.place.add";
    public const string StoragePlaceChange = "storage.place.change";
    public const string StoragePlaceRemove = "storage.place.remove";
    public const string LogTargetAdd = "logs.target.add";
    public const string LogTargetChange = "logs.target.change";
    public const string LogTargetRemove = "logs.target.remove";
    public const string StorageMoveStart = "storage.move.start";
    public const string StorageMoveSwitch = "storage.move.switch";
    public const string StorageMoveCancel = "storage.move.cancel";
}

public static class AuditHasher
{
    public static readonly string Genesis = new('0', 64);

    /// <summary>SHA-256 (lowercase hex) over canonical JSON of the entry plus the previous hash. Data keys are sorted ordinally.</summary>
    public static string Hash(string previousHash, long sequence, DateTimeOffset at, AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prev"] = previousHash,
            ["seq"] = sequence,
            ["at"] = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["action"] = e.Action,
            ["actor"] = e.ActorId?.ToString("D"),
            ["targetType"] = e.TargetType,
            ["targetId"] = e.TargetId,
            ["space"] = e.SpaceId?.ToString("D"),
            ["data"] = new SortedDictionary<string, string>(
                (IDictionary<string, string>)(e.Data ?? new Dictionary<string, string>()).ToDictionary(), StringComparer.Ordinal),
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
