using System.Net;
using System.Net.Mail;
using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Kb.Plugin.Email.Smtp.SmtpPlugin))]

namespace Kb.Plugin.Email.Smtp;

/// <summary>Email through an SMTP relay (unauthenticated, internal) or authenticated SMTP with STARTTLS.</summary>
public sealed class SmtpPlugin : IKbPlugin
{
    public const string Id = "kb.email.smtp";

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        services.AddSingleton<IEmailSender>(new SmtpEmailSender(context));
    }
}

public sealed class SmtpEmailSender(IPluginContext ctx) : IEmailSender
{
    public string ProviderId => SmtpPlugin.Id;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var host = ctx.GetSetting("Host") ?? throw new InvalidOperationException("kb.email.smtp: setting Host is required.");
        var port = int.TryParse(ctx.GetSetting("Port"), out var p) ? p : 25;
        var from = ctx.GetSetting("From") ?? throw new InvalidOperationException("kb.email.smtp: setting From is required.");
        var user = ctx.GetSetting("UserName");

        using var client = new SmtpClient(host, port) { EnableSsl = ctx.GetSetting("StartTls") != "false" && user is not null };
        if (user is not null)
            client.Credentials = new NetworkCredential(user, await ctx.GetSecretAsync("Password", ct) ?? "");

        using var mail = new MailMessage { From = new MailAddress(from), Subject = message.Subject, Body = message.TextBody };
        foreach (var to in message.To) mail.To.Add(to);
        await client.SendMailAsync(mail, ct);
    }
}
