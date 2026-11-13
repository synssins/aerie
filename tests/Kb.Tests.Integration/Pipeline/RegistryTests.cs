using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Every route under /api/v1 names the action it sends, and that action exists. With the bus refusing a route that sends another action
    /// than it names, this means: nothing is reachable over the API that is not an action.
    /// </summary>
    [Fact]
    public void EveryApiRoute_IsNamedAfterARegisteredAction()
    {
        var catalog = _f.Services.GetRequiredService<ActionCatalog>();
        var actions = catalog.Actions.Select(a => ActionCatalog.Describe(a.Action).Name).ToHashSet(StringComparer.Ordinal);
        var routes = Routes().Where(r => r.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null).ToList(); // the API's catch-all 404 has no method
        Assert.True(routes.Count > 90, $"the route list was not read ({routes.Count})");

        var unnamed = routes.Where(r => r.Endpoint.Metadata.GetMetadata<ActionAttribute>() is not { } named || !actions.Contains(named.Name))
            .Select(r => $"{r.Method} {r.Template}").ToList();
        Assert.Empty(unnamed);
        // And the other way round: an action nothing sends is dead weight, or a route forgot to name it.
        var sent = routes.Select(r => r.Endpoint.Metadata.GetMetadata<ActionAttribute>()!.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(actions.Except(sent));
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

    /// <summary>
    /// docs/plugins/action-reference.md lists every action: what a plugin author reads to know what can be hooked.
    /// It is written from the running app, so it cannot drift: run this test with KB_WRITE_REFS=1 to write the file after adding or changing an action.
    /// </summary>
    [Fact]
    public void ActionReference_IsUpToDate()
    {
        var text = ActionReference(_f.Services.GetRequiredService<ActionCatalog>());
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "docs", "plugins", "action-reference.md");
        if (Environment.GetEnvironmentVariable("KB_WRITE_REFS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        Assert.True(File.Exists(file), "docs/plugins/action-reference.md is missing. Run this test with KB_WRITE_REFS=1.");
        Assert.Equal(text, File.ReadAllText(file).ReplaceLineEndings("\n"));
    }

    private static string ActionReference(ActionCatalog catalog)
    {
        var contract = typeof(IAction<>).Assembly;
        var nullability = new NullabilityInfoContext();
        var text = new StringBuilder("# Action reference\n\n")
            .Append("Written by the test `ActionReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.\n\n")
            .Append("Every operation of the core is one action, sent through one bus. A plugin can put a before hook, a result filter (reads only) or an after handler\n")
            .Append("on an action of the contract assembly (`Kb.Plugins.Abstractions`). Core-only actions go through the same bus but are not part of the contract:\n")
            .Append("a plugin cannot hook how plugins, API tokens, storage and log shipping are administered.\n");
        foreach (var action in catalog.Actions.Select(a => a.Action).OrderBy(a => ActionCatalog.Describe(a).Name, StringComparer.Ordinal))
        {
            var described = ActionCatalog.Describe(action);
            var result = action.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAction<>)).GetGenericArguments()[0];
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\n## {described.Name}\n\n{(described.Read ? "Read" : "Write")}. `{action.Name}`, answers `{TypeName(result)}`.{(action.Assembly == contract ? "" : " Core only.")}\n");
            var fields = action.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract").ToList();
            if (fields.Count == 0) continue;
            text.Append("\n| Field | Type | |\n|---|---|---|\n");
            foreach (var p in fields)
            {
                var type = TypeName(p.PropertyType) + (!p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable ? "?" : "");
                var notes = new[] { p.GetCustomAttribute<RequiredMemberAttribute>() is not null ? "required" : null, p.GetCustomAttribute<JsonIgnoreAttribute>() is not null ? "never logged" : null };
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| {p.Name} | `{type}` | {string.Join(", ", notes.Where(n => n is not null))} |\n");
            }
        }
        return text.ToString();
    }

    private static string TypeName(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return TypeName(inner) + "?";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType) return $"{t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        return t == typeof(string) ? "string" : t == typeof(int) ? "int" : t == typeof(long) ? "long" : t == typeof(bool) ? "bool" : t.Name;
    }
}
