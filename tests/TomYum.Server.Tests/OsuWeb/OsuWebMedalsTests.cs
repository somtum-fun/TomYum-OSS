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
    [InlineData(1, "osu-combo-500", "Skill & Dedication", 0, 1, 0)]
    [InlineData(55, "osu-skill-pass-1", "Skill & Dedication", 1, 1, 0)]
    [InlineData(97, "taiko-skill-fc-3", "Skill & Dedication", 2, 3, 1)]
    [InlineData(110, "fruits-skill-fc-8", "Skill & Dedication", 2, 8, 2)]
    public void CatalogRows_GroupLikeTheProfilePage(int id, string slug, string grouping, int ordering, int progression, int mode)
    {
        var row = OsuWebMedals.CatalogRows().Single(r => r.Id == id);

        Assert.Equal((slug, grouping, ordering, progression, (int?)mode), (row.Slug, row.Grouping, row.Ordering, row.Progression, row.Mode));
    }

    [Fact]
    public void TheOfficialList_IsReadFromAProfilePage()
    {
        const string json = """{"achievements":[{"id":55,"name":"Rising Star","slug":"osu-skill-pass-1","description":"d","grouping":"Skill & Dedication","ordering":1,"mode":"osu","instructions":"<i>Pass</i>"},{"id":56,"name":"Constellation Prize","slug":"osu-skill-pass-2","description":"d2","grouping":"Skill & Dedication","ordering":1,"mode":"osu","instructions":null},{"id":124,"name":"Blindsight","slug":"all-intro-hidden","description":"d3","grouping":"Mod Introduction","ordering":0,"mode":null,"instructions":null}],"user":{}}""";
        var page = $"<div class=\"js-react\" data-initial-data=\"{System.Net.WebUtility.HtmlEncode(json)}\"></div>";

        var rows = OsuWebMedals.ParseOfficialList(page)!;

        Assert.Equal([55, 56, 124], rows.Select(r => r.Id));
        Assert.Equal(("Rising Star", "osu-skill-pass-1", 0, 1, "<i>Pass</i>"), (rows[0].Name, rows[0].Slug, rows[0].Mode, rows[0].Progression, rows[0].Instructions));
        Assert.Equal(2, rows[1].Progression); // second in its row
        Assert.Null(rows[2].Mode);
        Assert.Null(OsuWebMedals.ParseOfficialList("<html>changed</html>"));
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
