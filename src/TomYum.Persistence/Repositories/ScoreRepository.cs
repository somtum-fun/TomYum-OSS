using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;

namespace TomYum.Persistence.Repositories;

public sealed class ScoreRepository : IScoreRepository
{
    private readonly TomYumDbContext _db;
    private readonly RestrictedUsers? _restricted;

    public ScoreRepository(TomYumDbContext db, RestrictedUsers? restricted = null)
    {
        _db = db;
        _restricted = restricted;
    }

    public async Task<Score> AddAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        _db.Scores.Add(score);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(score).State = EntityState.Detached;
        return score;
    }

    public Task<Score?> GetPersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default) =>
        _db.Scores.AsNoTracking()
            .Where(s => s.UserId == userId && s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, CancellationToken cancellationToken = default)
    {
        var query = _db.Scores.AsNoTracking()
            .Where(s => s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best);

        // Restricted players stay off leaderboards (their scores are kept for if they're unrestricted).
        var restricted = _restricted?.Ids;
        if (restricted is { Count: > 0 })
            query = query.Where(s => !restricted.Contains(s.UserId));

        // Relax/Autopilot leaderboards rank by pp; vanilla ranks by score (bancho.py's rule).
        query = GameMode.UsesPpLeaderboard(mode)
            ? query.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue)
            : query.OrderByDescending(s => s.ScoreValue);

        return await query.Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkPreviousBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default)
    {
        await _db.Scores
            .Where(s => s.UserId == userId && s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScoreStatus.Submitted), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(double Pp, float Accuracy)>> GetBestPerformancesAsync(int userId, int mode, CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking()
            .Where(s => s.UserId == userId && s.Mode == mode && s.Status == ScoreStatus.Best && s.Pp > 0)
            .OrderByDescending(s => s.Pp)
            .Select(s => new { s.Pp, s.Mode, s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss })
            .ToListAsync(cancellationToken)
            .ContinueWith(t => (IReadOnlyList<(double, float)>)t.Result
                .Select(r =>
                {
                    var acc = (float)AccuracyCalculator.Compute(GameMode.AsVanilla(r.Mode), r.Count300, r.Count100, r.Count50, r.CountGeki, r.CountKatu, r.CountMiss);
                    return (r.Pp, acc);
                })
                .ToList(), TaskScheduler.Default)
            .ConfigureAwait(false);
}
