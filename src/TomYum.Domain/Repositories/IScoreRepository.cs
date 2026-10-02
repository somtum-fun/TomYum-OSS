using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

public interface IScoreRepository
{
    Task<Score> AddAsync(Score score, CancellationToken cancellationToken = default);
    Task<Score?> GetPersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);

    /// <summary>A player's best stable play on a map (lazer plays left out), as stable's leaderboard ranks it.</summary>
    Task<Score?> GetStablePersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    /// <summary>
    /// A map's leaderboard. Restricted players are left out, except <paramref name="viewerId"/>
    /// (a restricted player sees their own scores in place; nobody else sees them).
    /// </summary>
    Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId = null, CancellationToken cancellationToken = default);

    /// <summary>A map's leaderboard narrowed by <paramref name="filter"/> (osu!'s selected mods / friends / country tabs).</summary>
    Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId, LeaderboardFilter filter, CancellationToken cancellationToken = default);
    Task MarkPreviousBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(double Pp, float Accuracy)>> GetBestPerformancesAsync(int userId, int mode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrows a leaderboard: only scores with exactly these <see cref="Mods"/>, and/or only these players.
/// <see cref="StableOnly"/>: stable's own board, as osu! has it (lazer plays aren't on it): each player's
/// best stable play, whatever their best overall is.
/// </summary>
public sealed record LeaderboardFilter(int? Mods = null, IReadOnlyCollection<int>? UserIds = null, bool StableOnly = false);
