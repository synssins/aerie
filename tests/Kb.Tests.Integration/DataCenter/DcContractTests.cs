using System.Text.RegularExpressions;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>The routed /rest/api operations are the contract's operations (contracts/datacenter/openapi.yaml), no more and no fewer.</summary>
public sealed partial class DcContractTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>
    /// Contract operations not built yet. Each pull request of the retrofit takes its operations off. The test fails when a routed operation is not
    /// in the contract, or one listed here is routed: the list is always what is left to build.
    /// </summary>
    private static readonly string[] NotYet =
    [
        "GET /rest/api/content/search", "GET /rest/api/search",
        "GET /rest/api/content/{id}/child/attachment", "POST /rest/api/content/{id}/child/attachment",
        "PUT /rest/api/content/{id}/child/attachment/{attachmentId}", "DELETE /rest/api/content/{id}/child/attachment/{attachmentId}",
        "POST /rest/api/content/{id}/child/attachment/{attachmentId}/data", "POST /rest/api/content/{id}/child/attachment/{attachmentId}/move",
        "DELETE /rest/api/content/{id}/child/attachment/{attachmentId}/version/{version}", "GET /rest/api/content/{id}/child/attachment/{attachmentId}/extractedtext",
        "GET /rest/api/content/{id}/label", "POST /rest/api/content/{id}/label", "DELETE /rest/api/content/{id}/label", "DELETE /rest/api/content/{id}/label/{label}",
        // OAuth 2.0: the identity work, after T1 (behaviour/identity-plan.md).
        "GET /rest/oauth2/latest/authorize", "POST /rest/oauth2/latest/token", "POST /rest/oauth2/latest/revoke", "GET /.well-known/oauth-authorization-server",
    ];

    [GeneratedRegex(@"^  (/(?:rest/(?:api|oauth2)|\.well-known)[^\s:]*):\s*$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|post|put|delete|patch):\s*$")]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"\{([a-zA-Z]+):[^}]+\}")]
    private static partial Regex Constraint();

    internal static List<string> ContractOperations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var ops = new List<string>();
        string? path = null;
        foreach (var line in File.ReadLines(Path.Combine(dir!.FullName, "contracts", "datacenter", "openapi.yaml")))
        {
            if (line.StartsWith("components:", StringComparison.Ordinal)) break;
            if (PathLine().Match(line) is { Success: true } p) path = p.Groups[1].Value;
            else if (line.Length > 0 && line[0] != ' ') path = null;
            else if (path is not null && MethodLine().Match(line) is { Success: true } m) ops.Add($"{m.Groups[1].Value.ToUpperInvariant()} {path}");
        }
        return ops;
    }

    private List<string> Routed() =>
        [.. _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<DcRoute>() is not null)
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => $"{m} {Constraint().Replace(e.RoutePattern.RawText!, "{$1}")}"))
            .Distinct()];

    [Fact]
    public void RoutedOperations_AreTheContractsOperations()
    {
        var contract = ContractOperations();
        Assert.NotEmpty(contract); // the contract was read
        Assert.Empty(NotYet.Except(contract)); // the list names only contract operations
        Assert.Equal(NotYet.Length, NotYet.Distinct().Count());
        Assert.Equal(contract.Except(NotYet).Order(StringComparer.Ordinal), Routed().Order(StringComparer.Ordinal));
    }
}
