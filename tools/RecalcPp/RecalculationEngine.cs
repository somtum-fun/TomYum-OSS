using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence;

namespace TomYum.Tools.RecalcPp;

internal sealed record ScoreChange(long ScoreId, int BeatmapId, int Mode, double OldPp, double NewPp);
internal sealed record UserStatChange(int UserId, int Mode, double OldPp, double NewPp, float OldAcc, float NewAcc);
internal sealed record RecalculationResult(
    int ScoresProcessed,
    int ScoresRecalculated,
    int UsersUpdated,
    IReadOnlyList<ScoreChange> ScoreChanges,
    IReadOnlyList<UserStatChange> UserStatChanges);

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
    private readonly bool _force;
    private readonly int _batchSize;

    public RecalculationEngine(
        TomYumDbContext db,
        IPerformanceCalculator pp,
        ILogger<RecalculationEngine> log,
        bool dryRun,
        int batchSize,
        bool force = false)
    {
        _db = db;
        _pp = pp;
        _log = log;
        _dryRun = dryRun;
        _force = force;
        _batchSize = batchSize;
    }

    public async Task<RecalculationResult> RecalculateAsync(int? modeFilter, CancellationToken cancellationToken)
    {
        // Step 1: collect all Best scores on ranked/approved maps only.
        var query = _db.Scores.AsNoTracking()
            .Where(s => s.Status == ScoreStatus.Best && s.Passed)
            .Join(_db.Beatmaps,
                s => s.BeatmapId,
                b => b.Id,
                (s, b) => new { Score = s, BeatmapStatus = b.Status })
            .Where(x => x.BeatmapStatus == RankedStatus.Ranked || x.BeatmapStatus == RankedStatus.Approved)
            .Select(x => x.Score);

        if (modeFilter is not null)
            query = query.Where(s => s.Mode == modeFilter.Value);

        var allScores = await query.OrderBy(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Found {Count} Best scores to recalculate", allScores.Count);

        if (allScores.Count == 0)
            return new RecalculationResult(0, 0, 0, [], []);

        // Step 2: recalculate PP for all scores, batch-saving to DB.
        // Seed all user+mode pairs upfront so stats are always refreshed — even when
        // individual PP values haven't changed (e.g. accuracy formula was fixed but PP
        // recalc derives accuracy from raw hit counts and was already correct).
        var updatedUserModes = allScores
            .Select(s => (s.UserId, s.Mode))
            .ToHashSet();
        var recalculated = 0;
        var total = allScores.Count;
        var scoreChanges = new List<ScoreChange>();

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

                if (!_force && Math.Abs(newPp - score.Pp) < 0.001)
                    continue;

                scoreChanges.Add(new ScoreChange(score.Id, score.BeatmapId, score.Mode, score.Pp, newPp));

                if (!_dryRun)
                {
                    await _db.Scores
                        .Where(s => s.Id == score.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Pp, newPp), cancellationToken)
                        .ConfigureAwait(false);
                }

                recalculated++;
            }

            var processed = Math.Min(batchStart + _batchSize, total);
            Console.Write($"\r  Scores: {processed}/{total} ({processed * 100 / total}%)   ");
        }

        Console.WriteLine();

        // Step 3: recompute weighted PP + accuracy for each affected user+mode.
        _log.LogInformation("Updating stats for {Count} user+mode pairs", updatedUserModes.Count);

        var usersUpdated = 0;
        var userStatChanges = new List<UserStatChange>();
        foreach (var (userId, mode) in updatedUserModes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = await RecalcUserStatsAsync(userId, mode, cancellationToken).ConfigureAwait(false);
            if (change is not null)
                userStatChanges.Add(change);
            usersUpdated++;
        }

        return new RecalculationResult(total, recalculated, usersUpdated, scoreChanges, userStatChanges);
    }

    private async Task<UserStatChange?> RecalcUserStatsAsync(int userId, int mode, CancellationToken cancellationToken)
    {
        // Fetch all Best scores for this user+mode ordered by PP desc (same as submission path).
        var bestScores = await _db.Scores.AsNoTracking()
            .Where(s => s.UserId == userId && s.Mode == mode && s.Status == ScoreStatus.Best && s.Pp > 0)
            .OrderByDescending(s => s.Pp)
            .Select(s => new { s.Pp, s.Mode, s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (bestScores.Count == 0)
            return null;

        var n = bestScores.Count;

        // Weighted PP: Σ(pp_i × 0.95^i) + bonus (same formula as submission path + bancho.py)
        var weightedPp = bestScores.Select((s, i) => s.Pp * Math.Pow(0.95, i)).Sum();
        var bonusPp = 416.6667 * (1 - Math.Pow(0.9994, n));
        var totalPp = weightedPp + bonusPp;

        // Weighted accuracy: Σ(acc_i × 0.95^i) × 100 / (20 × (1 − 0.95^n))
        var weightedAcc = bestScores.Select((s, i) =>
        {
            var acc = ComputeAccuracy(s.Mode, s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss);
            return acc * Math.Pow(0.95, i);
        }).Sum();
        var bonusAcc = 100.0 / (20.0 * (1 - Math.Pow(0.95, n)));
        var totalAcc = (float)(weightedAcc * bonusAcc / 100.0);

        // Fetch old stats before writing so we can report the delta.
        var stats = await _db.UserStats
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken)
            .ConfigureAwait(false);

        var oldPp  = stats?.Pp ?? 0.0;
        var oldAcc = stats?.Accuracy ?? 0f;
        var change = new UserStatChange(userId, mode, oldPp, totalPp, oldAcc, totalAcc);

        if (_dryRun)
            return change;

        if (stats is null)
        {
            stats = new UserStats { UserId = userId, Mode = mode };
            _db.UserStats.Add(stats);
        }

        stats.Pp = totalPp;
        stats.Accuracy = totalAcc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        return change;
    }

    private static double ComputeAccuracy(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss) =>
        AccuracyCalculator.Compute(mode, n300, n100, n50, ngeki, nkatu, nmiss);
}
