using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

public interface IUserStatsRepository
{
    Task<UserStats?> GetAsync(int userId, int mode, CancellationToken cancellationToken = default);
    Task<UserStats> GetOrCreateAsync(int userId, int mode, CancellationToken cancellationToken = default);
    Task<UserStats> UpdateAsync(UserStats stats, CancellationToken cancellationToken = default);
    Task<int> GetGlobalRankAsync(int userId, int mode, CancellationToken cancellationToken = default);
}
