using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Bancho;

/// <summary>
/// Fills a stable player's panel numbers (ranked and total score, play count, accuracy, pp, rank).
/// With the website, the four vanilla modes show its numbers, rank included, which count stable and
/// lazer plays together, so the stable client (whose level comes from total score), lazer and the
/// profile all agree; relax and autopilot are TomYum's own. pp is rounded as the website rounds it.
/// </summary>
public sealed class PresenceStats
{
    private readonly IUserStatsRepository _stats;
    private readonly OsuWebScoreMirror? _website;

    public PresenceStats(IUserStatsRepository stats, OsuWebScoreMirror? website = null)
    {
        _stats = stats;
        _website = website;
    }

    /// <summary>
    /// Fills <paramref name="presence"/> for <paramref name="gameMode"/>; <paramref name="known"/> is
    /// TomYum's stats row when the caller already has it. False when there's nothing to show.
    /// </summary>
    public async Task<bool> FillAsync(BanchoPresence presence, int userId, int gameMode, UserStats? known = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var stats = known ?? await _stats.GetAsync(userId, gameMode, cancellationToken).ConfigureAwait(false);
        var website = _website is not null && OsuWebScores.IsMirrored(gameMode)
            ? await _website.ReadCountersAsync(userId, gameMode, cancellationToken).ConfigureAwait(false)
            : null;
        if (stats is null && website is null)
            return false;

        presence.RankedScore = website?.RankedScore ?? stats!.RankedScore;
        presence.TotalScore = website?.TotalScore ?? stats!.TotalScore;
        presence.PlayCount = website?.PlayCount ?? stats!.PlayCount;
        presence.Accuracy = website?.Accuracy ?? stats!.Accuracy;
        presence.Performance = Pp(website?.Pp ?? stats!.Pp);
        presence.GlobalRank = website is { GlobalRank: > 0 }
            ? website.GlobalRank
            : await _stats.GetGlobalRankAsync(userId, gameMode, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>What the panel shows, to tell whether it changed.</summary>
    public static (long, long, int, float, short, int) Snapshot(BanchoPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        return (presence.RankedScore, presence.TotalScore, presence.PlayCount, presence.Accuracy, presence.Performance, presence.GlobalRank);
    }

    /// <summary>pp as the panel carries it: rounded like the website shows it, within a short.</summary>
    public static short Pp(double pp) => (short)Math.Clamp((int)Math.Round(pp, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
}
