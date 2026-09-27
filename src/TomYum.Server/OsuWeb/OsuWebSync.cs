using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Mirrors a submitted score to osu-web: the score row, the player's stats for that ruleset, the
/// ruleset's global ranks, and a queue entry so osu-web's score indexer picks the score up (its
/// leaderboards and profile "best" lists read from Elasticsearch).
/// </summary>
public sealed class OsuWebSync : IOsuWebSync
{
    private readonly OsuWebScoreMirror _mirror;
    private readonly OsuWebScoreIndexing _indexQueue;

    public OsuWebSync(OsuWebScoreMirror mirror, OsuWebScoreIndexing indexQueue)
    {
        _mirror = mirror;
        _indexQueue = indexQueue;
    }

    public async Task ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (!OsuWebScores.IsMirrored(score.Mode))
            return;

        var osuWebScoreId = await _mirror.WriteScoreAsync(score, cancellationToken).ConfigureAwait(false);
        await _mirror.WriteStatsAsync(score.UserId, score.Mode, cancellationToken).ConfigureAwait(false);
        await _mirror.RecomputeGlobalRanksAsync(score.Mode, cancellationToken).ConfigureAwait(false);

        if (osuWebScoreId is { } id)
            await _indexQueue.QueueAsync([id]).ConfigureAwait(false);
    }
}
