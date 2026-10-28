using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Performance;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// <see cref="PerformanceOptions.VanillaPp"/>: relax and autopilot (when turned on) are worth what the same score
/// without that mod is, so the backend is asked without it; the route still follows the variant.
/// </summary>
public class VanillaPpTests
{
    private const int Hidden = 1 << 3;
    private const int Relax = 1 << 7;
    private const int Autopilot = 1 << 13;

    private static (RoutingPerformanceCalculator Router, Dictionary<string, RecordingBackend> Backends) Router(string? vanillaPp = null, bool relaxRoute = false)
    {
        var backends = new Dictionary<string, RecordingBackend> { ["official"] = new(), ["sidecar"] = new() };
        var options = new PerformanceOptions();
        if (vanillaPp is not null)
            options.VanillaPp = vanillaPp;
        if (relaxRoute)
            options.Routes.Add(new PerformanceRoute { Modes = ["relax"], Backend = "sidecar" });
        return (new RoutingPerformanceCalculator(options, new Registry(backends), NullLogger<RoutingPerformanceCalculator>.Instance), backends);
    }

    private static PerformanceRequest Play(int mods) => new(1, 0, mods, 500, 400, 10, 0, 1, 0, 0, Passed: true);

    [Fact]
    public async Task Relax_KeepsItsMod_ByDefault()
    {
        var (router, backends) = Router();

        await router.CalculateAsync(Play(Relax | Hidden));

        Assert.Equal(Relax | Hidden, backends["official"].Mods);
    }

    [Fact]
    public async Task Relax_IsWorkedOutAsVanilla_WhenOn()
    {
        var (router, backends) = Router("relax, autopilot");

        await router.CalculateAsync(Play(Relax | Hidden));

        Assert.Equal(Hidden, backends["official"].Mods);
    }

    [Fact]
    public async Task Autopilot_IsWorkedOutAsVanilla_WhenOn_UnlessLeftOut()
    {
        var (router, backends) = Router("relax, autopilot");
        await router.CalculateAsync(Play(Autopilot | Hidden));
        Assert.Equal(Hidden, backends["official"].Mods);

        (router, backends) = Router("relax");
        await router.CalculateAsync(Play(Autopilot | Hidden));
        Assert.Equal(Autopilot | Hidden, backends["official"].Mods);
    }

    [Fact]
    public async Task Relax_KeepsItsMod_WhenTurnedOff()
    {
        var (router, backends) = Router("");

        await router.CalculateAsync(Play(Relax));

        Assert.Equal(Relax, backends["official"].Mods);
    }

    [Fact]
    public async Task TheRoute_StillFollowsTheVariant()
    {
        var (router, backends) = Router("relax, autopilot", relaxRoute: true);

        await router.CalculateAsync(Play(Relax));

        Assert.Null(backends["official"].Mods);
        Assert.Equal(0, backends["sidecar"].Mods);
    }

    [Fact]
    public async Task ALazerPlaysOwnMods_LoseRelaxToo()
    {
        var (router, backends) = Router("relax, autopilot");

        await router.CalculateAsync(Play(Relax) with { LazerScore = """{"mods":[{"acronym":"DT","settings":{"speed_change":1.6}},{"acronym":"RX"}],"statistics":{"great":1}}""" });

        Assert.Equal("""{"mods":[{"acronym":"DT","settings":{"speed_change":1.6}}],"statistics":{"great":1}}""", backends["official"].LazerScore);
    }

    private sealed class Registry(Dictionary<string, RecordingBackend> backends) : IPerformanceBackendRegistry
    {
        public IPerformanceCalculator Resolve(string name) => backends[name];
    }

    private sealed class RecordingBackend : IPerformanceCalculator
    {
        public int? Mods { get; private set; }

        public string? LazerScore { get; private set; }

        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
        {
            Mods = request.Mods;
            LazerScore = request.LazerScore;
            return Task.FromResult(727.0);
        }
    }
}
