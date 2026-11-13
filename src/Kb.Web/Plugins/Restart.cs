namespace Kb.Web;

/// <summary>Restarts the app so that a plugin change takes effect. IIS and App Service start it again on the next request.</summary>
public interface IAppRestarter
{
    void Restart(string reason);
}

/// <summary>Stops the host a moment later, so the response that asked for the restart is sent first.</summary>
public sealed class HostRestarter(IHostApplicationLifetime lifetime, ILogger<HostRestarter> log) : IAppRestarter
{
    private int _asked;

    public void Restart(string reason)
    {
        if (Interlocked.Exchange(ref _asked, 1) == 1) return;
        log.LogWarning("Restarting: {Reason}", reason);
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
    }
}
