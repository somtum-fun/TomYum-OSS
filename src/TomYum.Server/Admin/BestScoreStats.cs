using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence;

namespace TomYum.Server.Admin;

/// <summary>
/// A player's stats that follow from their best scores (ranked score, weighted pp, accuracy),
/// computed the way score submission computes them. Used after staff change scores in bulk.
/// </summary>
internal static class BestScoreStats
{
    /// <summary>Sets <paramref name="stats"/>' ranked score, pp and accuracy from the player's current best scores in its mode.</summary>
    public static async Task ApplyAsync(TomYumDbContext db, UserStats stats, CancellationToken cancellationToken)
    {
        var bests = db.Scores.AsNoTracking().Where(s => s.UserId == stats.UserId && s.Mode == stats.Mode && s.Status == ScoreStatus.Best);
        stats.RankedScore = await bests.SumAsync(s => s.ScoreValue, cancellationToken).ConfigureAwait(false);

        var performances = await bests.Where(s => s.Pp > 0).OrderByDescending(s => s.Pp)
            .Select(s => new { s.Pp, s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var totals = PerformanceTotals.Compute(performances
            .Select(p => (p.Pp, (float)AccuracyCalculator.Compute(GameMode.AsVanilla(stats.Mode), p.Count300, p.Count100, p.Count50, p.CountGeki, p.CountKatu, p.CountMiss)))
            .ToList());
        stats.Pp = totals?.Pp ?? 0;
        stats.Accuracy = totals?.Accuracy ?? 0;
    }
}
