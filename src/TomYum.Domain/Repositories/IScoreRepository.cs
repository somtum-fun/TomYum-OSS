using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

public interface IScoreRepository
{
    Task<Score> AddAsync(Score score, CancellationToken cancellationToken = default);
    Task<Score?> GetPersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    /// <summary>
    /// A map's leaderboard. Restricted players are left out, except <paramref name="viewerId"/>
    /// (a restricted player sees their own scores in place; nobody else sees them).
    /// </summary>
    Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId = null, CancellationToken cancellationToken = default);
    Task MarkPreviousBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(double Pp, float Accuracy)>> GetBestPerformancesAsync(int userId, int mode, CancellationToken cancellationToken = default);
}
