using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>What the Data Center compatible tests share: people, Basic clients and reading RestError bodies.</summary>
internal static class DcKit
{
    public const string Password = "user-password-0123456789";
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Guid> UserAsync(KbAppFactory f, string name, Action<KbUser>? set = null)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var u = new KbUser { UserName = name, DisplayName = name + " Person", CreatedAt = DateTimeOffset.UtcNow };
        set?.Invoke(u);
        var r = await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(u, Password);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Description)));
        return u.Id;
    }

    /// <summary>The id of an existing account, or null.</summary>
    public static async Task<Guid?> UserIdAsync(KbAppFactory f, string name)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().FindByNameAsync(name))?.Id;
    }

    public static HttpClient Basic(KbAppFactory f, string name, string password = Password)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:{password}")));
        return c;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.StatusCode == expected, $"expected {(int)expected}, got {(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A RestError with this status: statusCode, message and reason.</summary>
    public static async Task ErrorAsync(this HttpResponseMessage r, HttpStatusCode expected)
    {
        var e = await r.JsonAsync(expected);
        Assert.Equal((int)expected, e.GetProperty("statusCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrEmpty(e.GetProperty("reason").GetString()));
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient c, string path, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await c.GetAsync(path, Ct)).JsonAsync(expected);

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string path, object body) => c.PostAsJsonAsync(path, body, Ct);
}
