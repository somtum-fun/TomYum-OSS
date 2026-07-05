using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TomYum.Domain.Entities;
using TomYum.Performance;
using TomYum.Persistence;

namespace TomYum.Tools.RecalcPp;

internal sealed record RecalculationResult(int ScoresProcessed, int ScoresRecalculated, int UsersUpdated);

/// <summary>
/// Core recalculation logic. For each Best score: recomputes PP via
/// <see cref="IPerformanceCalculator"/>, writes it back to the database, then recomputes
/// weighted PP + accuracy for each affected user (same formula as the submission path).
/// </summary>
internal sealed class RecalculationEngine
{
    private readonly TomYumDbContext _db;
    private readonly IPerformanceCalculator _pp;
    private readonly ILogger<RecalculationEngine> _log;
    private readonly bool _dryRun;
    private readonly int _batchSize;

    public RecalculationEngine(
        TomYumDbContext db,
        IPerformanceCalculator pp,
        ILogger<RecalculationEngine> log,
        bool dryRun,
        int batchSize)
    {
        _db = db;
        _pp = pp;
        _log = log;
        _dryRun = dryRun;
        _batchSize = batchSize;
    }

    public async Task<RecalculationResult> RecalculateAsync(int? modeFilter, CancellationToken cancellationToken)
    {
        // Step 1: collect all Best scores (non-tracked for read; we'll update in batches).
        var query = _db.Scores.AsNoTracking()
            .Where(s => s.Status == ScoreStatus.Best && s.Passed);

        if (modeFilter is not null)
            query = query.Where(s => s.Mode == modeFilter.Value);

        var allScores = await query.OrderBy(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Found {Count} Best scores to recalculate", allScores.Count);

        if (allScores.Count == 0)
            return new RecalculationResult(0, 0, 0);

        // Step 2: recalculate PP for all scores, batch-saving to DB.
        var updatedUserModes = new HashSet<(int UserId, int Mode)>();
        var recalculated = 0;
        var total = allScores.Count;

        for (var batchStart = 0; batchStart < total; batchStart += _batchSize)
        {
            var batch = allScores.Skip(batchStart).Take(_batchSize).ToList();

            foreach (var score in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double newPp;
                try
                {
                    newPp = await _pp.CalculateAsync(new PerformanceRequest(
                        BeatmapId: score.BeatmapId,
                        RulesetId: score.Mode,
                        Mods: score.Mods,
                        MaxCombo: score.MaxCombo,
                        Count300: score.Count300,
                        Count100: score.Count100,
                        Count50: score.Count50,
                        CountMiss: score.CountMiss,
                        CountGeki: score.CountGeki,
                        CountKatu: score.CountKatu,
                        Passed: score.Passed), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PP calc failed for score {Id} (beatmap {BeatmapId}, mode {Mode}) — skipping",
                        score.Id, score.BeatmapId, score.Mode);
                    continue;
                }

                if (Math.Abs(newPp - score.Pp) < 0.001)
                {
                    // No meaningful change; still mark user as needing stats refresh if PP was 0.
                    if (score.Pp > 0)
                        continue;
                }

                _log.LogDebug("Score {Id}: {OldPp:F2}pp → {NewPp:F2}pp", score.Id, score.Pp, newPp);

                if (!_dryRun)
                {
                    await _db.Scores
                        .Where(s => s.Id == score.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Pp, newPp), cancellationToken)
                        .ConfigureAwait(false);
                }

                updatedUserModes.Add((score.UserId, score.Mode));
                recalculated++;
            }

            var processed = Math.Min(batchStart + _batchSize, total);
            Console.Write($"\r  Scores: {processed}/{total} ({processed * 100 / total}%)   ");
        }

        Console.WriteLine();

        // Step 3: recompute weighted PP + accuracy for each affected user+mode.
        _log.LogInformation("Updating stats for {Count} user+mode pairs", updatedUserModes.Count);

        var usersUpdated = 0;
        foreach (var (userId, mode) in updatedUserModes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecalcUserStatsAsync(userId, mode, cancellationToken).ConfigureAwait(false);
            usersUpdated++;
        }

        return new RecalculationResult(total, recalculated, usersUpdated);
    }

    private async Task RecalcUserStatsAsync(int userId, int mode, CancellationToken cancellationToken)
    {
        // Fetch all Best scores for this user+mode ordered by PP desc (same as submission path).
        var bestScores = await _db.Scores.AsNoTracking()
            .Where(s => s.UserId == userId && s.Mode == mode && s.Status == ScoreStatus.Best && s.Pp > 0)
            .OrderByDescending(s => s.Pp)
            .Select(s => new { s.Pp, s.Count300, s.Count100, s.Count50, s.CountMiss })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (bestScores.Count == 0)
            return;

        var n = bestScores.Count;

        // Weighted PP: Σ(pp_i × 0.95^i) + bonus (same formula as submission path + bancho.py)
        var weightedPp = bestScores.Select((s, i) => s.Pp * Math.Pow(0.95, i)).Sum();
        var bonusPp = 416.6667 * (1 - Math.Pow(0.9994, n));
        var totalPp = weightedPp + bonusPp;

        // Weighted accuracy: Σ(acc_i × 0.95^i) × 100 / (20 × (1 − 0.95^n))
        var weightedAcc = bestScores.Select((s, i) =>
        {
            var total = s.Count300 + s.Count100 + s.Count50 + s.CountMiss;
            var acc = total > 0 ? (s.Count300 * 300.0 + s.Count100 * 100.0 + s.Count50 * 50.0) / (total * 300.0) : 0.0;
            return acc * Math.Pow(0.95, i);
        }).Sum();
        var bonusAcc = 100.0 / (20.0 * (1 - Math.Pow(0.95, n)));
        var totalAcc = (float)(weightedAcc * bonusAcc / 100.0);

        _log.LogDebug("User {UserId} mode {Mode}: PP {Pp:F2}, Acc {Acc:P2}", userId, mode, totalPp, totalAcc);

        if (_dryRun)
            return;

        // Upsert the stats row — create it first if it doesn't exist.
        var stats = await _db.UserStats
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken)
            .ConfigureAwait(false);

        if (stats is null)
        {
            stats = new UserStats { UserId = userId, Mode = mode };
            _db.UserStats.Add(stats);
        }

        stats.Pp = totalPp;
        stats.Accuracy = totalAcc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }
}
