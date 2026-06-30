using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class ScoreRepository : IScoreRepository
{
    private readonly TomYumDbContext _db;

    public ScoreRepository(TomYumDbContext db) => _db = db;

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

    public async Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking()
            .Where(s => s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best)
            .OrderByDescending(s => s.ScoreValue)
            .Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

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
            .Select(s => new { s.Pp, s.Count300, s.Count100, s.Count50, s.CountMiss })
            .ToListAsync(cancellationToken)
            .ContinueWith(t => (IReadOnlyList<(double, float)>)t.Result
                .Select(r =>
                {
                    var total = r.Count300 + r.Count100 + r.Count50 + r.CountMiss;
                    var acc = total > 0 ? (float)((r.Count300 * 300.0 + r.Count100 * 100.0 + r.Count50 * 50.0) / (total * 300.0)) : 0f;
                    return (r.Pp, acc);
                })
                .ToList(), TaskScheduler.Default)
            .ConfigureAwait(false);
}
