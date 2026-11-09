using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Plugin.Email.Smtp;
using Kb.Plugin.Sample;
using Kb.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Kb.Tests.Unit;

public class KbOptionsValidationTests
{
    private static KbOptions Valid() => new() { Database = { ConnectionString = "x" } };

    [Fact] public void Valid_Passes() => Assert.True(new KbOptionsValidator().Validate(null, Valid()).Succeeded);

    [Fact]
    public void NoBreakGlassSecretName_Fails()
    {
        var o = Valid(); o.Auth.BreakGlass.PasswordSecretName = "";
        var r = new KbOptionsValidator().Validate(null, o);
        Assert.True(r.Failed); Assert.Contains("PasswordSecretName", r.FailureMessage);
    }

    [Fact]
    public void HostnameDuplicate_Fails()
    {
        var o = Valid(); o.Hostnames.Add(new() { Host = "kb.x" }); o.Hostnames.Add(new() { Host = "KB.x" });
        Assert.Contains("duplicate", new KbOptionsValidator().Validate(null, o).FailureMessage);
    }

    [Fact]
    public void EmergencyAccessZero_Fails()
    {
        var o = Valid(); o.Auth.EmergencyAccessDuration = TimeSpan.Zero;
        Assert.Contains("EmergencyAccessDuration", new KbOptionsValidator().Validate(null, o).FailureMessage);
    }

    [Fact]
    public void ShippedAppSettings_Passes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Kb.Web", "appsettings.json");
        var o = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build().GetSection(KbOptions.Section).Get<KbOptions>()!;
        Assert.True(new KbOptionsValidator().Validate(null, o).Succeeded);
        Assert.Equal(TimeSpan.FromHours(4), o.Auth.EmergencyAccessDuration);
    }
}

public class AuditHasherTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameInput_SameOutput()
    {
        var e = new AuditEvent("x", Guid.Empty, Data: new Dictionary<string, string> { ["a"] = "1" });
        Assert.Equal(AuditHasher.Hash(AuditHasher.Genesis, 1, At, e), AuditHasher.Hash(AuditHasher.Genesis, 1, At, e));
    }

    [Fact]
    public void DataKeyOrder_DoesNotChangeHash()
    {
        var a = new AuditEvent("x", null, Data: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var b = new AuditEvent("x", null, Data: new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        Assert.Equal(AuditHasher.Hash(AuditHasher.Genesis, 1, At, a), AuditHasher.Hash(AuditHasher.Genesis, 1, At, b));
    }

    [Fact]
    public void AnyFieldChange_ChangesHash()
    {
        var e = new AuditEvent("x", null);
        Assert.NotEqual(AuditHasher.Hash(AuditHasher.Genesis, 1, At, e), AuditHasher.Hash(AuditHasher.Genesis, 2, At, e));
        Assert.NotEqual(AuditHasher.Hash(AuditHasher.Genesis, 1, At, e), AuditHasher.Hash(AuditHasher.Genesis, 1, At, e with { Action = "y" }));
    }
}

public class RoleMatrixTests
{
    [Theory]
    [InlineData(SpaceAction.Read, SpaceRole.Reader)]
    [InlineData(SpaceAction.CreateEdit, SpaceRole.Contributor)]
    [InlineData(SpaceAction.MoveDeleteOwn, SpaceRole.Contributor)]
    [InlineData(SpaceAction.MoveDeleteOthers, SpaceRole.Supervisor)]
    [InlineData(SpaceAction.SeeOthersDrafts, SpaceRole.Supervisor)]
    [InlineData(SpaceAction.ReviewTeamHistory, SpaceRole.Supervisor)]
    [InlineData(SpaceAction.ManageBinAgingComments, SpaceRole.Supervisor)]
    [InlineData(SpaceAction.RemoveAttribution, SpaceRole.Supervisor)]
    [InlineData(SpaceAction.ManageRolesSettingsSensitive, SpaceRole.Owner)]
    [InlineData(SpaceAction.ApproveAccessRequests, SpaceRole.Owner)]
    public void MinimumRole_MatchesSpecTable(SpaceAction action, SpaceRole role) => Assert.Equal(role, RoleMatrix.MinimumRole(action));

    [Fact]
    public void EveryActionHasARow() =>
        Assert.All(Enum.GetValues<SpaceAction>(), a => RoleMatrix.MinimumRole(a));
}

public class SpacePermissionServiceTests
{
    private sealed class FakeData : ISpaceAccessData
    {
        public Dictionary<Guid, PrincipalInfo> Users { get; } = [];
        public List<Space> Spaces { get; } = [];
        public List<SpaceRoleAssignment> Assignments { get; } = [];
        public List<SpaceAccessGrant> Grants { get; } = [];
        public bool PublicOn { get; set; }
        public Task<PrincipalInfo?> GetPrincipalAsync(Guid u, CancellationToken ct) => Task.FromResult(Users.GetValueOrDefault(u));
        public Task<Space?> GetSpaceAsync(Guid s, CancellationToken ct) => Task.FromResult(Spaces.FirstOrDefault(x => x.Id == s));
        public Task<IReadOnlyList<Space>> GetAllSpacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Space>>(Spaces);
        public Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsAsync(Guid s, CancellationToken ct) => Task.FromResult<IReadOnlyList<SpaceRoleAssignment>>(Assignments.Where(a => a.SpaceId == s).ToList());
        public Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsForPrincipalsAsync(IReadOnlyCollection<string> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<SpaceRoleAssignment>>(Assignments.Where(a => ids.Contains(a.PrincipalId)).ToList());
        public Task<IReadOnlyList<SpaceAccessGrant>> GetActiveGrantsAsync(Guid u, DateTimeOffset now, CancellationToken ct) => Task.FromResult<IReadOnlyList<SpaceAccessGrant>>(Grants.Where(g => g.UserId == u && (g.ExpiresAt is null || g.ExpiresAt > now)).ToList());
        public Task<bool> IsPublicSpaceEnabledAsync(CancellationToken ct) => Task.FromResult(PublicOn);
    }

    private readonly FakeData _d = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));
    private readonly Space _normal = new() { Key = "sd", Name = "Service Desk" };
    private readonly Space _sensitive = new() { Key = "hr", Name = "HR", IsSensitive = true };
    private readonly Space _public = new() { Key = "help", Name = "Help", IsPublic = true };
    private readonly Guid _admin = Guid.NewGuid(), _user = Guid.NewGuid(), _outsider = Guid.NewGuid();
    private SpacePermissionService Svc => new(_d, _time);

    public SpacePermissionServiceTests()
    {
        _d.Spaces.AddRange([_normal, _sensitive, _public]);
        _d.Users[_admin] = new(_admin, true, true, []);
        _d.Users[_user] = new(_user, false, true, ["grp-sd"]);
        _d.Users[_outsider] = new(_outsider, false, false, []);
        _d.Assignments.Add(new() { SpaceId = _normal.Id, PrincipalType = PrincipalType.Group, PrincipalId = "grp-sd", Role = SpaceRole.Contributor });
    }

    [Fact] public async Task GroupAssignment_GivesRole() => Assert.Equal(SpaceRole.Contributor, await Svc.GetRoleAsync(_user, _normal.Id));

    [Fact] public async Task NoRole_ReturnsNull() => Assert.Null(await Svc.GetRoleAsync(_user, _sensitive.Id));

    [Fact] public async Task Admin_ImplicitOwner_OnNormalSpace() => Assert.Equal(SpaceRole.Owner, await Svc.GetRoleAsync(_admin, _normal.Id));

    [Fact]
    public async Task Admin_OnSensitiveSpace_NoImplicitAccess()
    {
        Assert.Null(await Svc.GetRoleAsync(_admin, _sensitive.Id));
        Assert.DoesNotContain(_sensitive.Id, await Svc.VisibleSpaceIdsAsync(_admin));
    }

    [Fact]
    public async Task EmergencyGrant_GivesAccess_UntilExpiry()
    {
        _d.Grants.Add(new() { SpaceId = _sensitive.Id, UserId = _admin, Role = SpaceRole.Reader, IsEmergency = true, ExpiresAt = _time.GetUtcNow().AddHours(4) });
        Assert.Equal(SpaceRole.Reader, await Svc.GetRoleAsync(_admin, _sensitive.Id));
        _time.Advance(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1));
        Assert.Null(await Svc.GetRoleAsync(_admin, _sensitive.Id));
    }

    [Fact]
    public async Task NonFullAccessUser_OnlyPublicSpacesVisible_WhenSwitchOn()
    {
        Assert.Empty(await Svc.VisibleSpaceIdsAsync(_outsider));
        _d.PublicOn = true;
        Assert.Equal([_public.Id], await Svc.VisibleSpaceIdsAsync(_outsider));
        Assert.Equal(SpaceRole.Reader, await Svc.GetRoleAsync(_outsider, _public.Id));
        Assert.Null(await Svc.GetRoleAsync(_outsider, _normal.Id));
    }

    [Fact]
    public async Task Contributor_CannotMoveOthersPages_OrManageRoles()
    {
        Assert.True(await Svc.CanAsync(_user, _normal.Id, SpaceAction.CreateEdit));
        Assert.False(await Svc.CanAsync(_user, _normal.Id, SpaceAction.MoveDeleteOthers));
        Assert.False(await Svc.CanAsync(_user, _normal.Id, SpaceAction.ManageRolesSettingsSensitive));
    }
}

public class PluginLoaderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kb-plugin-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuiltInPlugin_Loads_FromEmbeddedManifest()
    {
        var reg = PluginLoader.Discover([typeof(SmtpPlugin).Assembly, typeof(SamplePlugin).Assembly], new PluginOptions(), Root);
        Assert.Contains(reg.All, p => p.Manifest.Id == "kb.email.smtp" && p.Origin == PluginOrigin.BuiltIn && p.Enabled);
        Assert.Contains(reg.All, p => p.Manifest.Id == "kb.sample");
    }

    [Fact]
    public void UnlistedDropInAssembly_Refused()
    {
        var dir = Path.Combine(Root, "plugins", "Evil.Plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "kb-plugin.json"), """{"id":"evil","version":"1","publisher":"x"}""");
        File.Copy(typeof(SamplePlugin).Assembly.Location, Path.Combine(dir, "Evil.Plugin.dll"), overwrite: true);

        var reg = PluginLoader.Discover([], new PluginOptions(), Root);
        Assert.DoesNotContain(reg.All, p => p.Manifest.Id == "evil");
        Assert.Contains(reg.Refused, r => r.Reason.Contains("not in Plugins:AllowedHashes", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledByConfig_LoadsButNotEnabled_AndNotConfigured()
    {
        var reg = PluginLoader.Discover([typeof(SamplePlugin).Assembly], new PluginOptions { Disabled = ["kb.sample"] }, Root);
        Assert.False(reg.All.Single().Enabled);
        var services = new ServiceCollection();
        services.AddKbPlugins(new ConfigurationBuilder().Build(), reg);
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IPageAction));
    }

    [Fact]
    public async Task OutboundHttp_HostNotInManifest_Throws()
    {
        using var guard = new OutboundHostGuard(["graph.microsoft.com"]) { InnerHandler = new HttpClientHandler() };
        using var client = new HttpClient(guard);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.com/"));
        Assert.Contains("example.com", ex.Message);
    }

    [Fact]
    public void ActiveProvider_SelectedByConfig()
    {
        var a = Substitute.For<IEmailSender>(); a.ProviderId.Returns("kb.email.smtp");
        var b = Substitute.For<IEmailSender>(); b.ProviderId.Returns("kb.email.graph");
        var sel = new ProviderSelector<IEmailSender>([a, b]);
        Assert.Same(b, sel.Get("KB.EMAIL.GRAPH"));
        Assert.Null(sel.Get(""));
    }
}
