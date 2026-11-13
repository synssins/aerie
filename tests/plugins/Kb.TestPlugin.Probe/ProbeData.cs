using Kb.Plugins.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.TestPlugin.Probe;

public sealed class ProbeNote
{
    public int Id { get; set; }
    public required string Text { get; set; }
}

/// <summary>The probe's own table, registered when its Register setting is "data".</summary>
public sealed class ProbeData(PluginDbOptions options) : PluginDbContext(options)
{
    public DbSet<ProbeNote> Notes => Set<ProbeNote>();

    protected override void Build(ModelBuilder model) => model.Entity<ProbeNote>().ToTable("notes");
}

/// <summary>A context whose tables cannot be made, registered when the Register setting is "baddata".</summary>
public sealed class ProbeBrokenData(PluginDbOptions options) : PluginDbContext(options)
{
    protected override void Build(ModelBuilder model) => throw new InvalidOperationException("Probe tables failed.");
}

public sealed record NoteBody(string Text);

public static class ProbeDataRoutes
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/notes", async (NoteBody body, HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<ProbeData>() is not { } db) return Results.NotFound();
            db.Notes.Add(new ProbeNote { Text = body.Text });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        group.MapGet("/notes", async (HttpContext http, CancellationToken ct) =>
            http.RequestServices.GetService<ProbeData>() is { } db ? Results.Ok(await db.Notes.OrderBy(n => n.Id).Select(n => n.Text).ToListAsync(ct)) : Results.NotFound());
    }
}
