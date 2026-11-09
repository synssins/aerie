namespace Kb.Plugins;

// Extension points. Core depends only on these interfaces, never on a concrete plugin.
// Single-provider points (email, map, search, ai, dns, malware) are selected by config;
// multi-provider points (record importer, page action, export format, audit sink) are all active when enabled.
// Every implementation is registered with a provider id equal to its plugin manifest id.

public interface IProvider
{
    /// <summary>Plugin manifest id that supplies this implementation.</summary>
    string ProviderId { get; }
}

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string TextBody, string? HtmlBody = null);

public interface IEmailSender : IProvider
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed record MapTileConfig(string TileUrlTemplate, string Attribution, int MaxZoom);

public interface IMapProvider : IProvider
{
    MapTileConfig GetTileConfig();
}

public sealed record SearchDocument(string Id, Guid SpaceId, string Title, string Body, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt);
public sealed record SearchQuery(string Text, IReadOnlyCollection<Guid> VisibleSpaceIds, int Skip = 0, int Take = 20);
public sealed record SearchHit(string Id, Guid SpaceId, string Title, string Snippet, double Score);

public interface ISearchProvider : IProvider
{
    Task IndexAsync(SearchDocument doc, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
    /// <summary>Implementations MUST only return hits whose SpaceId is in <see cref="SearchQuery.VisibleSpaceIds"/>.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);
}

public interface IAiProvider : IProvider
{
    /// <summary>Content passed in is already permission-trimmed and stripped of secure links by the host.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}

public sealed record ImportedRecord(string ExternalId, string RecordType, IReadOnlyDictionary<string, string?> Fields);

public interface IRecordImporter : IProvider
{
    /// <summary>Source system name shown on synced fields, e.g. "Meraki".</summary>
    string SourceName { get; }
    IAsyncEnumerable<ImportedRecord> FetchAsync(CancellationToken ct);
}

public sealed record PageActionContext(Guid UserId, Guid SpaceId, Guid PageId, string PageTitle, string PageUrl, IReadOnlyDictionary<string, string> Input);
public sealed record PageActionResult(bool Success, string Message);

public interface IPageAction : IProvider
{
    string Label { get; }
    /// <summary>Input fields the UI prompts for before running, e.g. "ticketNumber".</summary>
    IReadOnlyList<string> InputFields { get; }
    Task<PageActionResult> RunAsync(PageActionContext context, CancellationToken ct);
}

public interface IExportFormat : IProvider
{
    string FileExtension { get; }
    string ContentType { get; }
    /// <summary>Input HTML is already sanitized and stripped of secure links.</summary>
    Task<Stream> ExportAsync(string title, string sanitizedHtml, CancellationToken ct);
}

public sealed record AuditRecord(long Sequence, DateTimeOffset At, string Action, Guid? ActorId, string? TargetType, string? TargetId, Guid? SpaceId, IReadOnlyDictionary<string, string> Data, string Hash);

public interface IAuditSink : IProvider
{
    /// <summary>Called after an audit entry is committed (e.g. SIEM forwarding). Failures must not throw into the caller.</summary>
    Task ForwardAsync(AuditRecord record, CancellationToken ct);
}

public enum ScanVerdict { Clean, Infected, Unknown }

public interface IMalwareScanner : IProvider
{
    Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken ct);
}

public interface IDnsProvider : IProvider
{
    Task CreateTxtRecordAsync(string zone, string name, string value, CancellationToken ct);
    Task DeleteTxtRecordAsync(string zone, string name, string value, CancellationToken ct);
}
