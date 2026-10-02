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

    public async Task<Score?> GetStablePersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default)
    {
        var best = await StableBestsAsync(_db.Scores.AsNoTracking().Where(s => s.UserId == userId), beatmapMd5, mode, 1, cancellationToken).ConfigureAwait(false);
        return best.Count > 0 ? best[0] : null;
    }

    /// <summary>The best stable play of each player in <paramref name="scores"/> on a map, best first.</summary>
    private static async Task<IReadOnlyList<Score>> StableBestsAsync(IQueryable<Score> scores, string beatmapMd5, int mode, int count, CancellationToken cancellationToken)
    {
        // A player's stable best is their Best when that's a stable play, else their best earlier
        // stable submission: a lazer play can be someone's Best overall.
        var candidates = scores.Where(s => s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Passed && !s.FromLazer
                                           && (s.Status == ScoreStatus.Best || s.Status == ScoreStatus.Submitted));
        candidates = GameMode.UsesPpLeaderboard(mode)
            ? candidates.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue).ThenBy(s => s.Id)
            : candidates.OrderByDescending(s => s.ScoreValue).ThenBy(s => s.Id);
        var best = new List<Score>();
        var seen = new HashSet<int>();
        await foreach (var score in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(score.UserId))
            {
                best.Add(score);
                if (best.Count == count)
                    break;
            }
        }
        return best;
    }

    public Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId = null, CancellationToken cancellationToken = default) =>
        GetLeaderboardAsync(beatmapMd5, mode, count, viewerId, new LeaderboardFilter(), cancellationToken);

    public async Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId, LeaderboardFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = filter.StableOnly
            ? _db.Scores.AsNoTracking()
            : _db.Scores.AsNoTracking().Where(s => s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best);
        if (filter.Mods is { } mods)
            query = query.Where(s => s.Mods == mods);
        if (filter.UserIds is { } userIds)
        {
            var ids = userIds.ToList();
            query = query.Where(s => ids.Contains(s.UserId));
        }

        // Restricted players stay off leaderboards (their scores are kept for if they're unrestricted),
        // but see their own (bancho.py's shadow leaderboard).
        var restricted = _restricted?.Ids.Where(id => id != viewerId).ToList();
        if (restricted is { Count: > 0 })
            query = query.Where(s => !restricted.Contains(s.UserId));

        if (filter.StableOnly)
            return await StableBestsAsync(query, beatmapMd5, mode, count, cancellationToken).ConfigureAwait(false);

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
