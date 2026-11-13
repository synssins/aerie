using Kb.Core.Config;
using Kb.Core.Notifications;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Kb.Tests.Unit;

public sealed class NotifierTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static (Notifier N, INotificationStore Store, IEmailSender Mail) Build(Dictionary<Guid, NotifyPref> prefs)
    {
        var store = Substitute.For<INotificationStore>();
        var dir = Substitute.For<IUserDirectory>();
        dir.GetNotifyPrefsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(prefs);
        dir.GetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((IReadOnlyCollection<Guid>)ci[0]).Select(g => $"{g}@x").ToList());
        var mail = Substitute.For<IEmailSender>();
        mail.ProviderId.Returns("smtp");
        var opts = Options.Create(new KbOptions { Email = new EmailOptions { Provider = "smtp" } });
        return (new Notifier(store, dir, new ProviderSelector<IEmailSender>([mail]), opts, new FakeTimeProvider(), Substitute.For<IPluginFailures>(), NullLogger<Notifier>.Instance), store, mail);
    }

    [Fact]
    public async Task UserPreferences_ControlInAppAndEmail()
    {
        var (n, store, mail) = Build(new() { [A] = new NotifyPref(InApp: false, Email: false), [B] = new NotifyPref(InApp: true, Email: true) });
        await n.NotifyAsync([A, B], NotificationKinds.AccessDecision, "t", null, null, alsoEmail: false, TestContext.Current.CancellationToken);

        await store.Received(1).AddAsync(Arg.Is<IEnumerable<Notification>>(x => x.Single().UserId == B), Arg.Any<CancellationToken>());
        await mail.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To.Single() == $"{B}@x"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecurityAlerts_IgnorePreferences()
    {
        var (n, store, mail) = Build(new() { [A] = new NotifyPref(InApp: false, Email: false) });
        await n.NotifyAsync([A], NotificationKinds.SecurityAlert, "t", null, null, alsoEmail: true, TestContext.Current.CancellationToken);

        await store.Received(1).AddAsync(Arg.Is<IEnumerable<Notification>>(x => x.Single().UserId == A), Arg.Any<CancellationToken>());
        await mail.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }
}
