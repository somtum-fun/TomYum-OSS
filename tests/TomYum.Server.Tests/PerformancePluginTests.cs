using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Performance;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The pluggable pp engine (ADR-0010): the router dispatches per gamemode to the configured
/// backend, and the http backend parses the sidecar contract and falls back cleanly on failure.
/// </summary>
public class PerformancePluginTests
{
    private static PerformanceRequest Req(int ruleset, int mods) =>
        new(BeatmapId: 1, RulesetId: ruleset, Mods: mods, MaxCombo: 100,
            Count300: 100, Count100: 0, Count50: 0, CountMiss: 0, CountGeki: 0, CountKatu: 0, Passed: true);

    [Theory]
    [InlineData(0, 0, "official")]      // vn!std -> default
    [InlineData(2, 0, "official")]      // vn!catch -> default
    [InlineData(0, 128, "rust")]        // rx!std (relax) -> routed
    [InlineData(2, 128, "rust")]        // rx!catch -> routed
    [InlineData(0, 8192, "autopilot")]  // ap!std -> routed
    public async Task Router_dispatches_by_gamemode(int ruleset, int mods, string expectedBackend)
    {
        var options = new PerformanceOptions
        {
            Default = "official",
            Routes =
            [
                new PerformanceRoute { Modes = ["relax"], Backend = "rust" },
                new PerformanceRoute { Modes = ["autopilot"], Backend = "autopilot" },
            ],
        };
        var registry = new CapturingRegistry();
        var router = new RoutingPerformanceCalculator(options, registry, NullLogger<RoutingPerformanceCalculator>.Instance);

        await router.CalculateAsync(Req(ruleset, mods));

        Assert.Equal(expectedBackend, registry.LastResolved);
    }

    [Fact]
    public async Task Http_backend_parses_pp_from_sidecar()
    {
        var calc = new HttpPerformanceCalculator(
            new HttpClient(new StubHandler(HttpStatusCode.OK, """{"pp":42.5,"starRating":5.1}""")),
            "http://sidecar/pp", TimeSpan.FromSeconds(5), "rust", fallback: null,
            NullLogger<HttpPerformanceCalculator>.Instance);

        Assert.Equal(42.5, await calc.CalculateAsync(Req(0, 128)));
    }

    [Theory]
    [InlineData(128 | 64 | 16, 1.01, 128 | 16)]        // RX HR DT at 1.01x: paid without DT
    [InlineData(128 | 64 | 512, 1.2, 128)]             // RX NC at 1.2x: paid without NC/DT
    [InlineData(128 | 64, 1.5, 128 | 64)]              // DT at its usual speed stays
    [InlineData(128 | 64, 2.0, 128 | 64)]              // above 1.5x stays at 1.5x (never more)
    [InlineData(128 | 64, null, 128 | 64)]             // no speed set: the mod's usual 1.5x
    [InlineData(128 | 256, 0.6, 128 | 256)]            // HT untouched (0.75x already pays less)
    [InlineData(128, 1.01, 128)]                       // a speed without DT/NC changes nothing
    public void Http_backend_stopgap_pays_custom_dt_speed_below_1_5_as_no_rate_mod(int mods, double? speed, int expected)
    {
        Assert.Equal(expected, HttpPerformanceCalculator.SidecarMods(Req(0, mods) with { SpeedChange = speed }));
    }

    [Theory]
    [InlineData(128 | 64 | 16, 1.01, 1.01)]           // RX HR DT at 1.01x
    [InlineData(128 | 64 | 512, 1.2, 1.2)]             // NC at 1.2x
    [InlineData(128 | 64, 2.0, 2.0)]                   // DT at 2x
    [InlineData(128 | 64, 1.5, null)]                  // DT at its usual speed: nothing to send
    [InlineData(128 | 64, null, null)]                 // no speed set
    [InlineData(128 | 256, 0.6, 0.6)]                  // HT at 0.6x
    [InlineData(128 | 256, 0.75, null)]                // HT at its usual speed
    [InlineData(128, 1.01, null)]                      // a speed without a rate mod
    public void Http_backend_sends_a_rate_mods_own_speed(int mods, double? speed, double? expected)
    {
        Assert.Equal(expected, HttpPerformanceCalculator.CustomRate(Req(0, mods) with { SpeedChange = speed }));
    }

    [Fact]
    public async Task Http_backend_pays_a_custom_speed_at_that_speed()
    {
        var handler = new RecordingHandler(_ => """{"pp":300.0,"clockRate":1.01}""");
        var calc = new HttpPerformanceCalculator(new HttpClient(handler), "http://sidecar/pp", TimeSpan.FromSeconds(5), "rust",
            fallback: null, NullLogger<HttpPerformanceCalculator>.Instance);

        Assert.Equal(300.0, await calc.CalculateAsync(Req(0, 128 | 64) with { SpeedChange = 1.01 }));
        var sent = Assert.Single(handler.Bodies);
        Assert.Contains("\"mods\":192", sent, StringComparison.Ordinal);
        Assert.Contains("\"clockRate\":1.01", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_backend_falls_back_to_no_rate_mod_on_a_sidecar_without_clock_rate()
    {
        // An older sidecar ignores clockRate (pays DT as 1.5x) and doesn't echo it: ask again without DT.
        var handler = new RecordingHandler(body => body.Contains("\"mods\":192", StringComparison.Ordinal) ? """{"pp":900.0}""" : """{"pp":290.0}""");
        var calc = new HttpPerformanceCalculator(new HttpClient(handler), "http://sidecar/pp", TimeSpan.FromSeconds(5), "rust",
            fallback: null, NullLogger<HttpPerformanceCalculator>.Instance);

        Assert.Equal(290.0, await calc.CalculateAsync(Req(0, 128 | 64) with { SpeedChange = 1.01 }));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"mods\":128", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"clockRate\":null", handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_backend_uses_fallback_on_failure()
    {
        var calc = new HttpPerformanceCalculator(
            new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "boom")),
            "http://sidecar/pp", TimeSpan.FromSeconds(5), "rust",
            fallback: (_, _) => Task.FromResult(7.0),   // e.g. the 'official' backend
            NullLogger<HttpPerformanceCalculator>.Instance);

        Assert.Equal(7.0, await calc.CalculateAsync(Req(0, 128)));
    }

    [Fact]
    public async Task Http_backend_returns_zero_when_no_fallback()
    {
        var calc = new HttpPerformanceCalculator(
            new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable, "down")),
            "http://sidecar/pp", TimeSpan.FromSeconds(5), "rust", fallback: null,
            NullLogger<HttpPerformanceCalculator>.Instance);

        Assert.Equal(0.0, await calc.CalculateAsync(Req(0, 128)));
    }

    [Fact]
    public void AddTomYumPerformance_throws_on_undefined_route_backend()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Performance:Default"] = "official",
            ["Performance:Routes:0:Modes:0"] = "relax",
            ["Performance:Routes:0:Backend"] = "typo-backend",   // not defined
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTomYumPerformance(config));
    }

    [Fact]
    public void AddTomYumPerformance_throws_on_undefined_fallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Performance:Backends:akatsuki:Type"] = "http",
            ["Performance:Backends:akatsuki:Url"] = "http://x/pp",
            ["Performance:Backends:akatsuki:Fallback"] = "offical",   // typo for 'official'
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTomYumPerformance(config));
    }

    [Fact]
    public void AddTomYumPerformance_accepts_empty_config_and_auto_registers_official()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        // No routes/backends configured -> everything defaults to the auto-registered official backend.
        var ex = Record.Exception(() => new ServiceCollection().AddTomYumPerformance(config));
        Assert.Null(ex);
    }

    private sealed class CapturingRegistry : IPerformanceBackendRegistry
    {
        public string? LastResolved { get; private set; }
        public IPerformanceCalculator Resolve(string name)
        {
            LastResolved = name;
            return new ConstCalculator();
        }
    }

    private sealed class ConstCalculator : IPerformanceCalculator
    {
        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(0.0);
    }

    private sealed class RecordingHandler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
