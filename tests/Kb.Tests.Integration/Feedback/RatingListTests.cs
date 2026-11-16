using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>Slice R2: what the space's Supervisors and Owners see of ratings (contracts/behaviour/ratings.md, Who sees what; question Q-0029).</summary>
public sealed class RatingListTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync() =>
        _k = await FeedbackKit.CreateAsync(new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock))));
    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private string Ratings => $"/api/v1/pages/{_k.Page}/ratings";

    private async Task SaveAsync(HttpClient c, object body) => (await c.PutAsJsonAsync($"{Ratings}/mine", body, Ct)).EnsureSuccessStatusCode();

    private async Task<JsonElement> ListAsync(HttpClient c, string query = "") => await (await c.GetAsync($"{Ratings}/all{query}", Ct)).JsonAsync(HttpStatusCode.OK);

    // ratings.negative.visible_to_supervisors
    [Fact]
    public async Task Supervisors_SeeTheNegativeCount_AndWhoRatedWhatWhenAndWhy()
    {
        await SaveAsync(_k.Reader, new { value = "negative", reason = "Step 4 fails" });
        _clock.Advance(TimeSpan.FromMinutes(1));
        await SaveAsync(_k.Reader2, new { value = "positive", solved = true });
        foreach (var c in new[] { _k.Supervisor, _k.Owner })
        {
            var s = await (await c.GetAsync(Ratings, Ct)).JsonAsync(HttpStatusCode.OK);
            Assert.True(s.GetProperty("canSeeAll").GetBoolean());
            Assert.Equal((1, 1, 1), (s.GetProperty("positive").GetInt32(), s.GetProperty("solved").GetInt32(), s.GetProperty("negative").GetInt32()));
        }
        var list = await ListAsync(_k.Supervisor);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var entries = list.GetProperty("ratings").EnumerateArray().ToList();
        Assert.Equal(_k.Ids["rory"].ToString(), entries[0].GetProperty("user").GetProperty("id").GetString()); // newest change first
        var negative = entries[1];
        Assert.Equal(("user", _k.Ids["rita"].ToString()), (negative.GetProperty("user").GetProperty("type").GetString(), negative.GetProperty("user").GetProperty("id").GetString()));
        Assert.Equal(("negative", "Step 4 fails", "1.0"), (negative.GetProperty("value").GetString(), negative.GetProperty("reason").GetString(), negative.GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.String, negative.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, negative.GetProperty("updatedAt").ValueKind);

        var onlyNegative = await ListAsync(_k.Owner, "?value=negative");
        Assert.Equal(1, onlyNegative.GetProperty("total").GetInt32());
        Assert.Equal("negative", onlyNegative.GetProperty("ratings")[0].GetProperty("value").GetString());
        var paged = await ListAsync(_k.Owner, "?limit=1&offset=1");
        Assert.Equal((2, 1), (paged.GetProperty("total").GetInt32(), paged.GetProperty("ratings").GetArrayLength()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Owner.GetAsync($"{Ratings}/all?limit=201", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Owner.GetAsync($"{Ratings}/all?value=meh", Ct)).StatusCode);
    }

    // ratings.list.readers_refused, ratings.list.admin_without_role
    [Fact]
    public async Task ReadersContributors_AndAnAdminWithoutARole_AreRefused_AndSeeNoNegativeField()
    {
        await SaveAsync(_k.Reader, new { value = "negative" });
        foreach (var c in new[] { _k.Reader, _k.Contributor, _k.Admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{Ratings}/all", Ct)).StatusCode);
            var s = await (await c.GetAsync(Ratings, Ct)).JsonAsync(HttpStatusCode.OK);
            Assert.False(s.GetProperty("canSeeAll").GetBoolean());
            Assert.False(s.TryGetProperty("negative", out _));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.GetAsync($"{Ratings}/all", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.F.NewClient().GetAsync($"{Ratings}/all", Ct)).StatusCode);
    }

    // ratings.switch.page_off: Supervisors still see everything.
    [Fact]
    public async Task WithRatingsOff_SupervisorsStillSeeEverything()
    {
        await SaveAsync(_k.Reader, new { value = "negative", reason = "Bad" });
        await _k.PageSwitchAsync(_k.Page, ratings: false);
        var s = await (await _k.Supervisor.GetAsync(Ratings, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, s.GetProperty("negative").GetInt32());
        Assert.Equal(1, (await ListAsync(_k.Supervisor)).GetProperty("total").GetInt32());
    }
}
