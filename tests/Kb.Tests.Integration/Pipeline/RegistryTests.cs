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

    /// <summary>
    /// docs/api-reference.md lists every route of the API with the action it sends and whether an API token may call it.
    /// Written from the running app, like the action reference: run this test with KB_WRITE_REFS=1 after adding or changing a route.
    /// </summary>
    [Fact]
    public void ApiReference_IsUpToDate()
    {
        var text = new StringBuilder("# API reference\n\n")
            .Append("Written by the test `ApiReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.\n\n")
            .Append("Every route sends one action; [the action reference](plugins/action-reference.md) lists each action's fields and result.\n")
            .Append("The answer names the action in the `X-Kb-Action` header. A route needs sign-in unless it says otherwise.\n")
            .Append("API token: the scope a token needs to call the route. \"No\" means the route answers 403 to every token. See [the agent API](agent-api.md).\n")
            .Append("Body: the JSON the route reads; Answer: the JSON it answers (`Unit`: no body, status 204). Each type links to its fields and an example in [the action reference](plugins/action-reference.md#types).\n\n")
            .Append(ErrorShape)
            .Append("| Method | Route | Action | Body | Answer | Changes data | Sign-in | API token |\n|---|---|---|---|---|---|---|---|\n");
        ActionTypes = _f.Services.GetRequiredService<ActionCatalog>().Actions.Select(a => a.Action)
            .ToDictionary(a => ActionCatalog.Describe(a).Name, ResultOf, StringComparer.Ordinal);
        foreach (var (method, template, endpoint) in Routes().Where(r => r.Endpoint.Metadata.GetMetadata<ActionAttribute>() is not null)
                     .OrderBy(r => r.Template, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal))
        {
            var action = endpoint.Metadata.GetMetadata<ActionAttribute>()!;
            var anonymous = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            var token = !Kb.Web.ApiTokenAuth.IsOpen(method, template) ? "No" : method == "GET" ? "Read" : "Write";
            var body = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>() is { RequestType: { } rt } accepts && accepts.ContentTypes.Contains("application/json") ? Linked(rt, page: "plugins/action-reference.md") : "";
            var answer = ActionTypes.TryGetValue(action.Name, out var at) ? Linked(at, page: "plugins/action-reference.md") : "";
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| {method} | `{template}` | `{action.Name}` | {body} | {answer} | {(action.Read ? "No" : "Yes")} | {(anonymous ? "Not needed" : "Needed")} | {token} |\n");
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "docs", "api-reference.md");
        if (Environment.GetEnvironmentVariable("KB_WRITE_REFS") == "1") File.WriteAllText(file, text.ToString());
        Assert.True(File.Exists(file), "docs/api-reference.md is missing. Run this test with KB_WRITE_REFS=1.");
        Assert.Equal(text.ToString(), File.ReadAllText(file).ReplaceLineEndings("\n"));
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
        text.Append("\nA type in a table links to its fields and an example of its JSON under [Types](#types). JSON names are camelCase; an enum is its name as text.\n")
            .Append("A plugin sends an action with `IActionBus.SendAsync(new TheAction { ... }, ct)` and gets an `Outcome<T>`: `Value` or `Problem`.\n");
        var types = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        foreach (var action in catalog.Actions.Select(a => a.Action).OrderBy(a => ActionCatalog.Describe(a).Name, StringComparer.Ordinal))
        {
            var described = ActionCatalog.Describe(action);
            var result = ResultOf(action);
            Collect(result, types);
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\n## {described.Name}\n\n{(described.Read ? "Read" : "Write")}. `{action.Name}`, answers {Linked(result)}.{(action.Assembly == contract ? "" : " Core only.")}\n");
            var fields = action.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract").ToList();
            if (fields.Count == 0) continue;
            text.Append("\n| Field | Type | |\n|---|---|---|\n");
            foreach (var p in fields)
            {
                Collect(p.PropertyType, types);
                var nullable = !p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable;
                var notes = new[] { p.GetCustomAttribute<RequiredMemberAttribute>() is not null ? "required" : null, p.GetCustomAttribute<JsonIgnoreAttribute>() is not null ? "never logged" : null };
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| {p.Name} | {Linked(p.PropertyType, nullable)} | {string.Join(", ", notes.Where(n => n is not null))} |\n");
            }
        }

        // Every record and enum an action or a route names, with its fields and an example of its JSON.
        foreach (var route in typeof(Kb.Web.ApiTokenAuth).Assembly.GetTypes().Where(t => (t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Body", StringComparison.Ordinal)) && t.IsPublic && !t.IsAbstract)) Collect(route, types);
        text.Append("\n## Types\n\nThe records and enums the actions and routes use. `?` means it may be null or left out.\n");
        foreach (var (name, type) in types)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n### {name}\n\n");
            if (type.IsEnum) { text.Append(System.Globalization.CultureInfo.InvariantCulture, $"Enum: {string.Join(", ", Enum.GetNames(type).Select(n => $"`{n}`"))}.\n"); continue; }
            var props = Props(type);
            if (props.Count == 0) { text.Append("No fields.\n"); continue; }
            text.Append("| Field | Type |\n|---|---|\n");
            foreach (var p in props)
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| {Json(p.Name)} | {Linked(p.PropertyType, !p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable)} |\n");
            text.Append("\n```json\n").Append(System.Text.Json.JsonSerializer.Serialize(Example(type, 0), Pretty).ReplaceLineEndings("\n")).Append("\n```\n");
        }
        return text.ToString();
    }

    private static Dictionary<string, Type> ActionTypes = [];

    private const string ErrorShape =
        "A refusal answers a problem body (RFC 9457), with the status of the answer:\n\n" +
        "```json\n{\n  \"type\": \"https://tools.ietf.org/html/rfc9110#section-15.5.1\",\n  \"title\": \"Bad Request\",\n  \"status\": 400,\n  \"detail\": \"Not a plugin package.\",\n  \"errors\": { \"title\": [\"Required.\"] },\n  \"traceId\": \"00-...\"\n}\n```\n\n" +
        "`detail` is the message for a person. `errors` comes with a 400 about a field (the field's JSON name, its messages). " +
        "401: not signed in or a bad token. 403: not allowed. 404: not found, or not visible to the caller. 409: a conflict (a version changed, a name taken). " +
        "422: refused by a rule (a plugin's hook, the AI tripwire). 423: locked by a disabled enforcing plugin. 429: too many calls.\n\n";

    private static readonly System.Text.Json.JsonSerializerOptions Pretty = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    private static Type ResultOf(Type action) => action.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAction<>)).GetGenericArguments()[0];

    private static bool Documented(Type t) => t.Namespace?.StartsWith("Kb", StringComparison.Ordinal) == true && t != typeof(Kb.Plugins.Unit) && !typeof(Delegate).IsAssignableFrom(t)
        && !typeof(System.IO.Stream).IsAssignableFrom(t) && t.Name != "UploadContent" && t.Name != "FileOut";

    private static List<PropertyInfo> Props(Type t) =>
        [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract" && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)];

    private static Type[] Parts(Type t) =>
        Nullable.GetUnderlyingType(t) is { } inner ? [inner] : t.IsArray ? [t.GetElementType()!] : t.IsGenericType ? t.GetGenericArguments() : [t];

    private static void Collect(Type t, SortedDictionary<string, Type> into)
    {
        foreach (var part in Parts(t))
        {
            if (part != t) { Collect(part, into); continue; }
            if (!Documented(part) || part.IsGenericType || into.ContainsKey(part.Name)) continue;
            into[part.Name] = part;
            if (!part.IsEnum) foreach (var p in Props(part)) Collect(p.PropertyType, into);
        }
    }

    private static string Anchor(string name) => "#" + name.ToLowerInvariant();

    /// <summary>A type as it reads in a table: its name, linked to its entry under Types when it has one.</summary>
    private static string Linked(Type t, bool nullable = false, string page = "")
    {
        var name = TypeName(t) + (nullable ? "?" : "");
        var own = Parts(t).SelectMany(x => x == t ? [x] : Parts(x)).Where(x => Documented(x) && !x.IsGenericType).Select(x => x.Name).Distinct().ToList();
        return own.Count == 0 ? $"`{name}`" : $"`{name}` ({string.Join(", ", own.Select(n => $"[{n}]({page}{Anchor(n)})"))})";
    }

    private static string Json(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>An example value of a type, as its JSON would carry it.</summary>
    private static object? Example(Type t, int depth)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return Example(inner, depth);
        if (t == typeof(string)) return "text";
        if (t == typeof(Guid)) return "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
        if (t == typeof(bool)) return false;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short)) return 0;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return 0.5;
        if (t == typeof(DateTimeOffset) || t == typeof(DateTime)) return "2026-11-14T10:00:00+00:00";
        if (t == typeof(DateOnly)) return "2026-11-14";
        if (t == typeof(TimeSpan)) return "00:05:00";
        if (t.IsEnum) return Enum.GetNames(t).FirstOrDefault();
        if (t == typeof(System.Text.Json.Nodes.JsonNode) || t == typeof(System.Text.Json.JsonElement) || t == typeof(object)) return new Dictionary<string, object?>();
        if (depth > 3) return null;
        var dict = t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        if (dict is not null) return new Dictionary<string, object?> { ["key"] = Example(dict.GetGenericArguments()[1], depth + 1) };
        if (t.IsArray) return new[] { Example(t.GetElementType()!, depth + 1) };
        var list = t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (list is not null) return new[] { Example(list.GetGenericArguments()[0], depth + 1) };
        return Props(t).ToDictionary(p => Json(p.Name), p => Example(p.PropertyType, depth + 1));
    }

    private static string TypeName(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return TypeName(inner) + "?";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType) return $"{t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        return t == typeof(string) ? "string" : t == typeof(int) ? "int" : t == typeof(long) ? "long" : t == typeof(bool) ? "bool" : t.Name;
    }
}
