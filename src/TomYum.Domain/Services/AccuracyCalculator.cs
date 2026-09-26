namespace TomYum.Domain.Services;

/// <summary>
/// Per-mode accuracy formula, mirroring the official osu! stable calculation.
/// Result is a fraction in [0, 1] — multiply by 100 for percentage display.
/// </summary>
/// <remarks>
/// Formula references:
///   osu!    — (300·n300 + 100·n100 + 50·n50) / (total · 300)
///   taiko   — (n300 + 0.5·n100) / (n300 + n100 + nmiss)
///   catch   — (n300 + n100 + n50) / (n300 + n100 + n50 + nkatu + nmiss)
///             where nkatu = missed tiny droplets (SmallTickMiss)
///   mania   — (ngeki·300 + n300·300 + nkatu·200 + n100·100 + n50·50)
///             / ((ngeki + n300 + nkatu + n100 + n50 + nmiss) · 300)
/// </remarks>
public static class AccuracyCalculator
{
    /// <summary>
    /// Computes accuracy for the given mode and hit counts.
    /// Returns 0 when there are no scoreable objects.
    /// </summary>
    public static double Compute(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss) =>
        mode switch
        {
            // osu! standard
            0 => (n300 + n100 + n50 + nmiss) is var t && t > 0
                ? (n300 * 300.0 + n100 * 100.0 + n50 * 50.0) / (t * 300.0)
                : 0.0,

            // taiko — drum roll ticks (ngeki/nkatu → LargeBonus) are NOT in the denominator
            1 => (n300 + n100 + nmiss) is var t && t > 0
                ? (n300 + n100 * 0.5) / t
                : 0.0,

            // catch — nkatu = SmallTickMiss (missed tiny droplets), counted in denominator only
            2 => (n300 + n100 + n50 + nkatu + nmiss) is var t && t > 0
                ? (double)(n300 + n100 + n50) / t
                : 0.0,

            // mania — ngeki = MAX/rainbow-300 (Perfect), nkatu = 200 (Good)
            3 => (ngeki + n300 + nkatu + n100 + n50 + nmiss) is var t && t > 0
                ? (ngeki * 300.0 + n300 * 300.0 + nkatu * 200.0 + n100 * 100.0 + n50 * 50.0) / (t * 300.0)
                : 0.0,

            _ => 0.0,
        };
}
