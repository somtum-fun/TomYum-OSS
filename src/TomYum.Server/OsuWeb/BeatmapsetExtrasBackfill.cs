using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Once, in the background: gives every osu! set already on the website what imports now bring
/// from the osu! API v2 (featured artist track, description, nominators, who made it and each
/// difficulty) and re-indexes it, so older
/// imports show up under "Featured Artists" too. Needs the API v2 (<c>Osu:ClientId</c>/<c>ClientSecret</c>)
/// and InterOp; done when <c>osu_counts</c> has <see cref="DoneName"/>.
/// </summary>
public sealed class BeatmapsetExtrasBackfill : BackgroundService
{
    // v3: mappers (the set's creator and each difficulty's) too.
    public const string DoneName = "tomyum_beatmapset_extras_v3";

    /// <summary>The last set brought up to date (osu_counts), so a restart carries on instead of starting over.</summary>
    public const string CursorName = "tomyum_beatmapset_extras_v3_cursor";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BeatmapsetExtrasBackfill> _logger;

    public BeatmapsetExtrasBackfill(IServiceScopeFactory scopes, ILogger<BeatmapsetExtrasBackfill> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Runs the backfill unless done; returns how many sets were brought up to date (null: skipped).</summary>
    public async Task<int?> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<OsuWebDbContext>();
        var api = services.GetRequiredService<OsuApiV2Client>();
        var interop = services.GetRequiredService<OsuWebInterop>();
        if (!api.IsConfigured || !interop.IsConfigured)
            return null;
        if (await db.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM osu_counts WHERE name = {DoneName}")
                .SingleAsync(cancellationToken).ConfigureAwait(false) > 0)
            return null;

        // osu!'s sets only: custom sets (TomYum's own id range) aren't on the API. In id order, from
        // where the last run stopped (after an import of thousands of sets this takes hours).
        var floor = (uint)Beatmap.CustomIdFloor;
        var cursor = (await db.Database.SqlQuery<long>($"SELECT CAST(count AS SIGNED) AS Value FROM osu_counts WHERE name = {CursorName}")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var sets = await db.Beatmapsets.AsNoTracking().Where(s => s.BeatmapsetId < floor && s.BeatmapsetId > cursor)
            .OrderBy(s => s.BeatmapsetId).Select(s => (int)s.BeatmapsetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (cursor > 0 && sets.Count > 0)
            _logger.LogInformation("Bringing beatmap sets up to date: carrying on after set {Cursor} ({Count} to go).", cursor, sets.Count);
        var updated = 0;
        var done = 0;
        // Every set: the mirror would count that as crawling, so osu! (paced by the shared budget), as
        // background work: players' lookups go first.
        using var _ = TomYum.Server.Beatmaps.BeatmapMirror.Bypass();
        using var __ = OsuApiBudget.Background();
        foreach (var setId in sets)
        {
            if (await api.GetBeatmapsetExtrasAsync(setId, cancellationToken).ConfigureAwait(false) is { } extras)
            {
                try
                {
                    await interop.SyncBeatmapsetExtrasAsync(setId, extras, cancellationToken).ConfigureAwait(false);
                    await interop.IndexBeatmapsetAsync(setId, cancellationToken).ConfigureAwait(false);
                    updated++;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Bringing beatmap set {SetId} up to date on the website failed.", setId);
                }
            }
            if (++done % 25 == 0)
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO osu_counts (name, count) VALUES ({CursorName}, {setId}) ON DUPLICATE KEY UPDATE count = VALUES(count)", cancellationToken).ConfigureAwait(false);
            }
            // Gentle on osu!'s API.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({DoneName}, 1) ON DUPLICATE KEY UPDATE count = 1", cancellationToken).ConfigureAwait(false);
        return updated;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (and the website) settle first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        try
        {
            if (await RunAsync(stoppingToken).ConfigureAwait(false) is { } updated)
                _logger.LogInformation("Brought {Count} beatmap set(s) on the website up to date (featured artists, descriptions, nominators, mappers).", updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Bringing beatmap sets up to date failed; it runs again at the next start.");
        }
    }
}
