using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class UserAchievementRepository : IUserAchievementRepository
{
    private readonly TomYumDbContext _db;

    public UserAchievementRepository(TomYumDbContext db) => _db = db;

    public async Task<IReadOnlySet<int>> GetUnlockedIdsAsync(int userId, CancellationToken cancellationToken = default)
    {
        var ids = await _db.UserAchievements.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.AchievementId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }

    public async Task<bool> AddAsync(int userId, int achievementId, DateTime unlockedAt, CancellationToken cancellationToken = default)
    {
        // Guard against a race/duplicate: skip if the pair already exists.
        var exists = await _db.UserAchievements.AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.AchievementId == achievementId, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        _db.UserAchievements.Add(new UserAchievement
        {
            UserId = userId,
            AchievementId = achievementId,
            UnlockedAt = unlockedAt,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
