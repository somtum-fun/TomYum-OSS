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
/// Optional: pp for full-combo plays at given accuracies (BanchoBot's <c>/np</c> reply). A
/// calculator that can't (another backend, a ruleset it doesn't support) returns null.
/// </summary>
public interface IAccuracyPerformance
{
    /// <summary>pp for each of <paramref name="accuracies"/> (0–1), in order, or null when unknown.</summary>
    Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default);

    /// <summary>
    /// The map's star rating in <paramref name="rulesetId"/> (converted if needed) with the legacy
    /// <paramref name="mods"/> applied, or null when unknown. Medals are judged on this.
    /// </summary>
    Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional: pp for a failed play, on the part of the map it got through (the map's difficulty up to
/// the last object reached, as Bathbot shows a fail's pp).
/// </summary>
public interface IPartialPerformance
{
    /// <summary>pp for <paramref name="request"/>'s hits over the first objects of the map, or null when unknown.</summary>
    Task<double?> PartialAsync(PerformanceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Full set of inputs the pp calculator needs for one submitted score.</summary>
/// <param name="BeatmapId">Online beatmap id.</param>
/// <param name="RulesetId">0 = osu!, 1 = taiko, 2 = catch, 3 = mania.</param>
/// <param name="Mods">Legacy mod bitmask (e.g. Hidden = 8, HardRock = 16).</param>
/// <param name="MaxCombo">Highest combo reached during the play.</param>
/// <param name="Count300">Hit count mapped to HitResult.Great.</param>
/// <param name="Count100">Hit count mapped to HitResult.Ok / LargeTickHit.</param>
/// <param name="Count50">Hit count mapped to HitResult.Meh / SmallTickHit.</param>
/// <param name="CountMiss">Miss count.</param>
/// <param name="CountGeki">Geki count (taiko: LargeBonus, mania: Perfect).</param>
/// <param name="CountKatu">Katu count (catch: SmallTickMiss, mania: Good).</param>
/// <param name="Passed">Whether the score cleared the map.</param>
/// <param name="Lazer">Whether the play was made on lazer (its slider judgements differ from stable's).</param>
public sealed record PerformanceRequest(
    int BeatmapId,
    int RulesetId,
    int Mods,
    int MaxCombo,
    int Count300,
    int Count100,
    int Count50,
    int CountMiss,
    int CountGeki,
    int CountKatu,
    bool Passed,
    bool Lazer = false);

/// <summary>A map's own difficulty: star rating and max combo without mods.</summary>
public sealed record BeatmapDifficulty(double StarRating, int MaxCombo);

/// <summary>
/// Optional: a map's difficulty from its file (custom uploads, which no API describes). A calculator
/// that can't returns null.
/// </summary>
public interface IBeatmapDifficulty
{
    /// <summary>The map's difficulty in <paramref name="rulesetId"/> (converted if needed), or null when unknown.</summary>
    Task<BeatmapDifficulty?> DifficultyAsync(int beatmapId, int rulesetId, CancellationToken cancellationToken = default);
}
