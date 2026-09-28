using Microsoft.Extensions.Logging;

using TomYum.Domain.Enums;

namespace TomYum.Performance;

/// <summary>
/// The public <see cref="IPerformanceCalculator"/> seam. Derives the combined gamemode from the
/// request (ruleset + Relax/Autopilot mods) and dispatches to the backend the operator configured
/// for that mode, so e.g. vanilla can use the in-process official calculator while relax/autopilot
/// use an akatsuki-pp-rs sidecar. See ADR-0010.
/// </summary>
public sealed class RoutingPerformanceCalculator : IPerformanceCalculator, IAccuracyPerformance
{
    private readonly PerformanceOptions _options;
    private readonly IPerformanceBackendRegistry _registry;
    private readonly ILogger<RoutingPerformanceCalculator> _log;

    public RoutingPerformanceCalculator(
        PerformanceOptions options,
        IPerformanceBackendRegistry registry,
        ILogger<RoutingPerformanceCalculator> log)
    {
        _options = options;
        _registry = registry;
        _log = log;
    }

    public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
    {
        var gameMode = GameMode.FromLegacyMods(request.RulesetId, request.Mods);
        var backend = ResolveBackendName(gameMode);

        _log.LogDebug("pp route: {ShortName} (gamemode {GameMode}) -> backend '{Backend}'",
            GameMode.ToShortName(gameMode), gameMode, backend);

        return _registry.Resolve(backend).CalculateAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default) =>
        _registry.Resolve(ResolveBackendName(GameMode.FromLegacyMods(rulesetId, mods))) is IAccuracyPerformance backend
            ? backend.ForAccuraciesAsync(beatmapId, rulesetId, mods, accuracies, cancellationToken)
            : Task.FromResult<IReadOnlyList<double>?>(null);

    public Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default) =>
        _registry.Resolve(ResolveBackendName(GameMode.FromLegacyMods(rulesetId, mods))) is IAccuracyPerformance backend
            ? backend.StarRatingAsync(beatmapId, rulesetId, mods, cancellationToken)
            : Task.FromResult<double?>(null);

    private string ResolveBackendName(int gameMode)
    {
        foreach (var route in _options.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Backend))
                continue;
            if (route.Modes.Exists(m => GameMode.MatchesToken(m, gameMode)))
                return route.Backend;
        }

        return _options.Default;
    }
}
