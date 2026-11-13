using System.Text.Json;
using Kb.Plugins;
using Kb.Web.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

public sealed class HttpOutcomeTests
{
    private sealed record Thing(string Name);

    private static async Task<(int Status, JsonElement? Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        return (http.Response.StatusCode, text.Length == 0 ? null : JsonDocument.Parse(text).RootElement);
    }

    private static Outcome<T> Bad<T>(Problem p) => p;

    [Fact]
    public async Task ToHttp_MapsEveryStatus()
    {
        var ok = await RunAsync(((Outcome<Thing>)new Thing("a")).ToHttp());
        Assert.Equal((200, "a"), (ok.Status, ok.Body!.Value.GetProperty("name").GetString()));
        Assert.Equal((204, null), await RunAsync(((Outcome<Unit>)default(Unit)).ToHttp()));
        var created = await RunAsync(((Outcome<Thing>)new Thing("a")).ToHttp(t => Results.Created("/x", t)));
        Assert.Equal(201, created.Status);

        var invalid = await RunAsync(Bad<Thing>(Problem.Invalid("name", "Required.")).ToHttp());
        Assert.Equal((400, "Required."), (invalid.Status, invalid.Body!.Value.GetProperty("errors").GetProperty("name")[0].GetString()));
        var bad = await RunAsync(Bad<Thing>(new Problem(400, "Not a plugin package.")).ToHttp());
        Assert.Equal((400, "Not a plugin package."), (bad.Status, bad.Body!.Value.GetProperty("detail").GetString()));

        Assert.Equal(401, (await RunAsync(Bad<Thing>(Problem.Unauthorized).ToHttp())).Status);
        Assert.Equal(403, (await RunAsync(Bad<Thing>(Problem.Forbidden).ToHttp())).Status);
        Assert.Equal(404, (await RunAsync(Bad<Thing>(Problem.NotFound).ToHttp())).Status);

        var conflictBody = await RunAsync(Bad<Thing>(Problem.Conflict(body: new { current = "1.2" })).ToHttp());
        Assert.Equal((409, "1.2"), (conflictBody.Status, conflictBody.Body!.Value.GetProperty("current").GetString()));
        var conflict = await RunAsync(Bad<Thing>(Problem.Conflict("In use.")).ToHttp());
        Assert.Equal((409, "In use."), (conflict.Status, conflict.Body!.Value.GetProperty("detail").GetString()));

        var refused = await RunAsync(Bad<Thing>(Problem.Refused("A plugin said no.")).ToHttp());
        Assert.Equal((422, "A plugin said no."), (refused.Status, refused.Body!.Value.GetProperty("detail").GetString()));
        var locked = await RunAsync(Bad<Thing>(Problem.Locked("Locked.")).ToHttp());
        Assert.Equal((423, "Locked."), (locked.Status, locked.Body!.Value.GetProperty("detail").GetString()));

        var later = await RunAsync(await Task.FromResult<Outcome<Thing>>(new Thing("b")).ToHttp());
        Assert.Equal(200, later.Status);
    }
}
