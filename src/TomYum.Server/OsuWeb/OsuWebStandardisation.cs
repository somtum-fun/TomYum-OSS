using Microsoft.EntityFrameworkCore;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Stable scores in lazer's scoring for their website copies, the way ppy stores stable scores:
/// lazer shows <c>total_score</c> in standardised mode and sorts its leaderboards by it.
/// </summary>
public static class OsuWebStandardisation
{
    /// <summary>The website form of <paramref name="score"/>; null when it can't be worked out (the map's file is unavailable).</summary>
    public static async Task<OsuWebStandardisedScore?> ConvertAsync(LegacyScoreConverter converter, Score score, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(score);

        try
        {
            var converted = await converter.ConvertAsync(
                new PerformanceRequest(
                    BeatmapId: score.BeatmapId,
                    RulesetId: GameMode.AsVanilla(score.Mode),
                    Mods: score.Mods,
                    Count300: score.Count300,
                    Count100: score.Count100,
                    Count50: score.Count50,
                    CountMiss: score.CountMiss,
                    CountGeki: score.CountGeki,
                    CountKatu: score.CountKatu,
                    MaxCombo: score.MaxCombo,
                    Passed: score.Passed),
                score.ScoreValue,
                cancellationToken).ConfigureAwait(false);
            return converted is null ? null : new OsuWebStandardisedScore(converted.TotalScore, converted.DataJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The website copy then keeps the stable score in both columns, as before.
            logger.LogWarning(ex, "Couldn't convert score {ScoreId} to lazer's scoring.", score.Id);
            return null;
        }
    }
}

/// <summary>
/// Once at startup: converts TomYum's stable scores already on the website to lazer's scoring
/// (standardised <c>total_score</c>, ppy-style data, TomYum's id as legacy score id) and
/// re-indexes them, so lazer shows the right standardised score and recognises downloaded replays.
/// </summary>
public sealed class OsuWebScoreConversionBackfill : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OsuWebScoreConversionBackfill> _logger;

    public OsuWebScoreConversionBackfill(IServiceScopeFactory scopes, ILogger<OsuWebScoreConversionBackfill> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var mirror = services.GetRequiredService<OsuWebScoreMirror>();
            await mirror.EnsureLinkTableAsync(stoppingToken).ConfigureAwait(false);
            var pending = await mirror.GetUnconvertedScoresAsync(100_000, stoppingToken).ConfigureAwait(false);
            if (pending.Count == 0)
                return;

            var db = services.GetRequiredService<TomYumDbContext>();
            var converter = services.GetRequiredService<LegacyScoreConverter>();
            var converted = new List<ulong>();
            foreach (var chunk in pending.Chunk(200))
            {
                var ids = chunk.Select(p => p.ScoreId).ToList();
                var scores = await db.Scores.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, stoppingToken).ConfigureAwait(false);
                foreach (var item in chunk)
                {
                    if (!scores.TryGetValue(item.ScoreId, out var score)
                        || await OsuWebStandardisation.ConvertAsync(converter, score, _logger, stoppingToken).ConfigureAwait(false) is not { } standardised)
                        continue;
                    await mirror.ConvertScoreAsync(item.ScoreId, item.OsuWebScoreId, standardised, stoppingToken).ConfigureAwait(false);
                    converted.Add(item.OsuWebScoreId);
                }
            }

            if (services.GetService<OsuWebScoreIndexing>() is { } indexing)
                await indexing.QueueAsync(converted).ConfigureAwait(false);
            _logger.LogInformation("Converted {Count} of {Pending} stable score(s) on the website to lazer's scoring.", converted.Count, pending.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Converting stable scores on the website to lazer's scoring failed; it's retried on the next start.");
        }
    }
}
