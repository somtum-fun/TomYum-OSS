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

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
