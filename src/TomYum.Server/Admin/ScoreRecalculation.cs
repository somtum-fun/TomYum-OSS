using Microsoft.EntityFrameworkCore;
using TomYum.Bancho;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>What a recalculation did.</summary>
/// <param name="Scores">Passed scores looked at.</param>
/// <param name="Changed">Scores whose pp or best flag changed.</param>
/// <param name="Players">Players whose stats were recomputed.</param>
public sealed record ScoreRecalculationResult(int Scores, int Changed, int Players);

/// <summary>
/// Recalculates the scores on beatmaps whose status staff changed: pp (ranked/approved maps award
/// pp, others none), which score is each player's best, then the players' stats, in-game presence
/// and the website copies (scores, stats, ranks, search, country rankings).
/// </summary>
public sealed class ScoreRecalculation
{
    private readonly TomYumDbContext _db;
    private readonly IPerformanceCalculator _pp;
    private readonly IUserStatsRepository _userStats;
    private readonly PlayerRegistry _registry;
    private readonly ILogger<ScoreRecalculation> _logger;
    private readonly OsuWebScoreMirror? _mirror;
    private readonly OsuWebScoreIndexing? _indexing;
    private readonly OsuWeb.OsuWebInterop? _interop;
    private readonly OsuWeb.OsuWebPerformanceSync? _performance;

    public ScoreRecalculation(
        TomYumDbContext db,
        IPerformanceCalculator pp,
        IUserStatsRepository userStats,
        PlayerRegistry registry,
        ILogger<ScoreRecalculation> logger,
        OsuWebScoreMirror? mirror = null,
        OsuWebScoreIndexing? indexing = null,
        OsuWeb.OsuWebInterop? interop = null,
        OsuWeb.OsuWebPerformanceSync? performance = null)
    {
        _db = db;
        _pp = pp;
        _userStats = userStats;
        _registry = registry;
        _logger = logger;
        _mirror = mirror;
        _indexing = indexing;
        _interop = interop;
        _performance = performance;
    }

    /// <summary>Recalculates every passed score on <paramref name="beatmaps"/> for the status each now has.</summary>
    public async Task<ScoreRecalculationResult> RecalculateAsync(IReadOnlyCollection<Beatmap> beatmaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmaps);

        var looked = 0;
        var changed = new List<Score>();
        var players = new HashSet<(int UserId, int Mode)>();
        foreach (var beatmap in beatmaps)
        {
            var scores = await _db.Scores.Where(s => s.BeatmapMd5 == beatmap.Md5 && s.Passed).ToListAsync(cancellationToken).ConfigureAwait(false);
            looked += scores.Count;

            var before = scores.ToDictionary(s => s.Id, s => (s.Pp, s.Status));
            foreach (var score in scores)
                score.Pp = beatmap.Status.AwardsRankedPp() ? await CalculateAsync(beatmap, score, cancellationToken).ConfigureAwait(false) : 0;

            // Each player's best on the map is the one worth the most pp (score breaks ties, and
            // decides alone on maps without pp), as score submission picks it.
            foreach (var mine in scores.GroupBy(s => (s.UserId, s.Mode)))
            {
                var best = mine.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue).ThenBy(s => s.Id).First();
                foreach (var score in mine)
                    score.Status = score == best ? ScoreStatus.Best : ScoreStatus.Submitted;
            }

            var different = scores.Where(s => Math.Abs(before[s.Id].Pp - s.Pp) > 0.001 || before[s.Id].Status != s.Status).ToList();
            changed.AddRange(different);
            foreach (var score in different)
                players.Add((score.UserId, score.Mode));

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        foreach (var (userId, mode) in players)
            await RecomputePlayerAsync(userId, mode, cancellationToken).ConfigureAwait(false);

        // The website's copies follow the map's (already mirrored) status even when no pp changed.
        var md5s = beatmaps.Select(b => b.Md5).ToList();
        var websiteScores = await _db.Scores.AsNoTracking()
            .Where(s => md5s.Contains(s.BeatmapMd5) && s.Passed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await UpdateWebsiteAsync(websiteScores, players, beatmaps.Select(b => b.Id).ToList(), cancellationToken).ConfigureAwait(false);

        return new ScoreRecalculationResult(looked, changed.Count, players.Count);
    }

    private async Task<double> CalculateAsync(Beatmap beatmap, Score score, CancellationToken cancellationToken)
    {
        try
        {
            return await _pp.CalculateAsync(
                new PerformanceRequest(
                    BeatmapId: beatmap.Id,
                    RulesetId: GameMode.AsVanilla(score.Mode),
                    Mods: score.Mods,
                    Count300: score.Count300,
                    Count100: score.Count100,
                    Count50: score.Count50,
                    CountMiss: score.CountMiss,
                    CountGeki: score.CountGeki,
                    CountKatu: score.CountKatu,
                    MaxCombo: score.MaxCombo,
                    Passed: score.Passed,
                    Lazer: score.SliderEndHits is not null, LargeTickHits: score.LargeTickHits, SliderEndHits: score.SliderEndHits),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep what the score had rather than wiping it over a calculator hiccup.
            _logger.LogWarning(ex, "PP recalculation failed for score {ScoreId} on beatmap {BeatmapId}; keeping {Pp} pp.", score.Id, beatmap.Id, score.Pp);
            return score.Pp;
        }
    }

    /// <summary>The player's stats from their bests, and their in-game panel if they're online in that mode.</summary>
    private async Task RecomputePlayerAsync(int userId, int mode, CancellationToken cancellationToken)
    {
        var stats = await _db.UserStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken).ConfigureAwait(false);
        if (stats is null)
            return;

        await BestScoreStats.ApplyAsync(_db, stats, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(stats).State = EntityState.Detached;

        if (_registry.GetByUserId(userId) is not { } player || player.Presence.GameMode != mode)
            return;

        var presence = player.Presence;
        await new PresenceStats(_userStats, _mirror).FillAsync(presence, userId, mode, stats, cancellationToken).ConfigureAwait(false);

        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WriteStats(writer, presence);
        _registry.EnqueueAbout(player, writer.ToArray());
    }

    private async Task UpdateWebsiteAsync(List<Score> scores, HashSet<(int UserId, int Mode)> players, IReadOnlyCollection<int> beatmapIds, CancellationToken cancellationToken)
    {
        if (_mirror is null)
            return;

        try
        {
            var written = await _mirror.RefreshScoresAsync(scores, cancellationToken).ConfigureAwait(false);
            foreach (var (userId, mode) in players.Where(p => OsuWebScores.IsMirrored(p.Mode)))
                await _mirror.WriteStatsAsync(userId, mode, cancellationToken).ConfigureAwait(false);

            if (_performance is { Enabled: true })
            {
                // Lazer scores on these maps change too. The processor redoes every score on them
                // (its pp for stable scores replaces ours); totals are redone now with what's there,
                // and again by the processor as it goes.
                foreach (var lazer in await _mirror.ClearLazerPpOnUnrankedAsync(beatmapIds, cancellationToken).ConfigureAwait(false))
                    players.Add(lazer);
                await _performance.QueueAsync(await _mirror.GetScoreIdsOnBeatmapsAsync(beatmapIds, cancellationToken).ConfigureAwait(false), reprocess: true, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var (userId, mode) in players.Where(p => OsuWebScores.IsMirrored(p.Mode)))
                {
                    await _performance.ApplyAsync(userId, mode, recompute: true, cancellationToken).ConfigureAwait(false);
                    await _performance.RefreshPresenceAsync(userId, mode, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var mode in players.Select(p => p.Mode).Distinct().Where(OsuWebScores.IsMirrored))
                await _mirror.RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);
            if (_indexing is not null)
                await _indexing.QueueAsync(written).ConfigureAwait(false);

            if (_interop is { IsConfigured: true })
            {
                var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var userId in players.Select(p => p.UserId).Distinct())
                {
                    if (await _mirror.GetCountryAsync(userId, cancellationToken).ConfigureAwait(false) is { Length: > 0 } country)
                        countries.Add(country);
                }
                foreach (var country in countries)
                    await _interop.RecalculateCountryAsync(country, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The game has the new values either way; the website catches up on the next score.
            _logger.LogWarning(ex, "Recalculated {Count} score(s) but updating osu-web failed.", scores.Count);
        }
    }
}
