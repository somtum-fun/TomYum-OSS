using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;

namespace TomYum.Persistence.Repositories;

public sealed class UserStatsRepository : IUserStatsRepository
{
    private readonly TomYumDbContext _db;
    private readonly RestrictedUsers? _restricted;

    public UserStatsRepository(TomYumDbContext db, RestrictedUsers? restricted = null)
    {
        _db = db;
        _restricted = restricted;
    }

    public Task<UserStats?> GetAsync(int userId, int mode, CancellationToken cancellationToken = default) =>
        _db.UserStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken);

    public async Task<UserStats> GetOrCreateAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(userId, mode, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var stats = new UserStats { UserId = userId, Mode = mode };
        _db.UserStats.Add(stats);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(stats).State = EntityState.Detached;
        return stats;
    }

    public async Task<UserStats> UpdateAsync(UserStats stats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _db.UserStats.Update(stats);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(stats).State = EntityState.Detached;
        return stats;
    }

    public Task<int> GetGlobalRankAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        // Restricted players have no rank and don't count towards anyone else's.
        var restricted = _restricted?.Ids ?? [];
        if (restricted.Contains(userId))
            return Task.FromResult(0);

        // Inactive players (RankingInactivity: no score in the mode for a while) have no rank and
        // don't count towards anyone else's, until their next score.
        if (RankingInactivity.CutoffUtc(DateTimeOffset.UtcNow) is { } cutoff)
        {
            var cutoffAt = new DateTimeOffset(cutoff, TimeSpan.Zero);
            return GetGlobalRankActiveAsync(userId, mode, restricted, cutoffAt, cancellationToken);
        }

        return _db.UserStats.AsNoTracking()
            .Where(s => s.Mode == mode && !restricted.Contains(s.UserId))
            .CountAsync(s => s.Pp > _db.UserStats.Where(x => x.UserId == userId && x.Mode == mode).Select(x => x.Pp).FirstOrDefault(), cancellationToken)
            .ContinueWith(t => t.Result + 1, TaskContinuationOptions.ExecuteSynchronously);
    }

    private async Task<int> GetGlobalRankActiveAsync(int userId, int mode, IReadOnlyCollection<int> restricted, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!await _db.Scores.AsNoTracking().AnyAsync(x => x.UserId == userId && x.Mode == mode && x.SubmittedAt >= cutoff, cancellationToken).ConfigureAwait(false))
            return 0;
        var above = await _db.UserStats.AsNoTracking()
            .Where(s => s.Mode == mode && !restricted.Contains(s.UserId)
                        && s.Pp > _db.UserStats.Where(x => x.UserId == userId && x.Mode == mode).Select(x => x.Pp).FirstOrDefault()
                        && _db.Scores.Any(x => x.UserId == s.UserId && x.Mode == mode && x.SubmittedAt >= cutoff))
            .CountAsync(cancellationToken).ConfigureAwait(false);
        return above + 1;
    }
}
