using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

public sealed class InMemoryScoreRepository : IScoreRepository
{
    private readonly ConcurrentDictionary<long, Score> _scores = new();
    private readonly TomYum.Domain.Services.RestrictedUsers? _restricted;

    public InMemoryScoreRepository(TomYum.Domain.Services.RestrictedUsers? restricted = null) => _restricted = restricted;
    private long _nextId = 1;

    public Task<Score> AddAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        var id = Interlocked.Increment(ref _nextId);
        var stored = new Score
        {
            Id = id,
            UserId = score.UserId,
            BeatmapId = score.BeatmapId,
            BeatmapMd5 = score.BeatmapMd5,
            Mode = score.Mode,
            ScoreValue = score.ScoreValue,
            MaxCombo = score.MaxCombo,
            Count300 = score.Count300,
            Count100 = score.Count100,
            Count50 = score.Count50,
            CountMiss = score.CountMiss,
            CountGeki = score.CountGeki,
            CountKatu = score.CountKatu,
            Grade = score.Grade,
            Mods = score.Mods,
            Perfect = score.Perfect,
            Pp = score.Pp,
            Passed = score.Passed,
            Status = score.Status,
            SubmittedAt = score.SubmittedAt,
            FromLazer = score.FromLazer,
            OsuWebScoreId = score.OsuWebScoreId,
            PpHeld = score.PpHeld,
            OffBoard = score.OffBoard,
            LargeTickHits = score.LargeTickHits,
            SliderEndHits = score.SliderEndHits,
            SpeedChange = score.SpeedChange,
            LazerScore = score.LazerScore,
        };
        _scores[id] = stored;
        return Task.FromResult(stored);
    }

    public Task<Score?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_scores.TryGetValue(id, out var score) ? score : null);

    public Task<Score?> GetPersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default)
    {
        var best = _scores.Values
            .Where(s => s.UserId == userId && s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best)
            .FirstOrDefault();
        return Task.FromResult(best);
    }

    public async Task<Score?> GetStablePersonalBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default)
    {
        var best = await GetLeaderboardAsync(beatmapMd5, mode, int.MaxValue, userId, new LeaderboardFilter(UserIds: [userId], StableOnly: true), cancellationToken).ConfigureAwait(false);
        return best.Count > 0 ? best[0] : null;
    }

    public Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId = null, CancellationToken cancellationToken = default) =>
        GetLeaderboardAsync(beatmapMd5, mode, count, viewerId, new LeaderboardFilter(), cancellationToken);

    public Task<IReadOnlyList<Score>> GetLeaderboardAsync(string beatmapMd5, int mode, int count, int? viewerId, LeaderboardFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var filtered = _scores.Values
            .Where(s => s.BeatmapMd5 == beatmapMd5 && s.Mode == mode)
            .Where(s => filter.StableOnly
                ? s.Passed && !s.FromLazer && !s.OffBoard && (s.Status == ScoreStatus.Best || s.Status == ScoreStatus.Submitted)
                : s.Status == ScoreStatus.Best)
            .Where(s => filter.Mods is not { } mods || s.Mods == mods)
            .Where(s => filter.UserIds is not { } ids || ids.Contains(s.UserId))
            .Where(s => _restricted is null || s.UserId == viewerId || !_restricted.Contains(s.UserId));

        // Relax/Autopilot leaderboards rank by pp; vanilla ranks by score (bancho.py's rule).
        var ordered = GameMode.UsesPpLeaderboard(mode)
            ? filtered.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue)
            : filtered.OrderByDescending(s => s.ScoreValue);

        // Stable's board: each player's best stable play (one per player).
        IReadOnlyList<Score> board = (filter.StableOnly ? ordered.ThenBy(s => s.Id).DistinctBy(s => s.UserId) : ordered).Take(count).ToList();
        return Task.FromResult(board);
    }

    public Task MarkPreviousBestAsync(int userId, string beatmapMd5, int mode, CancellationToken cancellationToken = default)
    {
        foreach (var s in _scores.Values.Where(s => s.UserId == userId && s.BeatmapMd5 == beatmapMd5 && s.Mode == mode && s.Status == ScoreStatus.Best))
            s.Status = ScoreStatus.Submitted;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(double Pp, float Accuracy)>> GetBestPerformancesAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(double, float)> result = _scores.Values
            .Where(s => s.UserId == userId && s.Mode == mode && s.Status == ScoreStatus.Best && s.Pp > 0)
            .OrderByDescending(s => s.Pp)
            .Select(s =>
            {
                var total = s.Count300 + s.Count100 + s.Count50 + s.CountMiss;
                var acc = total > 0 ? (float)((s.Count300 * 300.0 + s.Count100 * 100.0 + s.Count50 * 50.0) / (total * 300.0)) : 0f;
                return (s.Pp, acc);
            })
            .ToList();
        return Task.FromResult(result);
    }
}
