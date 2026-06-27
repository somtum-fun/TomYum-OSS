namespace TomYum.Performance;

/// <summary>
/// Computes star rating (difficulty) and performance points (pp) for a score.
/// Implementations are swappable behind this interface per ADR-0005: the default is a
/// native C# calculator using the official <c>ppy.osu.Game.Rulesets.*</c> packages,
/// invoked inside the async score queue; a rosu-pp sidecar client is the documented
/// fallback if throughput becomes a bottleneck.
/// </summary>
/// <remarks>See <c>memories/protocols/pp-and-difficulty.md</c> and ADR-0005.</remarks>
public interface IPerformanceCalculator
{
    /// <summary>
    /// Calculates pp for the given score context. Shapes are intentionally minimal
    /// stubs; the concrete request/result types are defined when the engine lands.
    /// </summary>
    Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal placeholder for the inputs a pp calculation needs. TODO: expand with
/// beatmap reference, ruleset id, mods, and score statistics when implemented.
/// </summary>
/// <param name="BeatmapId">Id of the beatmap the score was set on.</param>
/// <param name="RulesetId">Ruleset (0 = osu!, 1 = taiko, 2 = catch, 3 = mania).</param>
/// <param name="Mods">Bitwise/string mod selection placeholder.</param>
public sealed record PerformanceRequest(int BeatmapId, int RulesetId, string Mods);
