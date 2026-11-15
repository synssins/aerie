using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kb.Core.Identity;
using Kb.Plugins;
using Kb.Web.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// Writes aerie's own API (/api/v1) as an OpenAPI 3.0.3 document, from the routes of the running app and the action contract.
/// It describes what the app does today; it is not a design. No dependency: the document is built as plain dictionaries and written as YAML.
/// </summary>
public static partial class OpenApiExport
{
    [GeneratedRegex(@"\b\w+\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]*)""(.*?)\.WithAction<", RegexOptions.Singleline)]
    private static partial Regex MapCall();

    [GeneratedRegex(@"\{(\*\*?)?([A-Za-z]+)(?::([a-z]+))?\??\}")]
    private static partial Regex Part();

    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Routes the generator could not describe in full, one line each. Written into the document as x-kb-undescribed.</summary>
    public static List<string> Gaps { get; } = [];

    public static string Write(IReadOnlyList<(string Method, string Template, RouteEndpoint Endpoint)> routes, ActionCatalog catalog, string sourceDir)
    {
        Gaps.Clear();
        var schemas = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var statuses = SuccessStatuses(sourceDir);
        var paths = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var results = catalog.Actions.Select(a => a.Action).ToDictionary(a => ActionCatalog.Describe(a).Name, a => a.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAction<>)).GetGenericArguments()[0], StringComparer.Ordinal);

        foreach (var (method, template, endpoint) in routes.Where(r => r.Endpoint.Metadata.GetMetadata<ActionAttribute>() is not null)
                     .OrderBy(r => r.Template, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal))
        {
            var action = endpoint.Metadata.GetMetadata<ActionAttribute>()!;
            var path = Part().Replace(template, m => "{" + m.Groups[2].Value + "}");
            var op = new Dictionary<string, object?>();
            var tag = template.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "api";
            var operationId = action.Name;
            if (!ids.Add(operationId)) { operationId = $"{action.Name}.{method.ToLowerInvariant()}{path.Replace('/', '.').Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal)}"; ids.Add(operationId); }
            op["tags"] = new List<object?> { tag };
            op["operationId"] = operationId;
            op["x-kb-action"] = action.Name;
            op["x-kb-changes-data"] = !action.Read;

            // Parameters: what the route's delegate binds from the path and the query. Services, the token and the raw request are not parameters.
            var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            var bodyType = accepts is { RequestType: { } rt } && accepts.ContentTypes.Contains("application/json") ? rt : null;
            var parameters = new List<object?>();
            var delegateMethod = endpoint.Metadata.GetMetadata<MethodInfo>();
            var rawRequest = false;
            var constraints = Part().Matches(template).ToDictionary(m => m.Groups[2].Value, m => m.Groups[3].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var p in delegateMethod?.GetParameters() ?? [])
            {
                var t = p.ParameterType;
                if (t == typeof(CancellationToken) || t.Name is "IActionBus" || t.Namespace?.StartsWith("Microsoft.", StringComparison.Ordinal) == true && t != typeof(string)) { rawRequest |= t == typeof(HttpContext) || t == typeof(HttpRequest); continue; }
                if (t == bodyType) continue;
                var inPath = constraints.TryGetValue(p.Name!, out var constraint);
                var schema = inPath ? PathSchema(constraint!, t, schemas) : Schema(t, schemas);
                var optional = !inPath && (p.HasDefaultValue || Nullable.GetUnderlyingType(t) is not null || !t.IsValueType && Nullability.Create(p).ReadState == NullabilityState.Nullable);
                var parameter = new Dictionary<string, object?> { ["name"] = p.Name, ["in"] = inPath ? "path" : "query", ["required"] = !optional, ["schema"] = schema };
                parameters.Add(parameter);
            }
            foreach (var name in constraints.Keys.Where(n => parameters.Cast<Dictionary<string, object?>>().All(p => !string.Equals((string?)p["name"], n, StringComparison.OrdinalIgnoreCase))))
                Gaps.Add($"{method} {template}: path part '{name}' is not bound by the handler's parameters.");
            if (parameters.Count > 0) op["parameters"] = parameters;

            if (bodyType is not null)
                op["requestBody"] = new Dictionary<string, object?> { ["required"] = true, ["content"] = new Dictionary<string, object?> { ["application/json"] = new Dictionary<string, object?> { ["schema"] = Schema(bodyType, schemas) } } };
            else if (rawRequest && method is "POST" or "PUT" or "PATCH")
            {
                op["requestBody"] = new Dictionary<string, object?> { ["content"] = new Dictionary<string, object?> { ["*/*"] = new Dictionary<string, object?> { ["schema"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "binary" } } } };
                Gaps.Add($"{method} {template}: reads the raw request (an upload); its media type and any form fields are not described.");
                op["x-kb-undescribed"] = "The handler reads the raw request body (an upload). Media type and form fields are not described.";
            }

            // Responses
            var responses = new Dictionary<string, object?>();
            results.TryGetValue(action.Name, out var result);
            // A group prefix (/admin) is not in the source's literal, so match on the end of the route and keep the longest literal.
            var near = statuses.Where(s => s.Method == method && template.EndsWith(s.Template, StringComparison.Ordinal)).ToList();
            var code = near.Where(s => s.Template.Length == near.Max(n => n.Template.Length)).Select(s => s.Status).Distinct().ToList();
            if (code.Count > 1) throw new InvalidOperationException($"{method} {template}: the route files give more than one success status ({string.Join(", ", code)}).");
            var status = code.Count == 1 && code[0] != 0 ? code[0] : result == typeof(Unit) ? 204 : 200;
            var ok = new Dictionary<string, object?> { ["description"] = status switch { 201 => "Created", 202 => "Accepted", 204 => "No content", _ => "OK" } };
            if (result is not null && result != typeof(Unit))
            {
                if (result.Name == "FileOut") ok["content"] = new Dictionary<string, object?> { ["*/*"] = new Dictionary<string, object?> { ["schema"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "binary" } } };
                else ok["content"] = new Dictionary<string, object?> { ["application/json"] = new Dictionary<string, object?> { ["schema"] = Schema(result, schemas) } };
            }
            responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ok;
            var anonymous = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            if (!anonymous) { responses["401"] = new Dictionary<string, object?> { ["description"] = "Not signed in, or a bad token" }; responses["403"] = new Dictionary<string, object?> { ["description"] = "Not allowed" }; }
            responses["default"] = new Dictionary<string, object?> { ["description"] = "A refusal: a problem body (RFC 9457) with the status of the answer. 400 a field is wrong, 404 not found or not visible to the caller, 409 a conflict, 422 refused by a rule, 423 locked by a disabled enforcing plugin, 429 too many calls", ["content"] = new Dictionary<string, object?> { ["application/problem+json"] = new Dictionary<string, object?> { ["schema"] = new Dictionary<string, object?> { ["$ref"] = "#/components/schemas/Problem" } } } };
            op["responses"] = responses;

            // Auth
            var token = Kb.Web.ApiTokenAuth.IsOpen(method, template);
            if (anonymous) op["security"] = new List<object?>();
            else op["security"] = token ? new List<object?> { new Dictionary<string, object?> { ["cookieAuth"] = new List<object?>() }, new Dictionary<string, object?> { ["bearerAuth"] = new List<object?>() } } : new List<object?> { new Dictionary<string, object?> { ["cookieAuth"] = new List<object?>() } };
            op["x-kb-api-token"] = !token ? "closed" : method == "GET" ? "read" : "write";

            if (!paths.TryGetValue(path, out var item)) paths[path] = item = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            var methods = (SortedDictionary<string, object?>)item!;
            if (!methods.TryAdd(method.ToLowerInvariant(), op)) throw new InvalidOperationException($"{method} {path} is registered twice.");
        }

        schemas["Problem"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["type"] = new Dictionary<string, object?> { ["type"] = "string" }, ["title"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["status"] = new Dictionary<string, object?> { ["type"] = "integer", ["format"] = "int32" }, ["detail"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["errors"] = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" } } },
                ["traceId"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        };

        var doc = new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "Aerie API (as-is)",
                ["version"] = "as-is",
                ["description"] = "Aerie's own API under /api/v1, generated from the routes of the running app by the test OpenApiExport_IsUpToDate. It describes the app as it behaves today. Do not edit by hand.",
            },
            ["servers"] = new List<object?> { new Dictionary<string, object?> { ["url"] = "/" } },
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object?>
            {
                ["securitySchemes"] = new Dictionary<string, object?>
                {
                    ["cookieAuth"] = new Dictionary<string, object?> { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "__Host-kb" },
                    ["bearerAuth"] = new Dictionary<string, object?> { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "An API token (kbt_...). Open to the routes marked x-kb-api-token read or write only." },
                },
                ["schemas"] = schemas,
            },
        };
        if (Gaps.Count > 0) doc["x-kb-undescribed"] = Gaps.Cast<object?>().ToList();
        var sb = new StringBuilder();
        Yaml(doc, 0, sb);
        return sb.ToString();
    }

    /// <summary>The success status each route's source names (Results.Created, Results.Accepted, Results.Ok): the handler's mapping is code, not metadata.</summary>
    private static List<(string Method, string Template, int Status)> SuccessStatuses(string sourceDir)
    {
        var found = new List<(string, string, int)>();
        foreach (var file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(Path.Combine(sourceDir, "Routes"), "*.cs")))
            foreach (Match m in MapCall().Matches(File.ReadAllText(file)))
            {
                var body = m.Groups[3].Value;
                var status = body.Contains("Results.Created(", StringComparison.Ordinal) ? 201 : body.Contains("Results.Accepted(", StringComparison.Ordinal) ? 202 : body.Contains("Results.Ok()", StringComparison.Ordinal) ? 200 : 0;
                found.Add((m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value, status));
            }
        return found;
    }

    private static Dictionary<string, object?> PathSchema(string constraint, Type t, SortedDictionary<string, object?> schemas) => constraint switch
    {
        "guid" => new() { ["type"] = "string", ["format"] = "uuid" },
        "int" => new() { ["type"] = "integer", ["format"] = "int32" },
        _ => Schema(t, schemas),
    };

    private static string Json(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string ComponentName(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)] + string.Concat(t.GetGenericArguments().Select(ComponentName)) : t.Name;

    private static Dictionary<string, object?> Schema(Type t, SortedDictionary<string, object?> schemas, bool nullable = false)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return Schema(inner, schemas, true);
        var s = Plain(t, schemas);
        if (!nullable) return s;
        return s.ContainsKey("$ref") ? new() { ["nullable"] = true, ["allOf"] = new List<object?> { s } } : new(s) { ["nullable"] = true };
    }

    private static Dictionary<string, object?> Plain(Type t, SortedDictionary<string, object?> schemas)
    {
        if (t == typeof(string)) return new() { ["type"] = "string" };
        if (t == typeof(Guid)) return new() { ["type"] = "string", ["format"] = "uuid" };
        if (t == typeof(bool)) return new() { ["type"] = "boolean" };
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte)) return new() { ["type"] = "integer", ["format"] = "int32" };
        if (t == typeof(long)) return new() { ["type"] = "integer", ["format"] = "int64" };
        if (t == typeof(double)) return new() { ["type"] = "number", ["format"] = "double" };
        if (t == typeof(float)) return new() { ["type"] = "number", ["format"] = "float" };
        if (t == typeof(decimal)) return new() { ["type"] = "number" };
        if (t == typeof(DateTimeOffset) || t == typeof(DateTime)) return new() { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(DateOnly)) return new() { ["type"] = "string", ["format"] = "date" };
        if (t == typeof(TimeSpan)) return new() { ["type"] = "string" };
        if (t == typeof(byte[])) return new() { ["type"] = "string", ["format"] = "byte" };
        if (t == typeof(JsonNode) || t == typeof(JsonElement) || t == typeof(JsonObject) || t == typeof(object)) return new();
        if (t.IsEnum) return Component(t, schemas, () => new() { ["type"] = "string", ["enum"] = Enum.GetNames(t).Cast<object?>().ToList() });
        var dict = t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        if (dict is not null) return new() { ["type"] = "object", ["additionalProperties"] = Schema(dict.GetGenericArguments()[1], schemas) };
        if (t.IsArray) return new() { ["type"] = "array", ["items"] = Schema(t.GetElementType()!, schemas) };
        var list = t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (list is not null) return new() { ["type"] = "array", ["items"] = Schema(list.GetGenericArguments()[0], schemas) };
        return Component(t, schemas, () =>
        {
            var props = new Dictionary<string, object?>();
            var required = new List<object?>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract" && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null))
            {
                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? Json(p.Name);
                var nullable = !p.PropertyType.IsValueType && Nullability.Create(p).ReadState == NullabilityState.Nullable;
                props[name] = Schema(p.PropertyType, schemas, nullable);
                if (p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null) required.Add(name);
            }
            var s = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) s["required"] = required;
            return s;
        });
    }

    /// <summary>A named schema under components; the first use builds it. The reference is what the caller gets.</summary>
    private static Dictionary<string, object?> Component(Type t, SortedDictionary<string, object?> schemas, Func<Dictionary<string, object?>> build)
    {
        var name = ComponentName(t);
        if (!schemas.ContainsKey(name))
        {
            schemas[name] = null; // placeholder: a type that refers to itself finds its name taken
            schemas[name] = build();
        }
        return new() { ["$ref"] = $"#/components/schemas/{name}" };
    }

    private static void Yaml(object? node, int indent, StringBuilder sb)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case IDictionary<string, object?> map when map.Count > 0:
                foreach (var (key, value) in map)
                {
                    sb.Append(pad).Append(Key(key)).Append(':');
                    if (value is IDictionary<string, object?> { Count: > 0 } || value is List<object?> { Count: > 0 }) { sb.Append('\n'); Yaml(value, indent + 2, sb); }
                    else sb.Append(' ').Append(Scalar(value)).Append('\n');
                }
                break;
            case List<object?> list when list.Count > 0:
                foreach (var value in list)
                {
                    if (value is IDictionary<string, object?> { Count: > 0 } child)
                    {
                        var inner = new StringBuilder();
                        Yaml(child, indent + 2, inner);
                        sb.Append(pad).Append("- ").Append(inner.ToString().AsSpan(indent + 2)); // the first key rides on the dash
                    }
                    else sb.Append(pad).Append("- ").Append(Scalar(value)).Append('\n');
                }
                break;
            default:
                sb.Append(pad).Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static string Key(string key) => Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$") && key is not ("true" or "false" or "null" or "yes" or "no" or "on" or "off") ? key : Quote(key);

    private static string Scalar(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int or long => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!,
        string s => Quote(s),
        IDictionary<string, object?> => "{}",
        List<object?> => "[]",
        _ => throw new InvalidOperationException($"No YAML for {v.GetType()}"),
    };

    // A JSON string is a valid YAML double-quoted scalar.
    private static string Quote(string s) => JsonSerializer.Serialize(s, Relaxed);

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
