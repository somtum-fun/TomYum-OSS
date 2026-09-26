using System.Collections.Concurrent;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>Throwaway in-memory achievement store used when no database is configured (local dev, tests).</summary>
public sealed class InMemoryUserAchievementRepository : IUserAchievementRepository
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, DateTime>> _unlocked = new();

    public Task<IReadOnlySet<int>> GetUnlockedIdsAsync(int userId, CancellationToken cancellationToken = default)
    {
        IReadOnlySet<int> ids = _unlocked.TryGetValue(userId, out var set)
            ? set.Keys.ToHashSet()
            : new HashSet<int>();
        return Task.FromResult(ids);
    }

    public Task<bool> AddAsync(int userId, int achievementId, DateTime unlockedAt, CancellationToken cancellationToken = default)
    {
        var set = _unlocked.GetOrAdd(userId, _ => new ConcurrentDictionary<int, DateTime>());
        return Task.FromResult(set.TryAdd(achievementId, unlockedAt));
    }
}
