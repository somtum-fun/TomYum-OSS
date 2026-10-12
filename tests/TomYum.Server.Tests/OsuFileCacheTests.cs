using System.IO;
using System.Net.Http;

using Microsoft.Extensions.Logging.Abstractions;

using TomYum.Performance;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The on-disk <c>.osu</c> cache serves an already-cached file without touching the network — the
/// L2-hit path that lets custom (locally-hosted) maps and warm caches skip osu.ppy.sh entirely.
/// </summary>
public class OsuFileCacheTests
{
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("network must not be hit on a disk-cache hit");
    }

    [Fact]
    public async Task GetAsync_serves_cached_file_without_downloading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tomyum-osucache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new OsuFileCache(dir, new HttpClient(new ThrowingHandler()), NullLogger<OsuFileCache>.Instance);
            var expected = "osu file format v14\n\n[HitObjects]\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "1801936.osu"), expected);

            var bytes = await cache.GetAsync(1801936);

            // Served from disk; the throwing handler proves no download was attempted.
            Assert.Equal(expected, bytes);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        public int Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task A_map_osu_doesnt_have_is_asked_for_once_until_it_is_stored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tomyum-osucache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new NotFoundHandler();
            var cache = new OsuFileCache(dir, new HttpClient(handler), NullLogger<OsuFileCache>.Instance);

            await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(999_999_999));
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(999_999_999));
            Assert.Equal(1, handler.Asked);
            Assert.Empty(Directory.GetFiles(dir));

            // A custom map stored under that id afterwards is served.
            await cache.StoreAsync(999_999_999, "osu file format v14\n"u8.ToArray());
            Assert.Equal("osu file format v14\n"u8.ToArray(), await cache.GetAsync(999_999_999));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("server", null, true)]
    [InlineData("localhost", null, true)]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("172.18.0.5", null, true)]
    [InlineData("[::1]", null, true)]
    [InlineData("osu.example.com", null, false)]
    [InlineData("b.example.com", null, false)]
    [InlineData("8.8.8.8", null, false)]
    [InlineData("server", "203.0.113.9", false)]
    [InlineData("", null, false)]
    public void OsuFiles_AreOnlyForTheServersOwnNetwork(string host, string? cloudflareIp, bool allowed)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        if (cloudflareIp is not null)
            context.Request.Headers["CF-Connecting-IP"] = cloudflareIp;

        Assert.Equal(allowed, TomYum.Server.Beatmaps.DirectEndpoints.IsInternalCaller(context.Request));
    }
}
