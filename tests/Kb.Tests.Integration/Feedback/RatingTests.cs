using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>Slice R1: page ratings (contracts/behaviour/ratings.md; questions Q-0025, Q-0027, Q-0028, Q-0029).</summary>
public sealed class RatingTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync() =>
        _k = await FeedbackKit.CreateAsync(new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock))));
    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private string Ratings(Guid? page = null) => $"/api/v1/pages/{page ?? _k.Page}/ratings";

    private async Task<JsonElement> SummaryAsync(HttpClient c, Guid? page = null) => await (await c.GetAsync(Ratings(page), Ct)).JsonAsync(HttpStatusCode.OK);

    private async Task<JsonElement> SaveAsync(HttpClient c, object body, Guid? page = null) =>
        await (await c.PutAsJsonAsync($"{Ratings(page)}/mine", body, Ct)).JsonAsync(HttpStatusCode.OK);

    private Task<HttpResponseMessage> TrySaveAsync(HttpClient c, object body, Guid? page = null) => c.PutAsJsonAsync($"{Ratings(page)}/mine", body, Ct);

    private static (int Positive, int Solved) Counts(JsonElement s) => (s.GetProperty("positive").GetInt32(), s.GetProperty("solved").GetInt32());

    // ratings.guest.refused, ratings.unreadable.hidden, ratings.folder.refused, ratings.token.refused
    [Fact]
    public async Task Guests_Outsiders_Folders_AndTokens_AreRefused()
    {
        var open = await _k.SpaceAsync("Open", "open", "Anyone");
        var page = await _k.PageAsync(open, "Public page");
        var guest = _k.F.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(Ratings(page), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrySaveAsync(guest, new { value = "positive" }, page)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.GetAsync(Ratings(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TrySaveAsync(_k.Outsider, new { value = "positive" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TrySaveAsync(_k.Reader, new { value = "positive" }, _k.Folder)).StatusCode);
        var token = await _k.TokenAsync(_k.Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync(Ratings(), Ct)).StatusCode); // Q-0039
        Assert.Equal(HttpStatusCode.Forbidden, (await TrySaveAsync(token, new { value = "positive" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync($"{Ratings()}/mine", Ct)).StatusCode);
    }

    // ratings.positive, ratings.solved
    [Fact]
    public async Task Positive_AndSolved_AreCounted_AndTheSummaryHasNoNegativeField()
    {
        var s = await SaveAsync(_k.Reader, new { value = "positive" });
        Assert.Equal((1, 0), Counts(s));
        Assert.Equal("positive", s.GetProperty("mine").GetProperty("value").GetString());
        Assert.False(s.TryGetProperty("negative", out _));
        Assert.Equal("1.0", s.GetProperty("mine").GetProperty("version").GetString());
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.True(s.GetProperty("canRate").GetBoolean());
        Assert.False(s.GetProperty("canSeeAll").GetBoolean());

        await SaveAsync(_k.Reader2, new { value = "positive", solved = true });
        var other = await SummaryAsync(_k.Contributor);
        Assert.Equal((2, 1), Counts(other));
        Assert.Equal(JsonValueKind.Null, other.GetProperty("mine").ValueKind);
        Assert.False(other.TryGetProperty("negative", out _));
    }

    // ratings.solved_with_negative.refused, ratings.reason.rules
    [Fact]
    public async Task TheRules_OfValueSolvedAndReason()
    {
        foreach (var bad in new object[]
                 {
                     new { value = "negative", solved = true }, new { value = "positive", reason = "Nice" }, new { value = "negative", reason = new string('r', 281) },
                     new { value = "meh" }, new { value = (string?)null },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await TrySaveAsync(_k.Reader, bad)).StatusCode);
        var s = await SaveAsync(_k.Reader, new { value = "negative", reason = "  " + new string('r', 280) + "  " });
        Assert.Equal(280, s.GetProperty("mine").GetProperty("reason").GetString()!.Length);
        // Empty after trimming is stored as no reason.
        s = await SaveAsync(_k.Reader, new { value = "negative", reason = "   " });
        Assert.Equal(JsonValueKind.Null, s.GetProperty("mine").GetProperty("reason").ValueKind);
    }

    // ratings.negative.hidden_from_readers
    [Fact]
    public async Task ANegative_IsHiddenFromEveryReader_ButItsOwnAuthor()
    {
        await SaveAsync(_k.Reader, new { value = "negative", reason = "Step 4 fails" });
        foreach (var c in new[] { _k.Reader2, _k.Contributor, _k.Admin })
        {
            var s = await SummaryAsync(c);
            Assert.Equal((0, 0), Counts(s));
            Assert.False(s.TryGetProperty("negative", out _));
        }
        var mine = (await SummaryAsync(_k.Reader)).GetProperty("mine");
        Assert.Equal(("negative", "Step 4 fails"), (mine.GetProperty("value").GetString(), mine.GetProperty("reason").GetString()));
    }

    // ratings.one_per_person, ratings.remove
    [Fact]
    public async Task OneRatingPerPerson_ChangingItMovesTheCounts_AndRemovingIsAlways204()
    {
        var first = await SaveAsync(_k.Reader, new { value = "negative" });
        var created = first.GetProperty("mine").GetProperty("createdAt").GetDateTimeOffset();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var s = await SaveAsync(_k.Reader, new { value = "positive", solved = true });
        Assert.Equal((1, 1), Counts(s));
        Assert.Equal(created, s.GetProperty("mine").GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(s.GetProperty("mine").GetProperty("updatedAt").GetDateTimeOffset() > created);
        Assert.Equal(1, await _k.DbAsync(db => db.Ratings.CountAsync(Ct)));

        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"{Ratings()}/mine", Ct)).StatusCode);
        Assert.Equal((0, 0), Counts(await SummaryAsync(_k.Reader)));
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"{Ratings()}/mine", Ct)).StatusCode);
    }

    // ratings.switch.page_off, ratings.switch.no_override
    [Fact]
    public async Task Off_StopsRating_HidesCountsFromReaders_NotFromSupervisors_AndRemovingStillWorks()
    {
        await SaveAsync(_k.Reader, new { value = "positive" });
        await _k.PageSwitchAsync(_k.Page, ratings: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TrySaveAsync(_k.Reader2, new { value = "positive" })).StatusCode);
        var reader = await SummaryAsync(_k.Reader2);
        Assert.False(reader.GetProperty("enabled").GetBoolean());
        Assert.False(reader.GetProperty("canRate").GetBoolean());
        Assert.Equal((0, 0), Counts(reader));
        Assert.Equal((1, 0), Counts(await SummaryAsync(_k.Supervisor)));
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"{Ratings()}/mine", Ct)).StatusCode);

        await _k.PageSwitchAsync(_k.Page, ratings: true);
        await _k.SiteAsync(comments: true, ratings: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TrySaveAsync(_k.Reader2, new { value = "positive" })).StatusCode);
    }

    [Fact]
    public async Task The31stChangeInAMinute_Is429_WithRetryAfter()
    {
        for (var i = 0; i < 30; i++) await SaveAsync(_k.Reader, new { value = i % 2 == 0 ? "positive" : "negative" });
        var r = await TrySaveAsync(_k.Reader, new { value = "positive" });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await SaveAsync(_k.Reader, new { value = "positive" });
    }

    // ratings.audit.no_value
    [Fact]
    public async Task SaveAndRemove_AreAudited_WithoutTheValueOrTheReason()
    {
        await SaveAsync(_k.Reader, new { value = "negative", reason = "Marker-reason-77" });
        (await _k.Reader.DeleteAsync($"{Ratings()}/mine", Ct)).EnsureSuccessStatusCode();
        foreach (var action in new[] { AuditActions.RatingSave, AuditActions.RatingRemove })
        {
            var a = Assert.Single(await _k.AuditAsync(action));
            Assert.Equal(("page", _k.Page.ToString()), (a.TargetType, a.TargetId));
            Assert.DoesNotContain("negative", a.DataJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Marker-reason", a.DataJson, StringComparison.Ordinal);
        }
    }

    // ratings.search.unchanged
    [Fact]
    public async Task Ratings_DoNotChangeSearch()
    {
        async Task<string> SearchAsync() => (await (await _k.Reader.GetAsync("/api/v1/search?q=Runbook", Ct)).Content.ReadAsStringAsync(Ct));
        var before = await SearchAsync();
        foreach (var c in new[] { _k.Reader, _k.Reader2, _k.Contributor, _k.Supervisor, _k.Owner })
            await SaveAsync(c, new { value = "negative", reason = "Bad" });
        Assert.Equal(before, await SearchAsync());
    }

    // Ratings move with the page and follow the new space; a trashed page keeps them.
    [Fact]
    public async Task Ratings_FollowThePage()
    {
        await SaveAsync(_k.Reader, new { value = "positive" });
        var elsewhere = await _k.SpaceAsync("Elsewhere", "else");
        (await _k.Admin.PostAsJsonAsync($"/api/v1/pages/{_k.Page}/move", new { spaceId = elsewhere }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.GetAsync(Ratings(), Ct)).StatusCode);
        Assert.Equal((1, 0), Counts(await SummaryAsync(_k.Admin)));
        (await _k.Admin.DeleteAsync($"/api/v1/pages/{_k.Page}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(1, await _k.DbAsync(db => db.Ratings.CountAsync(Ct)));
    }
}
