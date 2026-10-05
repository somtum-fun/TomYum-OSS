using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// In the background at start: uploaded maps' BPM on the website as osu! gives it (<see cref="OsuFileText.Bpm"/>, the one a map is at
/// the longest). Uploads used to take the first timing point's, which is wrong for maps with an intro at another tempo.
/// </summary>
public sealed class CustomBpmBackfill : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OsuFileCache? _files;
    private readonly ILogger<CustomBpmBackfill> _logger;

    public CustomBpmBackfill(IServiceScopeFactory scopes, ILogger<CustomBpmBackfill> logger, OsuFileCache? files = null)
    {
        _scopes = scopes;
        _logger = logger;
        _files = files;
    }

    /// <summary>Corrects the website's BPM of uploaded maps; returns how many changed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_files is null)
            return 0;

        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<TomYumDbContext>() is not { } db || scope.ServiceProvider.GetService<OsuWebDbContext>() is not { } web)
            return 0;

        var maps = await db.Beatmaps.AsNoTracking()
            .Where(b => b.SetId >= Beatmap.CustomIdFloor)
            .Select(b => new { b.Id, b.SetId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var changed = 0;
        var setBpms = new Dictionary<int, double>();
        foreach (var map in maps.OrderBy(m => m.Id))
        {
            var path = Path.Combine(_files.CacheDirectory, map.Id.ToString(CultureInfo.InvariantCulture) + ".osu");
            if (!File.Exists(path))
                continue;
            if (OsuFileText.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)).Bpm is not { } bpm)
                continue;

            setBpms.TryAdd(map.SetId, bpm);
            changed += await web.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE osu_beatmaps SET bpm = {(float)bpm} WHERE beatmap_id = {map.Id} AND ABS(bpm - {(float)bpm}) > 0.01", cancellationToken).ConfigureAwait(false);
        }

        // The set's is its first difficulty's, as when it was uploaded.
        foreach (var (setId, bpm) in setBpms)
        {
            await web.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE osu_beatmapsets SET bpm = {(float)bpm} WHERE beatmapset_id = {setId} AND ABS(bpm - {(float)bpm}) > 0.01", cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
            _logger.LogInformation("Custom BPM: corrected {Count} uploaded maps' BPM.", changed);
        return changed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Custom BPM: the backfill failed.");
        }
    }
}
