using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

public sealed class InMemoryUserStatsRepository : IUserStatsRepository
{
    private readonly ConcurrentDictionary<(int UserId, int Mode), UserStats> _stats = new();

    public Task<UserStats?> GetAsync(int userId, int mode, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stats.TryGetValue((userId, mode), out var s) ? s : null);

    public Task<UserStats> GetOrCreateAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        var stats = _stats.GetOrAdd((userId, mode), _ => new UserStats { UserId = userId, Mode = mode });
        return Task.FromResult(stats);
    }

    public Task<UserStats> UpdateAsync(UserStats stats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _stats[(stats.UserId, stats.Mode)] = stats;
        return Task.FromResult(stats);
    }

    public Task<int> GetGlobalRankAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        if (!_stats.TryGetValue((userId, mode), out var target))
        {
            return Task.FromResult(0);
        }
        var rank = _stats.Values.Count(s => s.Mode == mode && s.Pp > target.Pp) + 1;
        return Task.FromResult(rank);
    }
}
