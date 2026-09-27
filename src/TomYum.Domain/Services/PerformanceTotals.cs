namespace TomYum.Domain.Services;

/// <summary>
/// A player's total pp and accuracy from their best scores, with the standard osu! weighting:
/// the n-th best (0-based) counts 0.95^n, plus bonus pp for the number of scores.
/// </summary>
public static class PerformanceTotals
{
    /// <summary>
    /// Totals for <paramref name="bestPerformances"/> ordered best pp first; <c>null</c> when there
    /// are none (the caller keeps whatever it had).
    /// </summary>
    public static (double Pp, float Accuracy)? Compute(IReadOnlyList<(double Pp, float Accuracy)> bestPerformances)
    {
        ArgumentNullException.ThrowIfNull(bestPerformances);
        if (bestPerformances.Count == 0)
            return null;

        var weightedPp = bestPerformances.Select((p, i) => p.Pp * Math.Pow(0.95, i)).Sum();
        var bonusPp = 416.6667 * (1 - Math.Pow(0.9994, bestPerformances.Count));

        var weightedAcc = bestPerformances.Select((p, i) => p.Accuracy * Math.Pow(0.95, i)).Sum();
        var bonusAcc = 100.0 / (20.0 * (1 - Math.Pow(0.95, bestPerformances.Count)));

        return (weightedPp + bonusPp, (float)(weightedAcc * bonusAcc / 100.0));
    }
}
