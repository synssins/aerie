using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Core.Plugins;

/// <summary>The versions this host offers to plugins.</summary>
public static class KbHost
{
    public const string Version = "0.10.0";
    /// <summary>Raised one minor step per phase of the plugin model; 1.0.0 when the contracts are frozen.</summary>
    public const string AbstractionsVersion = "0.10.0";
    public const string UiApiVersion = "1.1.0";
}

public static partial class PluginId
{
    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*\z")]
    private static partial Regex Pattern();

    /// <summary>Lowercase letters and digits joined by single dots or hyphens, 3 to 64 characters.</summary>
    public static bool IsValid(string? id) => id is { Length: >= 3 and <= 64 } && Pattern().IsMatch(id);

    /// <summary>
    /// The prefix of a plugin's tables: "kb.email.smtp" gives "plg_kb_email_smtp__". The double underscore ends it,
    /// so the prefix of one plugin is never the start of another's.
    /// </summary>
    public static string TablePrefix(string id)
    {
        return PluginTables.Prefix(id);
    }
}

/// <summary>
/// The settings the app adds to a plugin for each sign-in method it registers: the client id, the client secret and whether unknown
/// people get an account. They are saved and shown with the plugin's own settings, under keys the plugin cannot declare and its settings
/// do not hand to it. That is a rule of the settings, not a wall: a plugin's code runs in the server process and can reach the store.
/// </summary>
public static class SignInSettings
{
    public const string Prefix = "signIn.";

    public static bool IsReserved(string? key) => key is not null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string ClientId(string method) => $"{Prefix}{method}.clientId";
    public static string ClientSecret(string method) => $"{Prefix}{method}.clientSecret";
    public static string NewAccounts(string method) => $"{Prefix}{method}.newAccounts";

    /// <summary>The settings of these methods (id, button text). Labels name the method only when the plugin has more than one.</summary>
    public static IEnumerable<PluginSetting> For(IReadOnlyList<(string Id, string Name)> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        foreach (var (id, name) in methods)
        {
            var of = methods.Count > 1 ? $" ({name})" : "";
            yield return new PluginSetting(ClientId(id), "Client ID" + of, Help: "From the provider, where this app is registered with the redirect address shown on this page. The sign-in button shows once it is set.");
            yield return new PluginSetting(ClientSecret(id), "Client secret" + of, Secret: true, Help: "From the provider. Stored encrypted; it is never shown again.");
            yield return new PluginSetting(NewAccounts(id), "New accounts" + of, Type: "toggle", Default: "false",
                Help: "On: a person the provider vouches for who has no account here gets one, never an Administrator and in no space. Off: only people who have an account sign in.");
        }
    }

    // ---- 0.9.0: providers an Admin adds (SignInProvidersReg). The list is kept under ProvidersKey; each provider's settings under its id.

    /// <summary>The list of a plugin's providers: a JSON array of { "id", "title" }. Not a setting the form shows; written by the provider actions.</summary>
    public const string ProvidersKey = Prefix + "providers";
    public const int MaxProviders = 20;

    public static string Issuer(string id) => $"{Prefix}{id}.issuer";
    public static string Discovery(string id) => $"{Prefix}{id}.discovery";
    public static string Scopes(string id) => $"{Prefix}{id}.scopes";
    public static string SubjectClaim(string id) => $"{Prefix}{id}.subjectClaim";
    public static string EmailClaim(string id) => $"{Prefix}{id}.emailClaim";
    public static string EmailVerifiedClaim(string id) => $"{Prefix}{id}.emailVerifiedClaim";
    public static string NameClaim(string id) => $"{Prefix}{id}.nameClaim";
    public static string UserInfo(string id) => $"{Prefix}{id}.userInfo";
    public static string RequiredClaim(string id) => $"{Prefix}{id}.requiredClaim";
    public static string RequiredValues(string id) => $"{Prefix}{id}.requiredValues";
    public static string PrivateNetwork(string id) => $"{Prefix}{id}.privateNetwork";

    /// <summary>A provider an Admin added: its stable id (part of its redirect address) and its title (its button's text).</summary>
    public sealed record Provider(string Id, string Title);

    /// <summary>The providers in the stored list; an empty list for nothing or a value that is not one.</summary>
    public static IReadOnlyList<Provider> Providers(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Provider>>(json, JsonOptions)?.Where(p => p is not null && SignInExtensions.IsId(p.Id) && IsTitle(p.Title)).DistinctBy(p => p.Id).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    public static string Write(IEnumerable<Provider> providers) => JsonSerializer.Serialize(providers, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A title is 1 to 60 characters, no control characters: the button's text, as for a method a plugin registers.</summary>
    public static bool IsTitle(string? title) => !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 60 && !title.Any(char.IsControl);

    /// <summary>The settings of one provider an Admin added: what a 0.8.0 plugin's code said about its provider, set on its page instead.</summary>
    public static IEnumerable<PluginSetting> ForProvider(string id)
    {
        yield return new PluginSetting(Issuer(id), "Issuer",
            Help: "The provider's issuer, exactly as its ID tokens give it: an https address, such as https://login.example.com/tenant/v2.0. The sign-in button shows once this and the client ID are set.");
        yield return new PluginSetting(Discovery(id), "Discovery address",
            Help: "Only when the provider's discovery document is not at the issuer followed by /.well-known/openid-configuration. An https address.");
        yield return new PluginSetting(ClientId(id), "Client ID", Help: "From the provider, where this app is registered with the redirect address shown for this provider.");
        yield return new PluginSetting(ClientSecret(id), "Client secret", Secret: true, Help: "From the provider. Stored encrypted; it is never shown again.");
        yield return new PluginSetting(Scopes(id), "Scopes", Default: "openid email profile", Help: "Separated by spaces; openid must be one of them.");
        yield return new PluginSetting(SubjectClaim(id), "Subject claim", Default: "sub", Help: "The claim that names the person for good. Accounts are linked by issuer and this value.");
        yield return new PluginSetting(EmailClaim(id), "Email claim", Default: "email");
        yield return new PluginSetting(EmailVerifiedClaim(id), "Email verified claim", Default: "email_verified",
            Help: "True when the provider verified the address. Only a verified address is matched to an account here.");
        yield return new PluginSetting(NameClaim(id), "Name claim", Default: "name");
        yield return new PluginSetting(UserInfo(id), "Read userinfo", Type: "toggle", Default: "true",
            Help: "On: after the sign-in the app reads the provider's userinfo endpoint, when it has one; its subject must match the ID token's, and it fills claims the ID token lacks.");
        yield return new PluginSetting(RequiredClaim(id), "Required claim",
            Help: "A claim the person must have, such as hd for a domain or tid for a tenant. Empty: none is required.");
        yield return new PluginSetting(RequiredValues(id), "Allowed values", Type: "multiline",
            Help: "One per line. The required claim must equal one of them (for a list claim, one of its values must).");
        yield return new PluginSetting(NewAccounts(id), "New accounts", Type: "toggle", Default: "false",
            Help: "On: a person the provider vouches for who has no account here gets one, never an Administrator and in no space. Off: only people who have an account sign in.");
        yield return new PluginSetting(PrivateNetwork(id), "Provider on a private network", Type: "toggle", Default: "false",
            Help: "Off (the default): the app reaches this provider only at public internet addresses, so a mistyped or hostile address cannot aim it at the internal network. On: private addresses too, for a provider on your own network such as a test provider in a lab. Https stays required.");
    }
}

/// <summary>Whether a value fits a setting. Used when an Admin saves it and for the manifest's default.</summary>
public static class SettingValue
{
    /// <summary>Null when the value fits; otherwise what is wrong, said to the Admin.</summary>
    public static string? Refusal(PluginSetting d, string value)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(value);
        switch (d.Type)
        {
            case "number":
                if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)) return "A number.";
                var unit = d.Unit is { Length: > 0 } u ? " " + u : "";
                if (d.Min is { } min && n < min) return $"At least {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}{unit}.";
                if (d.Max is { } max && n > max) return $"At most {max.ToString(System.Globalization.CultureInfo.InvariantCulture)}{unit}.";
                return null;
            case "toggle": return value is "true" or "false" ? null : "true or false.";
            case "select": return d.Options is { } options && !options.Contains(value) ? "Not one of the choices." : null;
            default: return null;
        }
    }
}

public static class PluginManifests
{
    public const string FileName = "kb-plugin.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads a <c>kb-plugin.json</c>. Null when the text is not a manifest.</summary>
    public static PluginManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<PluginManifest>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static string Write(PluginManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    /// <summary>The name people see: the manifest's name, or its id.</summary>
    public static string NameOf(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
    }
}

public static class PluginVersions
{
    /// <summary>
    /// Why a plugin does not fit this host, or null when it fits. Before 1.0.0 a plugin fits when its API version
    /// has the host's major number and is not higher than the host's.
    /// </summary>
    public static string? Refusal(PluginManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (!Version.TryParse(m.Version, out _)) return "Version is not valid.";
        if (!Fits(m.AbstractionsVersion, KbHost.AbstractionsVersion)) return $"Built for a newer plugin API ({m.AbstractionsVersion}).";
        if (!Version.TryParse(m.MinKbVersion, out var min) || min > Version.Parse(KbHost.Version)) return $"Needs version {m.MinKbVersion} or later.";
        if (m.UiApiVersion is not null && !Fits(m.UiApiVersion, KbHost.UiApiVersion)) return $"Built for a newer plugin UI ({m.UiApiVersion}).";
        if (m.Ui?.Script is not null && m.UiApiVersion is null) return "A plugin with a script needs uiApiVersion.";
        return PermissionProblem(m) ?? SettingProblem(m);
    }

    /// <summary>A setting whose bounds, default or option labels could never be right is refused before the plugin is installed.</summary>
    private static string? SettingProblem(PluginManifest m)
    {
        foreach (var s in m.Settings)
        {
            if (SignInSettings.IsReserved(s.Key)) return $"Setting {s.Key}: keys starting with \"{SignInSettings.Prefix}\" are the app's.";
            var secret = s.Secret || s.Type == "secret";
            if (s.Min is { } min && s.Max is { } max && min > max) return $"Setting {s.Key}: min is more than max.";
            if ((s.Min is not null || s.Max is not null) && s.Type != "number") return $"Setting {s.Key}: min and max are for a number.";
            if (s.Default is { } d)
            {
                if (secret) return $"Setting {s.Key}: a secret has no default.";
                if (SettingValue.Refusal(s, d) is { } why) return $"Setting {s.Key}: the default is refused: {why}";
            }
            if (s.OptionLabels is { } labels && labels.Keys.FirstOrDefault(k => s.Options?.Contains(k) != true) is { } stray)
                return $"Setting {s.Key}: optionLabels names \"{stray}\", which is not one of the options.";
        }
        return null;
    }

    /// <summary>A permission that could never be held, or that would not fit its column, is refused before the plugin is installed.</summary>
    private static string? PermissionProblem(PluginManifest m)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in m.Permissions)
        {
            if (p.Key.Length is 0 or > 100 || p.Key.Contains(':', StringComparison.Ordinal)) return "A permission key is 1 to 100 characters, with no colon.";
            if (!seen.Add(p.Key)) return $"Permission {p.Key} is listed twice.";
            if (p.Scope is not ("space" or "global")) return $"Permission {p.Key}: scope is space or global.";
            if (p.Scope == "space" && !(Enum.TryParse<Kb.Plugins.SpaceRole>(p.DefaultRole, out var role) && Enum.IsDefined(role) && role.ToString() == p.DefaultRole))
                return $"Permission {p.Key}: defaultRole is Reader, Contributor, Supervisor or Owner.";
        }
        return null;
    }

    private static bool Fits(string plugin, string host) =>
        Version.TryParse(plugin, out var p) && Version.Parse(host) is var h && p.Major == h.Major && p <= h;

    /// <summary>True when <paramref name="version"/> is at least <paramref name="minimum"/>. A text that is not a version is too low.</summary>
    public static bool AtLeast(string version, string minimum) =>
        Version.TryParse(version, out var v) && Version.TryParse(minimum, out var m) && v >= m;
}

/// <summary>The order to load plugins in, and the ones that cannot load, each with the reason (id to reason).</summary>
public sealed record PluginPlan(IReadOnlyList<string> LoadOrder, IReadOnlyDictionary<string, string> Refused);

public static class PluginGraph
{
    /// <summary>Requirements first; otherwise the order given. A plugin whose requirement is missing, too old, refused or in a circle is refused.</summary>
    public static PluginPlan Order(IReadOnlyCollection<PluginManifest> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, PluginManifest>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in enabled)
        {
            var prefix = PluginId.TablePrefix(m.Id);
            if (prefixes.TryGetValue(prefix, out var first)) refused[m.Id] = $"Conflicts with {first}.";
            else { prefixes[prefix] = m.Id; byId[m.Id] = m; }
        }

        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1: being visited, 2: done
        var path = new List<string>();

        bool Visit(string id)
        {
            if (refused.ContainsKey(id)) return false;
            if (state.TryGetValue(id, out var s))
            {
                if (s == 2) return true;
                foreach (var inCircle in path.Skip(path.IndexOf(id))) refused[inCircle] = "Circular requirement.";
                return false;
            }
            state[id] = 1;
            path.Add(id);
            foreach (var r in byId[id].Requires)
            {
                if (!byId.TryGetValue(r.Id, out var required)) refused.TryAdd(id, $"Requires {r.Id}.");
                else if (!PluginVersions.AtLeast(required.Version, r.MinVersion)) refused.TryAdd(id, $"Requires {r.Id} {r.MinVersion} or later.");
                else if (!Visit(r.Id)) refused.TryAdd(id, $"Requires {PluginManifests.NameOf(required)}.");
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            if (refused.ContainsKey(id)) return false;
            order.Add(id);
            return true;
        }

        foreach (var m in enabled) if (byId.ContainsKey(m.Id) && !refused.ContainsKey(m.Id)) Visit(m.Id);
        return new PluginPlan(order, refused);
    }

    /// <summary>Every plugin that requires <paramref name="id"/>, directly or through another plugin.</summary>
    public static IReadOnlyList<string> Dependents(string id, IReadOnlyCollection<PluginManifest> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var m in enabled)
                if (!seen.Contains(m.Id) && m.Requires.Any(r => seen.Contains(r.Id))) { seen.Add(m.Id); found.Add(m.Id); grew = true; }
        }
        return found;
    }

    /// <summary>
    /// The requirements of <paramref name="m"/> that <paramref name="available"/> does not meet. It takes what is available,
    /// not what was uploaded, so a marketplace can pass its catalogue.
    /// </summary>
    public static IReadOnlyList<PluginRequirement> Missing(PluginManifest m, IReadOnlyCollection<PluginManifest> available)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(available);
        return [.. m.Requires.Where(r => !available.Any(a => a.Id == r.Id && PluginVersions.AtLeast(a.Version, r.MinVersion)))];
    }
}
