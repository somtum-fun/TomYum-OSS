using System.Net;
using TomYum.Domain.Entities;
using TomYum.Server.Achievements;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Medal catalog rows and the InterOp client (no database needed).</summary>
public class OsuWebMedalsTests
{
    [Fact]
    public void CatalogRows_CoverEveryMedalWithUniqueIds()
    {
        var rows = OsuWebMedals.CatalogRows();

        Assert.Equal(MedalCatalog.All.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r =>
        {
            Assert.InRange(r.Name.Length, 1, 40);   // osu_achievements.name
            Assert.InRange(r.Grouping.Length, 1, 30);
            Assert.InRange(r.Ordering, 0, 255);      // tinyint unsigned
            Assert.InRange(r.Progression, 0, 255);
        });
    }

    [Theory]
    [InlineData(21, "osu-combo-500", "Skill", 0, 1, 0)]        // combo row, first tier
    [InlineData(1, "osu-skill-pass-1", "Skill", 1, 1, 0)]      // pass row, tier 1
    [InlineData(35, "taiko-skill-fc-3", "Skill", 2, 3, 1)]     // full-combo row, taiko
    [InlineData(24, "osu-combo-2000", "Skill", 0, 4, 0)]
    [InlineData(56, "fruits-skill-fc-8", "Skill", 2, 8, 2)]
    public void CatalogRows_GroupLikeTheProfilePage(int id, string slug, string grouping, int ordering, int progression, int mode)
    {
        var row = OsuWebMedals.CatalogRows().Single(r => r.Id == id);

        Assert.Equal((slug, grouping, ordering, progression, (int?)mode), (row.Slug, row.Grouping, row.Ordering, row.Progression, row.Mode));
    }

    [Fact]
    public void ModIntroMedals_AreModeAgnostic()
    {
        var hidden = OsuWebMedals.CatalogRows().Single(r => r.Slug == "all-intro-hidden");

        Assert.Equal("Mod Introduction", hidden.Grouping);
        Assert.Null(hidden.Mode);
    }

    [Fact]
    public void Sign_MatchesOsuWebsHmac()
    {
        var interop = new OsuWebInterop(
            new HttpClient(),
            new OsuWebInteropOptions { InteropUrl = "http://nginx/", InteropSecret = "test-secret" },
            new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000)));

        var (url, signature) = interop.Sign("/_lio/user-achievement/3/21/100");

        Assert.Equal("http://nginx/_lio/user-achievement/3/21/100?timestamp=1790000000", url.ToString());
        // hash_hmac('sha1', url, 'test-secret'), computed independently.
        Assert.Equal("f9f686f8b10aed36ebd6057aad30d905e80fb614", signature);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)] // already unlocked
    public async Task UnlockMedal_PostsASignedRequest(HttpStatusCode status, bool expected)
    {
        var handler = new RecordingHandler(status);
        var interop = new OsuWebInterop(
            new HttpClient(handler),
            new OsuWebInteropOptions { InteropUrl = "http://nginx", InteropSecret = "s" });

        var unlocked = await interop.UnlockMedalAsync(3, 21, 100);

        Assert.Equal(expected, unlocked);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.StartsWith("/_lio/user-achievement/3/21/100?timestamp=", handler.Request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", handler.Request.Headers.GetValues("X-LIO-Signature").Single());
    }

    [Fact]
    public async Task UnlockMedal_OtherFailuresThrow()
    {
        var interop = new OsuWebInterop(
            new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden)),
            new OsuWebInteropOptions { InteropUrl = "http://nginx", InteropSecret = "wrong" });

        await Assert.ThrowsAsync<HttpRequestException>(() => interop.UnlockMedalAsync(3, 21, null));
    }

    [Fact]
    public async Task NoOsuWebSync_DoesNothing()
    {
        var sync = new NoOsuWebSync();

        await sync.MedalsUnlockedAsync(1, [MedalCatalog.All[0]], 1);
        await sync.AccountCreatedAsync(1);
        await sync.ScoreSubmittedAsync(new Score());
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
