using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.Plugin.Email.Graph.GraphPlugin))]

namespace Kb.Plugin.Email.Graph;

/// <summary>Email through Microsoft Graph sendMail, authenticated with an app registration certificate (no client secret).</summary>
public sealed class GraphPlugin : IKbPlugin
{
    public const string Id = "kb.email.graph";

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        services.AddSingleton<IEmailSender>(sp => new GraphEmailSender(context, sp.GetRequiredService<IHttpClientFactory>()));
    }
}

public sealed class GraphEmailSender(IPluginContext ctx, IHttpClientFactory http) : IEmailSender
{
    public string ProviderId => GraphPlugin.Id;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        string Req(string k) => ctx.GetSetting(k) ?? throw new InvalidOperationException($"kb.email.graph: setting {k} is required.");
        var thumb = Req("CertificateThumbprint");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false).OfType<X509Certificate2>().FirstOrDefault()
            ?? throw new InvalidOperationException($"kb.email.graph: certificate {thumb} not found in CurrentUser\\My.");

        var cred = new ClientCertificateCredential(Req("TenantId"), Req("ClientId"), cert);
        var token = await cred.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);

        var client = http.CreateClient(PluginHttp.ClientName(GraphPlugin.Id));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(Req("SenderMailbox"))}/sendMail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        req.Content = JsonContent.Create(new
        {
            message = new
            {
                subject = message.Subject,
                body = new { contentType = "Text", content = message.TextBody },
                toRecipients = message.To.Select(a => new { emailAddress = new { address = a } }),
            },
            saveToSentItems = false,
        });
        using var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
