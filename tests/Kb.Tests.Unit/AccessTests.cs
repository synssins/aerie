using Kb.Core.Spaces;
using Kb.Plugins;
using Kb.Plugins.Actions;
using NSubstitute;

namespace Kb.Tests.Unit;

public class AccessTests
{
    private readonly Guid _normal = Guid.NewGuid(), _sensitive = Guid.NewGuid(), _user = Guid.NewGuid();
    private readonly ISpacePermissionService _perms = Substitute.For<ISpacePermissionService>();
    private readonly ISpaceAccessData _data = Substitute.For<ISpaceAccessData>();
    private readonly SpaceAccess _access;

    public AccessTests()
    {
        var spaces = new[] { new Space { Id = _normal, Key = "n", Name = "Normal" }, new Space { Id = _sensitive, Key = "s", Name = "Sensitive", IsSensitive = true } };
        _data.GetSpaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => spaces.FirstOrDefault(s => s.Id == (Guid)ci[0]));
        _data.GetAllSpacesAsync(Arg.Any<CancellationToken>()).Returns(spaces);
        _data.GetPrincipalAsync(_user, Arg.Any<CancellationToken>()).Returns(new PrincipalInfo(_user, IsAdmin: true, HasFullAccess: true, []));
        _perms.GetRoleAsync(_user, _normal, Arg.Any<CancellationToken>()).Returns(SpaceRole.Supervisor);
        _perms.VisibleSpaceIdsAsync(_user, Arg.Any<CancellationToken>()).Returns([_normal]);
        _access = new SpaceAccess(_perms, _data, Substitute.For<IPluginPermissionData>(), new Kb.Core.Plugins.PluginRegistry([], false, 0));
    }

    [Fact]
    public async Task PluginActor_IsOwnerOnNormalSpace_NothingOnSensitive()
    {
        var plugin = Actor.Plugin("kb.test.probe", "queue");
        Assert.Equal(SpaceRole.Owner, await _access.RoleAsync(plugin, _normal));
        Assert.Null(await _access.RoleAsync(plugin, _sensitive));
        Assert.Null(await _access.RoleAsync(plugin, Guid.NewGuid()));
        Assert.True(await _access.CanAsync(plugin, _normal, SpaceAction.ManageRolesSettingsSensitive));
        Assert.False(await _access.CanAsync(plugin, _sensitive, SpaceAction.Read));
        Assert.Equal([_normal], await _access.VisibleSpacesAsync(plugin));
        Assert.False(await _access.IsAdminAsync(plugin));
    }

    [Fact]
    public async Task Anonymous_HasNothing()
    {
        var nobody = Actor.Anonymous();
        Assert.Null(await _access.RoleAsync(nobody, _normal));
        Assert.Empty(await _access.VisibleSpacesAsync(nobody));
        Assert.False(await _access.IsAdminAsync(nobody));
    }

    [Fact]
    public async Task User_IsAnsweredByThePermissionService()
    {
        var user = Actor.User(_user);
        Assert.Equal(SpaceRole.Supervisor, await _access.RoleAsync(user, _normal));
        Assert.True(await _access.CanAsync(user, _normal, SpaceAction.MoveDeleteOthers));
        Assert.False(await _access.CanAsync(user, _normal, SpaceAction.ManageRolesSettingsSensitive));
        Assert.Null(await _access.RoleAsync(user, _sensitive));
        Assert.Equal([_normal], await _access.VisibleSpacesAsync(user));
        Assert.True(await _access.IsAdminAsync(user));
    }

    [Fact]
    public async Task TokenOfAnAdmin_IsNotAnAdmin()
    {
        Assert.False(await _access.IsAdminAsync(Actor.User(_user, tokenId: Guid.NewGuid())));
        Assert.False(await _access.IsAdminAsync(Actor.User(Guid.NewGuid()))); // a user the directory does not know
    }
}
