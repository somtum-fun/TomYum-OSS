using TomYum.Bancho;
using TomYum.Caching;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// With ppy's score processor (<c>OsuWeb:ScoreProcessor</c>, docs/lazer.md), a player's pp and
/// accuracy combine their stable and lazer scores and live on the website. This hands scores to the
/// processor and brings the combined numbers back into TomYum's stats and the in-game panel, so
/// both clients and the website show the same pp.
/// </summary>
public sealed class OsuWebPerformanceSync
{
    private readonly OsuWebScoreMirror _mirror;
    private readonly IUserStatsRepository _userStats;
    private readonly PlayerRegistry _registry;
    private readonly OsuWebScoreProcessing? _processing;

    public OsuWebPerformanceSync(OsuWebScoreMirror mirror, IUserStatsRepository userStats, PlayerRegistry registry, OsuWebScoreProcessing? processing = null)
    {
        _mirror = mirror;
        _userStats = userStats;
        _registry = registry;
        _processing = processing;
    }

    /// <summary>Whether the processor owns pp on the website (otherwise everything here does nothing).</summary>
    public bool Enabled => _mirror.UsesScoreProcessor && _processing is not null;

    /// <summary>
    /// Queues website scores for the processor; <paramref name="reprocess"/> redoes ones it already
    /// handled (after their map's status changed).
    /// </summary>
    public async Task QueueAsync(IReadOnlyCollection<ulong> osuWebScoreIds, bool reprocess = false, CancellationToken cancellationToken = default)
    {
        if (!Enabled || osuWebScoreIds.Count == 0)
            return;

        var items = await _mirror.BuildProcessingItemsAsync(osuWebScoreIds, reprocess, cancellationToken).ConfigureAwait(false);
        await _processing!.QueueAsync(items).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the player's combined pp and accuracy into TomYum's stats for <paramref name="mode"/>.
    /// <paramref name="recompute"/> first recomputes them on the website from its scores (for changes
    /// the processor doesn't see); otherwise the website's current values are taken. Null when the
    /// website has no stats row for the player.
    /// </summary>
    public async Task<OsuWebPerformance?> ApplyAsync(int userId, int mode, bool recompute, CancellationToken cancellationToken = default)
    {
        if (!Enabled || !OsuWebScores.IsMirrored(mode))
            return null;

        var performance = recompute
            ? await _mirror.RecomputeTotalPerformanceAsync(userId, mode, cancellationToken).ConfigureAwait(false)
            : await _mirror.ReadPerformanceAsync(userId, mode, cancellationToken).ConfigureAwait(false);
        if (performance is null)
            return null;

        var stats = await _userStats.GetOrCreateAsync(userId, mode, cancellationToken).ConfigureAwait(false);
        if (Math.Abs(stats.Pp - performance.Pp) > 0.001 || Math.Abs(stats.Accuracy - performance.Accuracy) > 0.00001f)
        {
            stats.Pp = performance.Pp;
            stats.Accuracy = performance.Accuracy;
            await _userStats.UpdateAsync(stats, cancellationToken).ConfigureAwait(false);
        }
        return performance;
    }

    /// <summary>Re-sends the player's stats (from TomYum's stats) to everyone in-game, if they're online in <paramref name="mode"/>.</summary>
    public async Task RefreshPresenceAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        if (_registry.GetByUserId(userId) is not { } player || player.Presence.GameMode != mode)
            return;

        var stats = await _userStats.GetOrCreateAsync(userId, mode, cancellationToken).ConfigureAwait(false);
        var presence = player.Presence;
        presence.RankedScore = stats.RankedScore;
        presence.TotalScore = stats.TotalScore;
        presence.PlayCount = stats.PlayCount;
        presence.Accuracy = stats.Accuracy;
        presence.Performance = (short)Math.Clamp((int)stats.Pp, short.MinValue, short.MaxValue);
        presence.GlobalRank = await _userStats.GetGlobalRankAsync(userId, mode, cancellationToken).ConfigureAwait(false);

        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WriteStats(writer, presence);
        _registry.EnqueueAbout(player, writer.ToArray());
    }
}

/// <summary>
/// Picks up pp changes the game didn't cause (lazer scores, and the processor finishing after a
/// stable submission stopped waiting) for players online in a stable mode, and queues the scores
/// the processor finished for the website's search index. The index is checked every
/// <see cref="IndexInterval"/> (a lazer score shows on leaderboards only once it's re-indexed);
/// the online players' pp every <see cref="PlayerInterval"/>.
/// </summary>
public sealed class OsuWebPerformanceWatcher : BackgroundService
{
    public static readonly TimeSpan IndexInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan PlayerInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _registry;
    private readonly ILogger<OsuWebPerformanceWatcher> _logger;

    public OsuWebPerformanceWatcher(IServiceScopeFactory scopes, PlayerRegistry registry, ILogger<OsuWebPerformanceWatcher> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(IndexInterval);
        var nextPlayers = DateTimeOffset.UtcNow + PlayerInterval;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<OsuWebPerformanceSync>();
                if (!sync.Enabled)
                    continue;

                await ReindexProcessedScoresAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow < nextPlayers)
                    continue;
                nextPlayers = DateTimeOffset.UtcNow + PlayerInterval;
                foreach (var player in _registry.All)
                {
                    var mode = player.Presence.GameMode;
                    if (player.UserId <= 1 || !OsuWebScores.IsMirrored(mode))
                        continue;

                    var before = player.Presence.Performance;
                    var beforeAccuracy = player.Presence.Accuracy;
                    if (await sync.ApplyAsync(player.UserId, mode, recompute: false, stoppingToken).ConfigureAwait(false) is { } now
                        && ((short)Math.Clamp((int)now.Pp, short.MinValue, short.MaxValue) != before || Math.Abs(now.Accuracy - beforeAccuracy) > 0.00001f))
                    {
                        await sync.RefreshPresenceAsync(player.UserId, mode, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Syncing pp from the website failed; retrying next round.");
            }
        }
    }

    // Where the last round got to in score_process_history, and the scores already queued at that second.
    private DateTime? _processedCursor;
    private HashSet<ulong> _queuedAtCursor = [];

    /// <summary>
    /// Queues scores the processor finished since last round for the website's search index: it
    /// doesn't re-index the scores it gives pp, and profiles and leaderboards read the index.
    /// </summary>
    private async Task ReindexProcessedScoresAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var (ids, cursor) = await services.GetRequiredService<OsuWebScoreMirror>()
            .GetProcessedSinceAsync(_processedCursor, cancellationToken).ConfigureAwait(false);
        var fresh = ids.Where(id => !_queuedAtCursor.Contains(id)).ToList();
        if (fresh.Count > 0)
            await services.GetRequiredService<OsuWebScoreIndexing>().QueueAsync(fresh).ConfigureAwait(false);

        _queuedAtCursor = cursor == _processedCursor ? [.. _queuedAtCursor, .. fresh] : [.. ids];
        _processedCursor = cursor;
    }
}
