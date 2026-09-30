using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.LastFm;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Plays scrobbled to Last.fm.</summary>
public sealed class LastFmTests : OsuWebDatabaseTest
{
    [Fact]
    public void Calls_AreSignedTheWayLastFmSays()
    {
        var parameters = new Dictionary<string, string> { ["token"] = "tok", ["method"] = "auth.getSession", ["api_key"] = "key", ["format"] = "json" };

        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("api_keykeymethodauth.getSessiontokentoksecret")));
        Assert.Equal(expected, LastFmClient.Sign(parameters, "secret"));
    }

    [Theory]
    [InlineData("""{"mods":[]}""", 1.0)]
    [InlineData("""{"mods":[{"acronym":"HD"},{"acronym":"DT"}]}""", 1.5)]
    [InlineData("""{"mods":[{"acronym":"NC","settings":{"speed_change":1.3}}]}""", 1.3)]
    [InlineData("""{"mods":[{"acronym":"HT"}]}""", 0.75)]
    [InlineData("not json", 1.0)]
    public void SpeedMods_ChangeHowLongTheSongPlays(string data, double speed) =>
        Assert.Equal(speed, LastFmScrobbler.Speed(data));

    [Fact]
    public void Songs_UseTheirOwnNames_AndTheirLengthAtThatSpeed()
    {
        Assert.Equal(new LastFmTrack("米津玄師", "Lemon", 160), LastFmScrobbler.Track("Kenshi Yonezu", "米津玄師", "Lemon", "", 240, 1.5));
        Assert.Equal(new LastFmTrack("xi", "FREEDOM DiVE", 263), LastFmScrobbler.Track("xi", "", "FREEDOM DiVE", "", 263));
    }

    [Fact]
    public void Listening_IsTheTrackLastFmMarksNowPlaying()
    {
        using var playing = System.Text.Json.JsonDocument.Parse(
            """{"recenttracks":{"track":[{"artist":{"#text":"xi"},"name":"FREEDOM DiVE","url":"https://www.last.fm/music/xi/_/FREEDOM+DiVE","@attr":{"nowplaying":"true"}},{"artist":{"#text":"A"},"name":"Old","date":{"uts":"1790700000"}}]}}""");
        var recent = LastFmClient.Recent(playing.RootElement);
        Assert.Equal(2, recent.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790700000), recent[1].At);
        Assert.Equal(new LastFmListening("xi", "FREEDOM DiVE", "https://www.last.fm/music/xi/_/FREEDOM+DiVE"), LastFmClient.Listening(recent));

        using var idle = System.Text.Json.JsonDocument.Parse("""{"recenttracks":{"track":{"artist":{"#text":"A"},"name":"Old"}}}""");
        Assert.Null(LastFmClient.Listening(LastFmClient.Recent(idle.RootElement)));
    }

    [MySqlFact]
    public async Task PassedPlays_OfConnectedPlayers_AreScrobbled()
    {
        var db = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var fan = (await users.AddAsync(new User { Username = "Fan", SafeName = "fan", Email = "f@x.dev", PasswordBcrypt = "x", Country = "nl" })).Id;
        var other = (await users.AddAsync(new User { Username = "Other", SafeName = "other", Email = "o@x.dev", PasswordBcrypt = "x", Country = "nl" })).Id;
        db.LastFmLinks.Add(new LastFmLink { UserId = fan, Username = "fan_on_lastfm", SessionKey = "session", LinkedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var osuWeb = NewOsuWebContext();
        var maps = new OsuWebMirroringBeatmapRepository(new BeatmapRepository(NewTomYumContext()), new OsuWebBeatmapMirror(osuWeb), osuWeb);
        await maps.UpsertAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "Kenshi Yonezu", Title = "Lemon", Creator = "M" });
        await maps.UpsertAsync(new Beatmap { Id = 200, SetId = 20, Md5 = "md5-200", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Short", Artist = "A", Title = "Jingle", Creator = "M" });
        await ExecuteAsync("UPDATE osu_beatmaps SET total_length = IF(beatmap_id = 100, 240, 20); UPDATE osu_beatmapsets SET artist_unicode = '米津玄師' WHERE beatmapset_id = 10");

        var lastFm = new FakeLastFm();
        using var services = new ServiceCollection()
            .AddScoped(_ => NewTomYumContext())
            .AddScoped(_ => new OsuWebScrobbles(NewOsuWebContext()))
            .BuildServiceProvider();
        var scrobbler = new LastFmScrobbler(services.GetRequiredService<IServiceScopeFactory>(),
            new LastFmClient(new HttpClient(lastFm), new LastFmOptions { ApiKey = "key", ApiSecret = "secret" }), NullLogger<LastFmScrobbler>.Instance);
        Assert.Equal(0, await scrobbler.ScrobbleAsync()); // starts at the newest score

        await InsertAsync(fan, 100, passed: true, """{"mods":[{"acronym":"DT"}]}""", legacyScoreId: 77); // from stable
        await InsertAsync(fan, 100, passed: false, "{}");     // failed: not listened through
        await InsertAsync(other, 100, passed: true, "{}");    // not connected
        await InsertAsync(fan, 200, passed: true, "{}");      // 20 seconds: too short for Last.fm
        Assert.Equal(1, await scrobbler.ScrobbleAsync());
        Assert.Equal(0, await scrobbler.ScrobbleAsync());     // not twice

        var call = Assert.Single(lastFm.Calls);
        Assert.Equal(("track.scrobble", "session", "米津玄師", "Lemon", "160"), (call["method"], call["sk"], call["artist"], call["track"], call["duration"]));
        var logged = Assert.Single(await NewTomYumContext().LastFmScrobbles.ToListAsync());
        Assert.Equal((fan, "米津玄師", "Lemon", "stable", 100), (logged.UserId, logged.Artist, logged.Title, logged.Client, logged.BeatmapId));

        await InsertAsync(fan, 100, passed: true, "{}");      // from lazer
        Assert.Equal(1, await scrobbler.ScrobbleAsync());
        Assert.Equal("lazer", (await NewTomYumContext().LastFmScrobbles.OrderByDescending(x => x.Id).FirstAsync()).Client);

        // The settings page's history: Last.fm's (Spotify too), with the plays from here saying where they were played.
        var lazerPlay = await NewTomYumContext().LastFmScrobbles.OrderByDescending(x => x.Id).FirstAsync();
        var uts = new DateTimeOffset(DateTime.SpecifyKind(lazerPlay.ScrobbledAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        lastFm.Answer = $$$"""{"recenttracks":{"track":[{"artist":{"#text":"Olivia Rodrigo"},"name":"vampire","@attr":{"nowplaying":"true"}},{"artist":{"#text":"米津玄師"},"name":"Lemon","date":{"uts":"{{{uts}}}"}}]}}""";
        var history = await LastFmEndpoints.RecentAsync(
            new LastFmClient(new HttpClient(lastFm), new LastFmOptions { ApiKey = "key", ApiSecret = "secret" }), NewTomYumContext(),
            await NewTomYumContext().LastFmLinks.SingleAsync(), CancellationToken.None);
        Assert.Equal(("vampire", true, (string?)null), (history[0].Title, history[0].NowPlaying, history[0].Client));
        Assert.Equal(("Lemon", "lazer", (int?)100), (history[1].Title, history[1].Client, history[1].BeatmapId));
        lastFm.Answer = """{"scrobbles":{}}""";

        // Access taken away on Last.fm: disconnected here too.
        lastFm.Answer = """{"error":9,"message":"Invalid session key"}""";
        await InsertAsync(fan, 100, passed: true, "{}");
        Assert.Equal(0, await scrobbler.ScrobbleAsync());
        Assert.False(await NewTomYumContext().LastFmLinks.AnyAsync());
    }

    private Task InsertAsync(int userId, int beatmapId, bool passed, string data, int? legacyScoreId = null) => ExecuteAsync($$"""
        INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, ended_at)
        VALUES ({{userId}}, 0, {{beatmapId}}, 1, 1, '{{(passed ? "S" : "F")}}', {{(passed ? 1 : 0)}}, 0.98, 400, 900000, '{{data}}', NULL, {{(legacyScoreId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL")}}, UTC_TIMESTAMP())
        """);

    private sealed class FakeLastFm : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Calls { get; } = [];

        public string Answer { get; set; } = """{"scrobbles":{}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Calls.Add(form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1])));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        }
    }
}
