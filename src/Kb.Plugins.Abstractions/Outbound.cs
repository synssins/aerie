namespace Kb.Plugins;

/// <summary>
/// Adds to an outbound HTTP request the core makes for one purpose (headers for an access gateway in front of the server it
/// calls, for example). It is given the request before it is sent and never sees the answer. A plugin registers one as a service.
/// </summary>
public interface IOutboundRequestDecorator
{
    Task DecorateAsync(OutboundRequest request, CancellationToken ct);
}

/// <summary>Purpose: what the call is for (<see cref="OutboundPurposes"/>); a decorator acts on the purposes it knows.</summary>
public sealed record OutboundRequest(string Purpose, HttpRequestMessage Message);

public static class OutboundPurposes
{
    /// <summary>The AI service's calls to its model server.</summary>
    public const string Ai = "ai";
}
