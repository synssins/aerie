using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Kb.Core.Identity;
using Kb.Web.Pipeline;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>Rules that hold for every route and every action, read from the running app so a new route cannot slip past them.</summary>
public sealed partial class RegistryTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [GeneratedRegex(@"\{[a-zA-Z]+:guid\}")]
    private static partial Regex GuidPart();

    [GeneratedRegex(@"\{[a-zA-Z]+:int\}")]
    private static partial Regex IntPart();

    [GeneratedRegex(@"\{[a-zA-Z]+\}")]
    private static partial Regex TextPart();

    /// <summary>Every route under /api/v1, as (method, template, endpoint).</summary>
    private List<(string Method, string Template, RouteEndpoint Endpoint)> Routes() =>
        [.. _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (m, e.RoutePattern.RawText!, e)))];

    /// <summary>A request the route can bind: ids filled in, the query values some admin routes need, and an empty JSON object as the body.</summary>
    private static HttpRequestMessage Request(string method, string template)
    {
        var path = TextPart().Replace(IntPart().Replace(GuidPart().Replace(template, Guid.NewGuid().ToString()), "1"), "light");
        var request = new HttpRequestMessage(new HttpMethod(method), path + "?enabled=true&take=1");
        if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
        return request;
    }

    /// <summary>
    /// The admin check used to be one filter on the /admin group; now each handler makes it. A route that lost it would let anyone signed in
    /// administer the server, and no other test would notice. So: every /admin route, called by someone who is not an Admin, answers 403.
    /// </summary>
    [Fact]
    public async Task EveryAdminRoute_Is403_ForANonAdmin()
    {
        _ = await _f.SignedInBreakGlassAsync(); // the app is set up
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(new KbUser { UserName = "cara", DisplayName = "Cara", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        var cara = _f.NewClient();
        (await cara.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "cara", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        var admin = Routes().Where(r => r.Template.StartsWith("/api/v1/admin/", StringComparison.Ordinal)).ToList();
        Assert.True(admin.Count > 40, $"the admin route list was not read ({admin.Count})");
        var open = new List<string>();
        foreach (var (method, template, _) in admin)
        {
            using var request = Request(method, template);
            var status = (await cara.SendAsync(request, Ct)).StatusCode;
            if (status != HttpStatusCode.Forbidden) open.Add($"{method} {template} answered {(int)status}");
        }
        Assert.Empty(open);
    }

    [Fact]
    public void EveryAction_HasExactlyOneHandler()
    {
        var actions = _f.Services.GetRequiredService<ActionCatalog>().Actions.ToList();
        Assert.NotEmpty(actions);
        Assert.DoesNotContain(actions, a => a.Handlers != 1);
        // A name is used once: two actions of one name could not be told apart in the audit log, a lock or the action reference.
        var names = actions.Select(a => ActionCatalog.Describe(a.Action).Name).ToList();
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
    }
}
