using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>The beatmap mirrors staff set in the website's admin panel (<see cref="BeatmapMirrorPool"/>).</summary>
public sealed class BeatmapMirrorPoolTests
{
    private const string MapJson = """
        {"id": 77, "beatmapset_id": 7, "checksum": "00000000000000000000000000000077", "version": "V", "mode_int": 0, "ranked": 1,
         "set": {"id": 7, "artist": "A", "title": "T", "creator": "C", "ranked": 1}}
        """;

    private sealed class Hosts(Func<Uri, HttpStatusCode> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.Host);
            var status = answer(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? MapJson : "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static BeatmapMirrorPool Pool(params BeatmapMirrorEntry[]? entries)
    {
        var pool = new BeatmapMirrorPool(new MirrorOptions(), new OsuApiBudget(TimeProvider.System, 1000), TimeProvider.System,
            NullLogger<BeatmapMirrorPool>.Instance);
        pool.UsePanelList(entries);
        return pool;
    }

    [Theory]
    [InlineData("https://catboy.best/", true)]
    [InlineData("https://mirror.example.com/osu", true)]
    [InlineData("http://catboy.best/", false)]              // https only
    [InlineData("https://user:pass@catboy.best/", false)]   // no credentials in it
    [InlineData("https://localhost/", false)]
    [InlineData("https://server:8080/", false)]             // a compose service name
    [InlineData("https://db.internal/", false)]
    [InlineData("https://127.0.0.1/", false)]
    [InlineData("https://10.0.100.1/", false)]
    [InlineData("https://192.168.1.10/", false)]
    [InlineData("https://172.20.0.5/", false)]
    [InlineData("https://169.254.169.254/", false)]          // cloud metadata
    [InlineData("https://[::1]/", false)]
    [InlineData("https://1.1.1.1/", true)]
    public void OnlyPublicHttpsAddressesAreAsked(string url, bool allowed) =>
        Assert.Equal(allowed, BeatmapMirrorEntry.IsAllowed(new Uri(url)));

    [Fact]
    public async Task AMirrorThatRefuses_IsPausedAndTheNextAnswers()
    {
        var hosts = new Hosts(uri => uri.Host == "first.example" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        var pool = Pool(
            new BeatmapMirrorEntry("second", "https://second.example/", Priority: 2, RequestsPerMinute: 1000),
            new BeatmapMirrorEntry("first", "https://first.example/", Priority: 1, RequestsPerMinute: 1000));
        var mirror = new BeatmapMirror(new HttpClient(hosts), new MirrorOptions(), pool, NullLogger<BeatmapMirror>.Instance);

        Assert.Equal(77, (await mirror.GetByIdAsync(77))?.Id);
        Assert.Equal(["first.example", "second.example"], hosts.Asked);

        Assert.Equal(77, (await mirror.GetByIdAsync(77))?.Id);
        Assert.Equal(["first.example", "second.example", "second.example"], hosts.Asked); // the first is paused
        Assert.True(pool.PaceFor((await pool.LookupMirrorsAsync())[0]).IsPaused);
    }

    [Fact]
    public async Task ANotFound_IsAnAnswer_TheNextIsntAsked()
    {
        var hosts = new Hosts(_ => HttpStatusCode.NotFound);
        var pool = Pool(
            new BeatmapMirrorEntry("first", "https://first.example/", Priority: 1, RequestsPerMinute: 1000),
            new BeatmapMirrorEntry("second", "https://second.example/", Priority: 2, RequestsPerMinute: 1000));
        var mirror = new BeatmapMirror(new HttpClient(hosts), new MirrorOptions(), pool, NullLogger<BeatmapMirror>.Instance);

        Assert.Null(await mirror.GetByIdAsync(77));
        Assert.Equal(["first.example"], hosts.Asked);
    }

    [Fact]
    public async Task DisabledPrivateAndDownloadOnlyMirrors_AreNotAskedForLookups()
    {
        var hosts = new Hosts(_ => HttpStatusCode.OK);
        var pool = Pool(
            new BeatmapMirrorEntry("off", "https://off.example/", Enabled: false),
            new BeatmapMirrorEntry("inside", "https://10.0.0.5/"),
            new BeatmapMirrorEntry("downloads", "https://dl.example/", Lookups: false, Downloads: true));
        var mirror = new BeatmapMirror(new HttpClient(hosts), new MirrorOptions(), pool, NullLogger<BeatmapMirror>.Instance);

        Assert.Null(await mirror.GetByIdAsync(77));
        Assert.Empty(hosts.Asked);
    }

    [Fact]
    public async Task WithoutAPanelList_TheConfiguredMirrorIsAsked()
    {
        var hosts = new Hosts(_ => HttpStatusCode.OK);
        var mirror = new BeatmapMirror(new HttpClient(hosts), new MirrorOptions { Url = "https://configured.example/" },
            new BeatmapMirrorPool(new MirrorOptions { Url = "https://configured.example/" }, new OsuApiBudget(TimeProvider.System, 1000),
                TimeProvider.System, NullLogger<BeatmapMirrorPool>.Instance),
            NullLogger<BeatmapMirror>.Instance);

        Assert.Equal(77, (await mirror.GetByIdAsync(77))?.Id);
        Assert.Equal(["configured.example"], hosts.Asked);
    }

    [Fact]
    public void Downloads_GoToThePanelsDownloadMirror_ElseTheServersSettings()
    {
        var options = Options.Create(new DirectOptions());
        var store = new CustomBeatmapStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var pool = Pool(new BeatmapMirrorEntry("cache", "https://dl.example/", Lookups: false, Downloads: true, DownloadPath: "/sets/{id}.osz"));

        var redirect = Assert.IsType<RedirectHttpResult>(DirectEndpoints.HandleDownload(123, new DefaultHttpContext().Request, options, store,
            mirror: pool.DownloadMirror));
        Assert.Equal("https://dl.example/sets/123.osz", redirect.Url);

        var fallback = Assert.IsType<RedirectHttpResult>(DirectEndpoints.HandleDownload(123, new DefaultHttpContext().Request, options, store));
        Assert.StartsWith(new DirectOptions().MirrorBaseUrl, fallback.Url, StringComparison.Ordinal);
    }
}
