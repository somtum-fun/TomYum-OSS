using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

public sealed class AudioPreviewsTests : IDisposable
{
    private static readonly byte[] Mp3 = [.. "ID3"u8, .. new byte[2048]];
    private static readonly byte[] Ogg = [.. "OggS"u8, .. new byte[2048]];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-previews-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task APreview_ComesFromTheMirrorOnce_ThenFromDisk()
    {
        var http = new FakeHttp(_ => Audio(Mp3));
        using var previews = Previews(http);

        var first = await previews.GetAsync(1143822);
        var again = await previews.GetAsync(1143822);

        Assert.NotNull(first);
        Assert.Equal("audio/mpeg", first.Value.Type);
        Assert.Equal(first, again);
        Assert.Equal(["https://catboy.best/preview/audio/2388297"], http.Asked);
        Assert.Equal(Mp3, await File.ReadAllBytesAsync(first.Value.Path));
    }

    [Fact]
    public async Task WithoutTheMirror_OsusOwnPreviewIsUsed_OggToo()
    {
        // The mirror answers with an error page (not audio): osu!'s, which is Ogg here.
        var http = new FakeHttp(url => url.Contains("catboy", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"error\":\"nope\"}") }
            : Audio(Ogg));
        using var previews = Previews(http);

        var preview = await previews.GetAsync(1143822);

        Assert.Equal("audio/ogg", preview?.Type);
        Assert.EndsWith(".ogg", preview?.Path, StringComparison.Ordinal);
        Assert.Equal(2, http.Asked.Count);
    }

    [Fact]
    public async Task NowhereHasIt_NothingIsKept()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var previews = Previews(http);

        Assert.Null(await previews.GetAsync(1));
        Assert.Null(await previews.GetAsync(1));
        Assert.Equal(4, http.Asked.Count); // asked again: a miss isn't kept
    }

    [Fact]
    public async Task NearTheMirrorsRateLimit_OsusPreviewsAreUsedUntilItResets()
    {
        var reset = DateTimeOffset.UtcNow.AddSeconds(40);
        var http = new FakeHttp(url =>
        {
            var response = Audio(Mp3);
            if (url.Contains("catboy", StringComparison.Ordinal))
            {
                response.Headers.Add("X-Ratelimit-Remaining", "12");
                response.Headers.Add("X-Ratelimit-Reset", reset.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            return response;
        });
        using var previews = Previews(http);

        await previews.GetAsync(1);
        Assert.True(previews.MirrorPaused);
        await previews.GetAsync(2);

        Assert.Equal(["https://catboy.best/preview/audio/2388297", "https://b.ppy.sh/preview/2.mp3"], http.Asked);
    }

    [Fact]
    public async Task ToldToSlowDown_TheMirrorIsLeftAlone()
    {
        var http = new FakeHttp(url =>
        {
            if (!url.Contains("catboy", StringComparison.Ordinal))
                return Audio(Mp3);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var previews = Previews(http);

        Assert.Equal("audio/mpeg", (await previews.GetAsync(1))?.Type); // osu!'s, after the 429
        await previews.GetAsync(2);

        Assert.True(previews.MirrorPaused);
        Assert.Equal(["https://catboy.best/preview/audio/2388297", "https://b.ppy.sh/preview/1.mp3", "https://b.ppy.sh/preview/2.mp3"], http.Asked);
    }

    [Fact]
    public void OnlyAudio_IsTakenForAPreview()
    {
        Assert.Equal("audio/mpeg", AudioPreviews.AudioType(Mp3));
        Assert.Equal("audio/mpeg", AudioPreviews.AudioType([0xFF, 0xFB, .. new byte[2048]]));
        Assert.Equal("audio/ogg", AudioPreviews.AudioType(Ogg));
        Assert.Null(AudioPreviews.AudioType("{\"error\":\"rate limited\"}"u8));
        Assert.Null(AudioPreviews.AudioType([.. "<html>"u8, .. new byte[2048]]));
    }

    private AudioPreviews Previews(FakeHttp http) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assets:CachePath"] = _root }).Build(),
            http, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<AudioPreviews>.Instance)
        {
            BeatmapIdFor = (_, _) => Task.FromResult<int?>(2388297),
        };

    private static HttpResponseMessage Audio(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class FakeHttp(Func<string, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Asked)
                Asked.Add(url);
            return Task.FromResult(answer(url));
        }
    }
}
