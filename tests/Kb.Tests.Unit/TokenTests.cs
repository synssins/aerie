using Kb.Core.Identity;

namespace Kb.Tests.Unit;

public class TokenFormatTests
{
    [Fact]
    public void Generate_IsWellFormed_AndChecksumDetectsTypo()
    {
        var token = TokenFormat.Generate();
        Assert.StartsWith("kbt_", token);
        Assert.Equal(54, token.Length);
        Assert.True(TokenFormat.IsWellFormed(token));
        Assert.NotEqual(token, TokenFormat.Generate());

        var typo = token[..10] + (token[10] == 'x' ? 'y' : 'x') + token[11..];
        Assert.False(TokenFormat.IsWellFormed(typo));
        Assert.False(TokenFormat.IsWellFormed(token[..^1]));
        Assert.False(TokenFormat.IsWellFormed("abc_" + token[4..]));
        Assert.False(TokenFormat.IsWellFormed(null));
        Assert.Equal(32, TokenFormat.Hash(token).Length);
    }

    [Fact]
    public void AccessTo_TakesTheWidestScope_AndCanLeaveOutTheGlobalOne()
    {
        var space = Guid.NewGuid();
        var token = new ApiToken { Name = "t", Hash = [], Prefix = "", ScopesJson = $$"""[{"spaceId":null,"access":"Read"},{"spaceId":"{{space}}","access":"Write"}]""" };
        Assert.Equal(TokenAccess.Write, token.AccessTo(space));
        Assert.Equal(TokenAccess.Read, token.AccessTo(Guid.NewGuid()));
        Assert.Null(token.AccessTo(Guid.NewGuid(), includeGlobal: false));
    }
}
