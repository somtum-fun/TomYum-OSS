using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>A score as the GMT panel lists it.</summary>
public sealed record ModeratedScore(
    long Id, int UserId, int BeatmapId, string Beatmap, int Mode, long Score, double Pp, float Accuracy,
    string Grade, int Mods, int MaxCombo, bool Passed, string Status, DateTimeOffset SubmittedAt);

/// <summary>What a removal did.</summary>
public sealed record ScoreRemovalResult(int Removed, IReadOnlyList<int> AffectedModes);

/// <summary>
/// Staff score removal (GMT panel): delete single scores or wipe a player's scores. Afterwards the
/// next-best score on each affected map becomes the best, the player's stats are recomputed the way
/// score submission computes them, the website copies are deleted (and dropped from its search),
/// replays are deleted, and the action is logged.
/// </summary>
public sealed class ScoreModeration
{
    private readonly TomYumDbContext _db;
    private readonly IStaffLogRepository _log;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<ScoreModeration> _logger;
    private readonly OsuWebScoreMirror? _mirror;
    private readonly OsuWebScoreIndexing? _indexing;
    private readonly OsuWeb.OsuWebInterop? _interop;

    public ScoreModeration(
        TomYumDbContext db,
        IStaffLogRepository log,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<ScoreModeration> logger,
        OsuWebScoreMirror? mirror = null,
        OsuWebScoreIndexing? indexing = null,
        OsuWeb.OsuWebInterop? interop = null)
    {
        _db = db;
        _log = log;
        _configuration = configuration;
        _time = time;
        _logger = logger;
        _mirror = mirror;
        _indexing = indexing;
        _interop = interop;
    }

    /// <summary>A player's scores, newest first (optionally one mode, or only their bests by pp).</summary>
    public async Task<IReadOnlyList<ModeratedScore>> ListAsync(int userId, int? mode, bool bestOnly, int limit, CancellationToken cancellationToken = default)
    {
        var scores = _db.Scores.AsNoTracking().Where(s => s.UserId == userId);
        if (mode is { } m)
            scores = scores.Where(s => s.Mode == m);
        scores = bestOnly
            ? scores.Where(s => s.Status == ScoreStatus.Best).OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue)
            : scores.OrderByDescending(s => s.Id);

        var page = await scores.Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await DescribeAsync(page, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes one score; null when it doesn't exist.</summary>
    public async Task<ScoreRemovalResult?> RemoveAsync(long scoreId, int actorId, string? reason, CancellationToken cancellationToken = default)
    {
        var score = await _db.Scores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scoreId, cancellationToken).ConfigureAwait(false);
        if (score is null)
            return null;

        var described = (await DescribeAsync([score], cancellationToken).ConfigureAwait(false))[0];
        var result = await RemoveScoresAsync(score.UserId, [score], cancellationToken).ConfigureAwait(false);
        await LogAsync(actorId, "score.remove", score.UserId, Invariant(
            $"Removed score #{score.Id}: {described.Beatmap} ({GameMode.ToShortName(score.Mode)}, {score.ScoreValue:N0} score, {score.Pp:0}pp)"), reason, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Removes all of a player's scores (in one mode, or all modes).</summary>
    public async Task<ScoreRemovalResult> WipeAsync(int userId, int? mode, int actorId, string? reason, CancellationToken cancellationToken = default)
    {
        var scores = _db.Scores.AsNoTracking().Where(s => s.UserId == userId);
        if (mode is { } m)
            scores = scores.Where(s => s.Mode == m);
        var all = await scores.ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = await RemoveScoresAsync(userId, all, cancellationToken).ConfigureAwait(false);
        var which = mode is { } wiped ? GameMode.ToShortName(wiped) : "all modes";
        await LogAsync(actorId, "user.wipe_scores", userId, Invariant($"Wiped {result.Removed} score(s) ({which})"), reason, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<ScoreRemovalResult> RemoveScoresAsync(int userId, List<Score> scores, CancellationToken cancellationToken)
    {
        if (scores.Count == 0)
            return new ScoreRemovalResult(0, []);

        var ids = scores.Select(s => s.Id).ToList();
        foreach (var chunk in ids.Chunk(500))
            await _db.Scores.Where(s => chunk.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A removed best leaves its map without one: the next-best remaining score takes over.
        foreach (var map in scores.Where(s => s.Status == ScoreStatus.Best).Select(s => (s.BeatmapMd5, s.Mode)).Distinct())
            await PromoteNextBestAsync(userId, map.BeatmapMd5, map.Mode, cancellationToken).ConfigureAwait(false);

        var modes = scores.Select(s => s.Mode).Distinct().Order().ToList();
        foreach (var mode in modes)
        {
            var removed = scores.Where(s => s.Mode == mode).ToList();
            await RecomputeStatsAsync(userId, mode, removed.Count, removed.Sum(s => s.ScoreValue), cancellationToken).ConfigureAwait(false);
        }

        await RemoveFromWebsiteAsync(userId, ids, modes, cancellationToken).ConfigureAwait(false);
        DeleteReplays(ids);
        return new ScoreRemovalResult(ids.Count, modes);
    }

    /// <summary>Makes the best remaining passed score on the map the player's best (pp first on relax/autopilot boards, score otherwise).</summary>
    private async Task PromoteNextBestAsync(int userId, string md5, int mode, CancellationToken cancellationToken)
    {
        var candidates = _db.Scores.Where(s => s.UserId == userId && s.BeatmapMd5 == md5 && s.Mode == mode && s.Status == ScoreStatus.Submitted && s.Passed);
        var next = await (GameMode.UsesPpLeaderboard(mode)
                ? candidates.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue)
                : candidates.OrderByDescending(s => s.ScoreValue))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (next is null)
            return;

        next.Status = ScoreStatus.Best;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(next).State = EntityState.Detached;
    }

    /// <summary>Stats after removing scores: pp/accuracy/ranked score from the remaining bests, totals minus what was removed.</summary>
    private async Task RecomputeStatsAsync(int userId, int mode, int removedCount, long removedScore, CancellationToken cancellationToken)
    {
        var stats = await _db.UserStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken).ConfigureAwait(false);
        if (stats is null)
            return;

        var bests = _db.Scores.AsNoTracking().Where(s => s.UserId == userId && s.Mode == mode && s.Status == ScoreStatus.Best);
        stats.RankedScore = await bests.SumAsync(s => s.ScoreValue, cancellationToken).ConfigureAwait(false);
        stats.TotalScore = Math.Max(0, stats.TotalScore - removedScore);
        stats.PlayCount = Math.Max(0, stats.PlayCount - removedCount);

        var performances = await bests.Where(s => s.Pp > 0).OrderByDescending(s => s.Pp)
            .Select(s => new { s.Pp, s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var totals = PerformanceTotals.Compute(performances
            .Select(p => (p.Pp, (float)AccuracyCalculator.Compute(GameMode.AsVanilla(mode), p.Count300, p.Count100, p.Count50, p.CountGeki, p.CountKatu, p.CountMiss)))
            .ToList());
        stats.Pp = totals?.Pp ?? 0;
        stats.Accuracy = totals?.Accuracy ?? 0;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(stats).State = EntityState.Detached;
    }

    private async Task RemoveFromWebsiteAsync(int userId, List<long> scoreIds, List<int> modes, CancellationToken cancellationToken)
    {
        if (_mirror is null)
            return;

        try
        {
            var deleted = await _mirror.DeleteScoresAsync(scoreIds, cancellationToken).ConfigureAwait(false);
            foreach (var mode in modes.Where(OsuWebScores.IsMirrored))
            {
                await _mirror.WriteStatsAsync(userId, mode, cancellationToken).ConfigureAwait(false);
                await _mirror.RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);
            }
            if (_indexing is not null)
                await _indexing.QueueAsync(deleted).ConfigureAwait(false);
            if (_interop is { IsConfigured: true } && await _mirror.GetCountryAsync(userId, cancellationToken).ConfigureAwait(false) is { Length: > 0 } country)
                await _interop.RecalculateCountryAsync(country, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The game's copy is gone either way; the website catches up on the next score.
            _logger.LogWarning(ex, "Removed {Count} score(s) of user {UserId} but updating osu-web failed.", scoreIds.Count, userId);
        }
    }

    private void DeleteReplays(IEnumerable<long> scoreIds)
    {
        var directory = _configuration["Replays:Path"] ?? "data/replays";
        foreach (var id in scoreIds)
        {
            var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{id}.osr"));
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Couldn't delete replay {Path}.", path);
            }
        }
    }

    private async Task<IReadOnlyList<ModeratedScore>> DescribeAsync(IReadOnlyList<Score> scores, CancellationToken cancellationToken)
    {
        var beatmapIds = scores.Select(s => s.BeatmapId).Distinct().ToList();
        var beatmaps = await _db.Beatmaps.AsNoTracking().Where(b => beatmapIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.FullName, cancellationToken).ConfigureAwait(false);

        return scores.Select(s => new ModeratedScore(
            s.Id, s.UserId, s.BeatmapId, beatmaps.GetValueOrDefault(s.BeatmapId) ?? Invariant($"beatmap #{s.BeatmapId}"),
            s.Mode, s.ScoreValue, s.Pp,
            (float)AccuracyCalculator.Compute(GameMode.AsVanilla(s.Mode), s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss),
            s.Grade, s.Mods, s.MaxCombo, s.Passed,
            s.Status switch { ScoreStatus.Best => "best", ScoreStatus.Submitted => "submitted", _ => "failed" },
            s.SubmittedAt)).ToList();
    }

    private async Task LogAsync(int actorId, string action, int userId, string summary, string? reason, CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? summary : $"{summary}. Reason: {reason.Trim()}";
        await _log.AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = actorId,
            Action = action,
            TargetId = userId,
            Summary = text.Length > 512 ? text[..512] : text,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
