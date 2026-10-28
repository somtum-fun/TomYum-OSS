using Microsoft.EntityFrameworkCore;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// In the background, every few minutes: osu! maps' star ratings converted to taiko, catch and mania, on the website
/// (<c>osu_beatmap_difficulty</c>, which its beatmap pages read for a convert and showed 0★ without). Only the ones
/// it doesn't have yet, so new imports and uploads get theirs on the next run; a map whose .osu can't be had is tried
/// again after a restart.
/// </summary>
public sealed class ConvertStarRatingsJob : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private const int Batch = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ConvertStarRatingsJob> _logger;
    // Maps whose difficulty couldn't be worked out (no .osu), not asked for again until a restart.
    private readonly HashSet<int> _failed = [];

    public ConvertStarRatingsJob(IServiceScopeFactory scopes, ILogger<ConvertStarRatingsJob> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Stores the converted star ratings osu! maps are missing (up to a batch of maps); returns how many maps got some.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<OsuWebDbContext>() is not { } web
            || scope.ServiceProvider.GetService<IPerformanceCalculator>() is not IAccuracyPerformance calculator)
            return 0;

        var missing = await web.Database.SqlQuery<int>($"""
            SELECT b.beatmap_id AS Value FROM osu_beatmaps b
            WHERE b.playmode = 0 AND b.deleted_at IS NULL
              AND (SELECT COUNT(*) FROM osu_beatmap_difficulty d WHERE d.beatmap_id = b.beatmap_id AND d.mods = 0 AND d.mode IN (1, 2, 3)) < 3
            ORDER BY b.beatmap_id
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        var stored = 0;
        foreach (var beatmapId in missing.Where(id => !_failed.Contains(id)).Take(Batch))
        {
            var any = false;
            for (var mode = 1; mode <= 3; mode++)
            {
                double? stars;
                try
                {
                    stars = await calculator.StarRatingAsync(beatmapId, mode, 0, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Convert star ratings: map {BeatmapId} in ruleset {Mode} failed.", beatmapId, mode);
                    stars = null;
                }
                if (stars is not { } value)
                    continue;

                await web.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO osu_beatmap_difficulty (beatmap_id, mode, mods, diff_unified) VALUES ({beatmapId}, {mode}, 0, {(float)value})
                    ON DUPLICATE KEY UPDATE diff_unified = VALUES(diff_unified)
                    """, cancellationToken).ConfigureAwait(false);
                any = true;
            }
            if (any)
                stored++;
            else
                _failed.Add(beatmapId);
        }

        if (stored > 0)
            _logger.LogInformation("Convert star ratings: stored taiko/catch/mania star ratings for {Count} osu! maps.", stored);
        return stored;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch: there may be more, go on straight away.
                while (await RunAsync(stoppingToken).ConfigureAwait(false) >= Batch)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Convert star ratings: the run failed.");
            }

            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
