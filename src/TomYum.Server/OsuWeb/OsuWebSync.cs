using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Mirrors a submitted score to osu-web: the score row, the player's stats for that ruleset, the
/// ruleset's global ranks, and a queue entry so osu-web's score indexer picks the score up (its
/// leaderboards and profile "best" lists read from Elasticsearch). Medals and new accounts go
/// through osu-web's InterOp API when it's configured.
/// </summary>
public sealed class OsuWebSync : IOsuWebSync
{
    private readonly OsuWebScoreMirror _mirror;
    private readonly OsuWebScoreIndexing _indexQueue;
    private readonly OsuWebMedals _medals;
    private readonly OsuWebInterop _interop;
    private readonly ILogger<OsuWebSync> _logger;

    public OsuWebSync(
        OsuWebScoreMirror mirror,
        OsuWebScoreIndexing indexQueue,
        OsuWebMedals medals,
        OsuWebInterop interop,
        ILogger<OsuWebSync> logger)
    {
        _mirror = mirror;
        _indexQueue = indexQueue;
        _medals = medals;
        _interop = interop;
        _logger = logger;
    }

    public async Task ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (!OsuWebScores.IsMirrored(score.Mode))
            return;

        var osuWebScoreId = await _mirror.WriteScoreAsync(score, cancellationToken).ConfigureAwait(false);
        await _mirror.RecordPlayAsync(score, cancellationToken).ConfigureAwait(false);
        await _mirror.WriteStatsAsync(score.UserId, score.Mode, cancellationToken).ConfigureAwait(false);
        await _mirror.RecomputeGlobalRanksAsync(score.Mode, cancellationToken).ConfigureAwait(false);

        if (osuWebScoreId is { } id)
            await _indexQueue.QueueAsync([id]).ConfigureAwait(false);

        await RecalculateCountryAsync(score.UserId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Has osu-web rebuild the player's country ranking data (country rankings and the country filter
    /// of the global ranking). Best effort: rankings just lag behind if it fails.
    /// </summary>
    private async Task RecalculateCountryAsync(int userId, CancellationToken cancellationToken)
    {
        if (!_interop.IsConfigured)
            return;

        try
        {
            if (await _mirror.GetCountryAsync(userId, cancellationToken).ConfigureAwait(false) is { Length: > 0 } country)
                await _interop.RecalculateCountryAsync(country, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "osu-web country recalculation failed for user {UserId}.", userId);
        }
    }

    public async Task MedalsUnlockedAsync(int userId, IReadOnlyList<Medal> medals, int beatmapId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(medals);

        foreach (var medal in medals)
        {
            if (_interop.IsConfigured)
            {
                try
                {
                    await _interop.UnlockMedalAsync(userId, medal.Id, beatmapId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "osu-web InterOp unlock of medal {MedalId} for user {UserId} failed; writing it directly", medal.Id, userId);
                }
            }

            // No InterOp (or it failed): the medal still shows on the profile, just without an activity entry.
            await _medals.InsertUnlockAsync(userId, medal.Id, beatmapId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AccountCreatedAsync(int userId, CancellationToken cancellationToken = default)
    {
        // Without InterOp the account shows up in website search after the scheduled re-index (docs/osu-web.md).
        if (_interop.IsConfigured)
            await _interop.IndexUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }
}
