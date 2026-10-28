using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Where a website play placed on its leaderboard when it was set: the website's (vanilla plays, by score), or TomYum's
/// for Relax/Autopilot plays (by score, pp breaking ties, as the boards rank them) once TomYum has taken them in.
/// </summary>
public static class ModePlacements
{
    /// <summary>The outcome for a play: its placement (null: not a leaderboard play or not a new best), or not known yet.</summary>
    public readonly record struct Result(ScorePlacement? Placement, bool Pending);

    /// <summary>Where <paramref name="score"/> placed; <see cref="Result.Pending"/> while TomYum hasn't taken a Relax/Autopilot play in yet.</summary>
    public static async Task<Result> PlacementAsync(IServiceProvider services, OsuWebCommunity community, ScoreCard score, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(score);
        if (!OsuWebCommunity.IsRelaxOrAutopilot(score))
            return new Result(await community.GetPlacementAsync(score, cancellationToken).ConfigureAwait(false), false);
        if (!score.Passed || !OsuWebCommunity.LeaderboardStatuses.Contains(score.BeatmapStatus) || services.GetService<TomYumDbContext>() is not { } db)
            return new Result(null, false);

        var mine = await db.Scores.AsNoTracking().FirstOrDefaultAsync(s => s.OsuWebScoreId == score.ScoreId, cancellationToken).ConfigureAwait(false);
        if (mine is null)
            return new Result(null, true);
        if (!mine.Passed || mine.OffBoard || !GameMode.UsesPpLeaderboard(mine.Mode))
            return new Result(null, false);

        var hidden = services.GetService<RestrictedUsers>()?.Ids.ToHashSet() ?? [];
        var earlier = await db.Scores.AsNoTracking()
            .Where(s => s.BeatmapMd5 == mine.BeatmapMd5 && s.Mode == mine.Mode && s.Passed && !s.OffBoard && s.Id < mine.Id
                        && (s.Status == ScoreStatus.Best || s.Status == ScoreStatus.Submitted))
            .Select(s => new { s.Id, s.UserId, s.Pp, s.ScoreValue })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Each player's best before this play, as the board would have shown it.
        var bests = earlier.Where(s => !hidden.Contains(s.UserId))
            .GroupBy(s => s.UserId)
            .Select(g => g.OrderByDescending(s => s.ScoreValue).ThenByDescending(s => s.Pp).ThenBy(s => s.Id).First())
            .ToList();
        if (bests.FirstOrDefault(b => b.UserId == mine.UserId) is { } own && !GameMode.BeatsBest(mine.Mode, mine.Pp, mine.ScoreValue, own.Pp, own.ScoreValue))
            return new Result(null, false);

        var position = 1 + bests.Count(b => b.UserId != mine.UserId && !GameMode.BeatsBest(mine.Mode, mine.Pp, mine.ScoreValue, b.Pp, b.ScoreValue));
        var leader = bests.OrderByDescending(b => b.ScoreValue).ThenByDescending(b => b.Pp).ThenBy(b => b.Id).FirstOrDefault();
        return new Result(new ScorePlacement(position, leader?.UserId), false);
    }
}
