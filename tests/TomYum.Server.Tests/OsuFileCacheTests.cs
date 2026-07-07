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
}
