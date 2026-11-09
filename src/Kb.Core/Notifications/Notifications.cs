using Kb.Core.Config;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kb.Core.Notifications;

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public static class NotificationKinds
{
    public const string AccessRequest = "access.request";
    public const string AccessDecision = "access.decision";
    public const string SecurityAlert = "security.alert";
}

public interface INotificationStore
{
    Task AddAsync(IEnumerable<Notification> items, CancellationToken ct);
}

/// <summary>In-app notifications plus optional email through the configured email-sender plugin.</summary>
public interface INotifier
{
    Task NotifyAsync(IReadOnlyCollection<Guid> userIds, string kind, string title, string? body, string? link, bool alsoEmail, CancellationToken ct = default);
}

public interface IUserDirectory
{
    Task<IReadOnlyList<string>> GetEmailsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
    Task<IReadOnlyList<Guid>> GetAdminIdsAsync(CancellationToken ct);
}

public sealed class Notifier(
    INotificationStore store,
    IUserDirectory users,
    ProviderSelector<IEmailSender> senders,
    IOptions<KbOptions> options,
    TimeProvider time,
    ILogger<Notifier> log) : INotifier
{
    public async Task NotifyAsync(IReadOnlyCollection<Guid> userIds, string kind, string title, string? body, string? link, bool alsoEmail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0) return;
        var now = time.GetUtcNow();
        await store.AddAsync(userIds.Select(u => new Notification { UserId = u, Kind = kind, Title = title, Body = body, Link = link, CreatedAt = now }), ct);
        if (!alsoEmail) return;

        var sender = senders.Get(options.Value.Email.Provider);
        var to = await users.GetEmailsAsync(userIds, ct);
        if (sender is null)
        {
            // Dev / unconfigured: never silently drop a security email.
            log.LogWarning("No email provider configured; email not sent. Subject: {Subject}; recipients: {Count}", title, to.Count);
            return;
        }
        if (to.Count == 0) return;
        try
        {
            await sender.SendAsync(new EmailMessage(to, title, body ?? title), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // In-app notification already stored; email failure must not undo the action that triggered it.
            log.LogError(ex, "Email send failed via {Provider}", sender.ProviderId);
        }
    }
}
