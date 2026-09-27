using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Guards the MD5→beatmap resolution in <see cref="OsuApiClient"/>. The keyless osu.direct mirror
/// ignores the <c>h=</c> filter and returns EVERY difficulty in the set (each with its own
/// <c>file_md5</c>); the resolver must pick the difficulty whose md5 actually matches the requested
/// hash, not the first entry. Taking the first entry filed plays on non-first difficulties under the
/// wrong beatmap id and computed pp against the wrong map (the "1★ convert scored as 4★" bug).
/// </summary>
public class OsuApiClientResolutionTests
{
    // A set response where the requested md5 ("bbb...") is NOT the first entry — mirrors what
    // osu.direct returns for a multi-difficulty mapset.
    private const string SetJson = """
    [
      {"file_md5":"aaa111","beatmapset_id":859783,"beatmap_id":1797542,"difficultyrating":5.18,"approved":1,"mode":0},
      {"file_md5":"bbb222","beatmapset_id":859783,"beatmap_id":1801936,"difficultyrating":1.02,"approved":1,"mode":0},
      {"file_md5":"ccc333","beatmapset_id":859783,"beatmap_id":1849505,"difficultyrating":6.40,"approved":1,"mode":0}
    ]
    """;

    private static OsuApiClient ClientReturning(string json)
    {
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://osu.direct/api/") };
        return new OsuApiClient(http, new OsuApiOptions { ApiKey = null }, NullLogger<OsuApiClient>.Instance);
    }

    [Fact]
    public async Task GetByMd5Async_SelectsEntryMatchingHash_NotFirst()
    {
        var client = ClientReturning(SetJson);

        var result = await client.GetByMd5Async("bbb222");

        Assert.NotNull(result);
        Assert.Equal(1801936, result!.Id);   // the difficulty whose md5 matches — NOT 1797542 (first)
        Assert.Equal("bbb222", result.Md5);
    }

    [Fact]
    public async Task GetByMd5Async_IsCaseInsensitive()
    {
        var client = ClientReturning(SetJson);

        var result = await client.GetByMd5Async("BBB222");

        Assert.NotNull(result);
        Assert.Equal(1801936, result!.Id);
    }

    [Fact]
    public async Task GetByMd5Async_ReturnsNull_WhenNoEntryMatches()
    {
        var client = ClientReturning(SetJson);

        var result = await client.GetByMd5Async("does-not-exist");

        Assert.Null(result);   // never guess a difficulty when the hash isn't present
    }

    [Fact]
    public async Task GetSetAsync_ReturnsEveryDifficultyOfTheSet()
    {
        var client = ClientReturning(SetJson);

        var set = await client.GetSetAsync(859783);

        Assert.Equal([1797542, 1801936, 1849505], set.Select(b => b.Id));
    }

    [Fact]
    public async Task GetSetAsync_DropsEntriesFromOtherSets()
    {
        var client = ClientReturning(SetJson);

        Assert.Empty(await client.GetSetAsync(1));
    }

    [Fact]
    public async Task Details_AreReadFromTheMirrorsNumericShape()
    {
        // osu.direct: bare numbers, no object counts, genre/language 0.
        var client = ClientReturning("""
        [{"file_md5":"aaa","beatmapset_id":950289,"beatmap_id":2044361,"approved":1,"mode":0,
          "diff_size":3,"diff_approach":5,"diff_overall":4,"diff_drain":4.5,"bpm":185,"hit_length":87,
          "genre_id":0,"language_id":0,"source":"Black Clover","tags":"opening pop","video":1,"storyboard":0,
          "approved_date":"2019-06-30T02:40:01Z"}]
        """);

        var details = (await client.GetByMd5Async("aaa"))!.Details!;

        Assert.Equal(3f, details.CircleSize);
        Assert.Equal(5f, details.ApproachRate);
        Assert.Equal(4f, details.OverallDifficulty);
        Assert.Equal(4.5f, details.DrainRate);
        Assert.Equal(185f, details.Bpm);
        Assert.Equal(87, details.HitLength);
        Assert.Null(details.CountCircles);
        Assert.Null(details.GenreId);      // 0 = unknown
        Assert.Null(details.LanguageId);
        Assert.Equal("Black Clover", details.Source);
        Assert.Equal("opening pop", details.Tags);
        Assert.True(details.Video);
        Assert.False(details.Storyboard);
        Assert.Equal(new DateTimeOffset(2019, 6, 30, 2, 40, 1, TimeSpan.Zero), details.ApprovedDate);
    }

    [Fact]
    public async Task Details_AreReadFromTheOfficialStringShape()
    {
        // old.ppy.sh: every value quoted, object counts and real genre/language present.
        var client = ClientReturning("""
        [{"file_md5":"aaa","beatmapset_id":"1","beatmap_id":"2","approved":"1","mode":"0",
          "diff_size":"4","count_normal":"120","count_slider":"80","count_spinner":"2",
          "genre_id":"3","language_id":"3","approved_date":"2019-06-30 02:40:01"}]
        """);

        var details = (await client.GetByMd5Async("aaa"))!.Details!;

        Assert.Equal(4f, details.CircleSize);
        Assert.Equal(120, details.CountCircles);
        Assert.Equal(80, details.CountSliders);
        Assert.Equal(2, details.CountSpinners);
        Assert.Equal(3, details.GenreId);
        Assert.Equal(3, details.LanguageId);
        Assert.NotNull(details.ApprovedDate);
    }

    [Fact]
    public async Task GetById_PicksTheAskedForDifficulty_EvenWhenTheWholeSetComesBack()
    {
        var client = ClientReturning("""
        [{"file_md5":"aaa","beatmapset_id":"355322","beatmap_id":"782310","approved":"1","version":"easy!"},
         {"file_md5":"bbb","beatmapset_id":"355322","beatmap_id":"782311","approved":"1","version":"normal!"}]
        """);

        var beatmap = await client.GetByIdAsync(782311);

        Assert.Equal((782311, 355322, "normal!"), (beatmap!.Id, beatmap.SetId, beatmap.Version));
        Assert.Null(await client.GetByIdAsync(1));
        Assert.Null(await client.GetByIdAsync(0));
    }

    [Theory]
    [InlineData("\"S1082, S1086,TOOLONG\"", "S1082,S1086")]
    [InlineData("[\"S1\",\"P2\",\"S1\"]", "S1,P2")]
    [InlineData("\"\"", "")]
    public async Task PackTags_AreReadFromAStringOrAnArray(string packs, string expected)
    {
        var client = ClientReturning($$"""[{"file_md5":"aaa","beatmapset_id":"1","beatmap_id":"2","approved":"1","packs":{{packs}}}]""");

        var details = (await client.GetByMd5Async("aaa"))!.Details!;

        Assert.Equal(expected, string.Join(',', details.PackTags!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"packs\":null")]
    public async Task PackTags_AreUnknownWithoutPackData(string packs)
    {
        var client = ClientReturning($$"""[{"file_md5":"aaa","beatmapset_id":"1","beatmap_id":"2","approved":"1"{{packs}}}]""");

        Assert.Null((await client.GetByMd5Async("aaa"))!.Details!.PackTags);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
