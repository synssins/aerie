using System.Text.Json;
using Kb.Data;

namespace Kb.Web;

/// <summary>
/// The token an audit entry was made with. It goes in the entry's data, which the hash chain covers, so the "Via" an Admin reads
/// (API or Web) and the token's name cannot be changed afterwards without breaking the chain.
/// </summary>
public static class AuditData
{
    public const string TokenKey = "token", TokenIdKey = "tokenId";

    /// <summary>The note's data, plus the token's name and id when the request was made with an API token.</summary>
    public static IReadOnlyDictionary<string, string>? WithToken(IReadOnlyDictionary<string, string>? data, Actor actor, HttpContext? http)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.TokenId is not { } id || ApiTokenAuth.Current(http) is not { } token || token.Id != id) return data;
        var all = new Dictionary<string, string>(data ?? new Dictionary<string, string>()) { [TokenKey] = token.Name, [TokenIdKey] = id.ToString("D") };
        return all;
    }

    public static Dictionary<string, string> Read(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
