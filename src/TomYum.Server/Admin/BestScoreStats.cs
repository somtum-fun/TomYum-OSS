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
        // Lazer plays count as classic (OsuWeb.LazerRelaxImport.ClassicScore), as osu! adds them.
        stats.RankedScore = (await bests.ToListAsync(cancellationToken).ConfigureAwait(false)).Sum(OsuWeb.LazerRelaxImport.ClassicScore);

        // Each play's accuracy as its client showed it (a lazer play's with its slider ticks and ends).
        var performances = await bests.Where(s => s.Pp > 0).OrderByDescending(s => s.Pp)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var totals = PerformanceTotals.Compute(performances
            .Select(p => (p.Pp, (float)AdminModeScoresEndpoints.Accuracy(p)))
            .ToList());
        stats.Pp = totals?.Pp ?? 0;
        stats.Accuracy = totals?.Accuracy ?? 0;
    }
}
