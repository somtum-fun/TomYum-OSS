using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

public interface IScoreRepository
{
    Task<Score> AddAsync(Score score, CancellationToken cancellationToken = default);
    Task<Score?> GetPersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, CancellationToken cancellationToken = default);
    Task MarkPreviousBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(double Pp, float Accuracy)>> GetBestPerformancesAsync(int userId, int mode, CancellationToken cancellationToken = default);
}
