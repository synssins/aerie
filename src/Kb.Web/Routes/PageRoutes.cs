using Kb.Core.Content;

namespace Kb.Web;

public sealed record CreatePageRequest(Guid? ParentId, PageKind Kind, string Title, string[]? Tags = null, string? ContentJson = null);
public sealed record SavePageRequest(string Title, string ContentJson, string BaseVersion, bool Major = false, string? Note = null, string[]? Tags = null);

/// <summary>The pattern every route follows: bind the request, build the action, send it, answer. No check and no work happens here.</summary>
public static class PageRoutes
{
    public static void MapPageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/spaces/{id:guid}/tree", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadTree { SpaceId = id }, ct).ToHttp()).AllowAnonymous().WithAction<ReadTree>();

        api.MapPost("/spaces/{id:guid}/pages", (Guid id, CreatePageRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreatePage { SpaceId = id, ParentId = req.ParentId, Kind = req.Kind.ToString(), Title = req.Title, Tags = req.Tags, ContentJson = req.ContentJson }, ct)
                .ToHttp(page => Results.Created($"/api/v1/pages/{page.Id}", page))).WithAction<CreatePage>();

        api.MapGet("/pages/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPage { PageId = id }, ct).ToHttp()).AllowAnonymous().WithAction<ReadPage>();

        api.MapPut("/pages/{id:guid}", (Guid id, SavePageRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SavePage { PageId = id, Title = req.Title, ContentJson = req.ContentJson, BaseVersion = req.BaseVersion, Major = req.Major, Note = req.Note, Tags = req.Tags }, ct)
                .ToHttp()).WithAction<SavePage>();
    }
}
