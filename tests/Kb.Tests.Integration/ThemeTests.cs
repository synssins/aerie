using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Web;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration;

/// <summary>Theme colors: an Admin's overrides, stored, served with the brand to everyone (the sign-in page too), audited.</summary>
public sealed partial class ThemeTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task AnAdminsColors_AreServedToEveryone_Checked_Audited_AndResetByEmpty()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(JsonValueKind.Null, (await _f.NewClient().GetFromJsonAsync<JsonElement>("/api/v1/brand", Ct)).GetProperty("theme").ValueKind); // the default

        var r = await admin.PutAsJsonAsync("/api/v1/admin/brand/theme", new { day = new Dictionary<string, string> { ["--nav"] = "#2B3440", ["--accent"] = "#0f7" }, night = new Dictionary<string, string> { ["--nav"] = "#161b22" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Not signed in: the sign-in page gets the theme with the brand.
        var theme = (await _f.NewClient().GetFromJsonAsync<JsonElement>("/api/v1/brand", Ct)).GetProperty("theme");
        Assert.Equal("#2b3440", theme.GetProperty("day").GetProperty("--nav").GetString());
        Assert.Equal("#00ff77", theme.GetProperty("day").GetProperty("--accent").GetString());
        Assert.Equal("#161b22", theme.GetProperty("night").GetProperty("--nav").GetString());

        async Task<string?> FieldOf(object body)
        {
            var bad = await admin.PutAsJsonAsync("/api/v1/admin/brand/theme", body, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            return (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateObject().First().Name;
        }
        Assert.Equal("day.--nav", await FieldOf(new { day = new Dictionary<string, string> { ["--nav"] = "blue" } }));
        Assert.Equal("night.--paper", await FieldOf(new { night = new Dictionary<string, string> { ["--paper"] = "#12345" } }));
        Assert.Equal("day.--width", await FieldOf(new { day = new Dictionary<string, string> { ["--width"] = "#fff" } }));
        Assert.Equal("day.color", await FieldOf(new { day = new Dictionary<string, string> { ["color"] = "#fff" } }));

        await _f.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.BrandTheme, Ct);
            Assert.Contains("day --nav=#2b3440", entry.DataJson, StringComparison.Ordinal);
            Assert.Contains("night --nav=#161b22", entry.DataJson, StringComparison.Ordinal);
        });

        (await admin.PutAsJsonAsync("/api/v1/admin/brand/theme", new { day = new Dictionary<string, string>(), night = new Dictionary<string, string>() }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/brand", Ct)).GetProperty("theme").ValueKind);
    }

    [GeneratedRegex(@"""(--[a-z0-9-]+)""")]
    private static partial Regex TokenName();

    /// <summary>The server allows exactly the colors the Theme colors screen offers.</summary>
    [Fact]
    public void TheServersTokens_AreTheClients()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var ts = File.ReadAllText(Path.Combine(dir!.FullName, "client", "src", "themeTokens.ts"));
        var table = ts[ts.IndexOf("export const THEME_TOKENS", StringComparison.Ordinal)..ts.IndexOf("const BY_NAME", StringComparison.Ordinal)];
        // Each entry's first quoted --name is the token; a second one is what it follows.
        var names = table.Split('\n').Select(l => TokenName().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ThemeTokens.All.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }
}
