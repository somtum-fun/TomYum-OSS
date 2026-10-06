using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;
using TomYum.Server.Beatmaps;

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
    private readonly IConfiguration _configuration;
    private readonly OsuWebPerformanceSync _performance;
    private readonly ILogger<OsuWebSync> _logger;
    private readonly TomYum.Performance.LegacyScoreConverter? _converter;

    public OsuWebSync(
        OsuWebScoreMirror mirror,
        OsuWebScoreIndexing indexQueue,
        OsuWebMedals medals,
        OsuWebInterop interop,
        IConfiguration configuration,
        OsuWebPerformanceSync performance,
        ILogger<OsuWebSync> logger,
        TomYum.Performance.LegacyScoreConverter? converter = null)
    {
        _mirror = mirror;
        _indexQueue = indexQueue;
        _medals = medals;
        _interop = interop;
        _configuration = configuration;
        _performance = performance;
        _logger = logger;
        _converter = converter;
    }

    public async Task<OsuWebPerformance?> ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (!OsuWebScores.IsMirrored(score.Mode))
            return null;

        // The replay is saved before this runs; the website downloads it from TomYum.
        // In lazer's scoring too, like ppy stores stable scores (lazer shows and sorts by it).
        var standardised = _converter is null ? null : await OsuWebStandardisation.ConvertAsync(_converter, score, _logger, cancellationToken).ConfigureAwait(false);
        var osuWebScoreId = await _mirror.WriteScoreAsync(score, ReplayFiles.Exists(_configuration, score.Id), standardised, cancellationToken).ConfigureAwait(false);
        await _mirror.RecordPlayAsync(score, cancellationToken).ConfigureAwait(false);
        await _mirror.WriteStatsAsync(score.UserId, score.Mode, cancellationToken).ConfigureAwait(false);

        OsuWebPerformance? combined = null;
        if (_performance.Enabled && osuWebScoreId is { } processed)
        {
            // ppy's processor gives the score its pp and grade counts. Give it a moment so the
            // ranking panel shows the new total; if it's slower, the watcher catches up later.
            await _performance.QueueAsync([processed], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!await _mirror.WaitForProcessingAsync(processed, ProcessingWait, cancellationToken).ConfigureAwait(false))
                _logger.LogInformation("Score processor hasn't handled website score {ScoreId} yet; pp follows when it does.", processed);
            combined = await _performance.ApplyAsync(score.UserId, score.Mode, recompute: true, cancellationToken).ConfigureAwait(false);
        }

        await _mirror.RecomputeGlobalRanksAsync(score.Mode, cancellationToken).ConfigureAwait(false);

        if (osuWebScoreId is { } id)
            await _indexQueue.QueueAsync([id]).ConfigureAwait(false);

        await RecalculateCountryAsync(score.UserId, cancellationToken).ConfigureAwait(false);
        return combined;
    }

    /// <summary>How long a submission waits for ppy's score processor.</summary>
    private static readonly TimeSpan ProcessingWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Has osu-web rebuild the player's country ranking data (country rankings and the country filter
    /// of the global ranking). Best effort: rankings just lag behind if it fails.
    /// </summary>
    /// <summary>
    /// Rebuilds the country ranking data of these players' countries, once per country: after lazer
    /// plays (ppy's processor updates the players' stats, not their countries' rankings).
    /// </summary>
    public async Task RecalculateCountriesOfAsync(IEnumerable<int> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (!_interop.IsConfigured)
            return;

        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var userId in userIds.Distinct())
        {
            if (await _mirror.GetCountryAsync(userId, cancellationToken).ConfigureAwait(false) is { Length: > 0 } country)
                countries.Add(country);
        }
        foreach (var country in countries)
        {
            try
            {
                await _interop.RecalculateCountryAsync(country, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "osu-web country recalculation failed for {Country}.", country);
            }
        }
    }

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

    public Task MedalsUnlockedAsync(int userId, IReadOnlyList<Medal> medals, int beatmapId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(medals);
        return UnlockMedalIdsAsync(userId, medals.Select(m => m.Id).ToList(), beatmapId, cancellationToken);
    }

    /// <summary>Unlocks medals by id on the website (with its notification and activity entry when InterOp works).</summary>
    public async Task UnlockMedalIdsAsync(int userId, IReadOnlyList<int> medalIds, int beatmapId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(medalIds);

        foreach (var medalId in medalIds)
        {
            if (_interop.IsConfigured)
            {
                try
                {
                    await _interop.UnlockMedalAsync(userId, medalId, beatmapId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "osu-web InterOp unlock of medal {MedalId} for user {UserId} failed; writing it directly", medalId, userId);
                }
            }

            // No InterOp (or it failed): the medal still shows on the profile, just without an activity entry.
            await _medals.InsertUnlockAsync(userId, medalId, beatmapId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AccountCreatedAsync(int userId, CancellationToken cancellationToken = default)
    {
        // Without InterOp the account shows up in website search after the scheduled re-index (docs/osu-web.md).
        if (_interop.IsConfigured)
            await _interop.IndexUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a player is told when the website's sign-up rules can't be asked.</summary>
    public const string RegistrationUnavailable = "Registration is unavailable right now. Please try again in a few minutes.";

    public async Task<string?> CheckRegistrationAsync(string? ip, string username, string? country, bool quick, CancellationToken cancellationToken = default)
    {
        // Without InterOp there are no website rules to ask (docs/osu-web.md).
        if (!_interop.IsConfigured)
            return null;

        try
        {
            return await _interop.CheckRegistrationAsync(ip ?? string.Empty, username, country, quick, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // The keystroke check lets the player go on; the final one makes no account unchecked.
            _logger.LogWarning(ex, "Asking osu-web's sign-up rules about {Username} failed", username);
            return quick ? null : RegistrationUnavailable;
        }
    }

    public async Task RegistrationCreatedAsync(int userId, string username, string? ip, string? country, CancellationToken cancellationToken = default)
    {
        if (_interop.IsConfigured && !string.IsNullOrEmpty(ip))
            await _interop.RegistrationCreatedAsync(userId, username, ip, country, cancellationToken).ConfigureAwait(false);
    }
}
