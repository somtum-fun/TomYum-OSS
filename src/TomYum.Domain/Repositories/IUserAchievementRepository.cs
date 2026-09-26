namespace TomYum.Domain.Repositories;

/// <summary>
/// Persistence for per-user achievement (medal) unlocks. Achievement definitions live in code;
/// this stores only which medals a user has unlocked, so score submission can skip already-earned
/// ones and record newly-earned ones.
/// </summary>
public interface IUserAchievementRepository
{
    /// <summary>Returns the set of achievement ids the user has already unlocked.</summary>
    Task<IReadOnlySet<int>> GetUnlockedIdsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that <paramref name="userId"/> unlocked <paramref name="achievementId"/> at
    /// <paramref name="unlockedAt"/>. Idempotent: a duplicate unlock is ignored (returns <c>false</c>).
    /// </summary>
    Task<bool> AddAsync(int userId, int achievementId, DateTime unlockedAt, CancellationToken cancellationToken = default);
}
