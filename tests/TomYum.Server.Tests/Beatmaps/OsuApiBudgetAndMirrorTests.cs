using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Enums;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>One pace for all of osu!'s API, and map lookups from the mirror first.</summary>
public sealed class OsuApiBudgetAndMirrorTests
{
    // Trimmed from catboy.best's answer for https://osu.ppy.sh/beatmaps/5436733 (osu!'s API v2 format).
    private const string SetJson = """
        {"id": 2476607, "artist": "t+pazolite", "title": "Gumbarlzo!", "creator": "Ldnz", "ranked": 1, "status": "ranked",
         "genre_id": 10, "language_id": 5, "video": false, "storyboard": true, "source": "osu! World Cup 2025", "tags": "owc2025 featured artist",
         "ranked_date": "2026-01-05T20:25:33Z", "last_updated": "2025-12-29T20:10:31Z", "pack_tags": ["S1749", "FM37"], "track_id": 11619,
         "beatmaps": [{"id": 5436733, "beatmapset_id": 2476607, "checksum": "285b4d5d3de627e642431948c5c1fd3d", "version": "FOREVER LOVED!",
           "mode_int": 0, "ranked": 1, "difficulty_rating": 6.90278, "total_length": 365, "hit_length": 339, "max_combo": 2496,
           "cs": 3.8, "ar": 9.5, "accuracy": 9, "drain": 5, "bpm": 225, "count_circles": 1201, "count_sliders": 987, "count_spinners": 10,
           "last_updated": "2025-12-29T20:10:31Z"}]}
        """;

    [Fact]
    public async Task TheBudget_LetsThroughItsShare_ThenWaits()
    {
        using var budget = new OsuApiBudget(TimeProvider.System, perMinute: 2, window: TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();
        await budget.WaitAsync();
        await budget.WaitAsync();
        Assert.True(clock.ElapsedMilliseconds < 200);
        Assert.Equal(2, budget.UsedLastMinute);

        await budget.WaitAsync();   // the window has to move on first
        Assert.True(clock.ElapsedMilliseconds >= 350, $"waited only {clock.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData("https://old.ppy.sh/api/get_beatmaps?b=1", true)]
    [InlineData("https://osu.ppy.sh/api/v2/beatmapsets/1", true)]
    [InlineData("https://osu.ppy.sh/oauth/token", true)]
    [InlineData("https://osu.ppy.sh/osu/129891", false)]     // a .osu file, not the API
    [InlineData("https://assets.ppy.sh/medals/x.png", false)]
    [InlineData("https://catboy.best/api/v2/b/1", false)]
    public void OnlyOsusApi_CountsAgainstTheBudget(string url, bool counted) =>
        Assert.Equal(counted, OsuApiBudgetHandler.IsOsuApi(new Uri(url)));

    [Fact]
    public void TheMirrorsAnswer_IsReadLikeOsus()
    {
        var map = Assert.Single(BeatmapMirror.ParseSet(JsonDocument.Parse(SetJson).RootElement));

        Assert.Equal((5436733, 2476607, "285b4d5d3de627e642431948c5c1fd3d"), (map.Id, map.SetId, map.Md5));
        Assert.Equal(("t+pazolite", "Gumbarlzo!", "FOREVER LOVED!", "Ldnz"), (map.Artist, map.Title, map.Version, map.Creator));
        Assert.Equal((RankedStatus.Ranked, 1, 0), (map.Status, map.OsuApiStatus, map.Mode));
        Assert.Equal((6.90278, 365, 2496), (map.StarRating, map.TotalLength, map.MaxCombo));
        Assert.Equal((3.8f, 9.5f, 9f, 5f, 225f), (map.Details!.CircleSize, map.Details.ApproachRate, map.Details.OverallDifficulty, map.Details.DrainRate, map.Details.Bpm));
        Assert.Equal((339, 1201, 987, 10), (map.Details.HitLength, map.Details.CountCircles, map.Details.CountSliders, map.Details.CountSpinners));
        Assert.Equal((10, 5, false, true), (map.Details.GenreId, map.Details.LanguageId, map.Details.Video, map.Details.Storyboard));
        Assert.Equal(["S1749", "FM37"], map.Details.PackTags!);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 20, 25, 33, TimeSpan.Zero), map.Details.ApprovedDate);
    }

    [Fact]
    public async Task Lookups_AskTheMirrorFirst_AndOsuOnlyForWhatItDoesntKnow()
    {
        var mirrorCalls = new List<string>();
        var osuCalls = new List<string>();
        var mirror = new BeatmapMirror(
            new HttpClient(new Stub(path =>
            {
                mirrorCalls.Add(path);
                return path switch
                {
                    "/api/v2/s/2476607" => (HttpStatusCode.OK, SetJson),
                    "/api/v2/b/5436733" => (HttpStatusCode.OK, BeatmapWithSet()),
                    _ => (HttpStatusCode.NotFound, "{}"),
                };
            })) { BaseAddress = new Uri("https://catboy.best/") },
            new MirrorOptions(), new OsuApiBudget(TimeProvider.System, 1000), NullLogger<BeatmapMirror>.Instance);
        var osu = new OsuApiClient(
            new HttpClient(new Stub(path =>
            {
                osuCalls.Add(path);
                return (HttpStatusCode.OK, """[{"beatmap_id":"77","beatmapset_id":"7","file_md5":"00000000000000000000000000000077","approved":"1","artist":"A","title":"T","version":"V","creator":"C","mode":"0"}]""");
            })) { BaseAddress = new Uri("https://old.ppy.sh/api/") },
            new TomYum.Server.Beatmaps.OsuApiOptions { ApiKey = "key" }, NullLogger<OsuApiClient>.Instance);
        var client = new MirrorFirstOsuApiClient(mirror, osu);

        Assert.Equal("Gumbarlzo!", (await client.GetByIdAsync(5436733))!.Title);
        Assert.Single(await client.GetSetAsync(2476607));
        Assert.Empty(osuCalls);

        // Not on the mirror: osu! is asked.
        Assert.Equal(77, (await client.GetByIdAsync(77))!.Id);
        Assert.Single(osuCalls);
        Assert.Contains("/api/v2/b/77", mirrorCalls);
    }

    [Fact]
    public async Task A429_PausesTheMirrorUntilItsRetryAfter_AndBulkJobsNeverAskIt()
    {
        var calls = 0;
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var pace = new OsuApiBudget(TimeProvider.System, 1000);
        var mirror = new BeatmapMirror(
            new HttpClient(new Stub(_ =>
            {
                calls++;
                return (HttpStatusCode.TooManyRequests, "{}");
            }, retryAt)) { BaseAddress = new Uri("https://catboy.best/") },
            new MirrorOptions(), pace, NullLogger<BeatmapMirror>.Instance);

        Assert.Null(await mirror.GetByIdAsync(1));
        Assert.True(pace.IsPaused);
        Assert.Equal(retryAt.ToUnixTimeSeconds(), pace.PausedUntil.ToUnixTimeSeconds());
        Assert.Null(await mirror.GetByIdAsync(2));   // not asked while paused
        Assert.Equal(1, calls);

        var fresh = new BeatmapMirror(
            new HttpClient(new Stub(_ =>
            {
                calls++;
                return (HttpStatusCode.OK, SetJson);
            })) { BaseAddress = new Uri("https://catboy.best/") },
            new MirrorOptions(), new OsuApiBudget(TimeProvider.System, 1000), NullLogger<BeatmapMirror>.Instance);
        using (BeatmapMirror.Bypass())
            Assert.Empty(await fresh.GetSetAsync(2476607));
        Assert.Equal(1, calls);
        Assert.Single(await fresh.GetSetAsync(2476607));   // asked again once the job is over
        Assert.Equal(2, calls);
    }

    private static string BeatmapWithSet()
    {
        using var set = JsonDocument.Parse(SetJson);
        var map = set.RootElement.GetProperty("beatmaps")[0].GetRawText();
        return map.TrimEnd('}') + ", \"set\": " + SetJson + "}";
    }

    private sealed class Stub(Func<string, (HttpStatusCode Status, string Body)> answer, DateTimeOffset? retryAt = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAt is { } at)
                response.Headers.TryAddWithoutValidation("Retry-After", at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }
    }
}
