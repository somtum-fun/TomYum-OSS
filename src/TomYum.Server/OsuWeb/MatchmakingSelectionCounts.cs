using Microsoft.EntityFrameworkCore;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// How many times each Ranked Play / matchmaking pool map was played (the website's
/// <c>matchmaking_pool_beatmaps.selection_count</c>, shown in lazer as "12x" next to the map). ppy's
/// spectator server reads it but never counts it (osu! does that elsewhere), so without this it
/// stays 0. Counted from the maps played in the pool's matches: a match's pool is the one its
/// results were rated in; a match that ended without results counts for every pool with the map.
/// </summary>
public sealed class MatchmakingSelectionCounts : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatchmakingSelectionCounts> _logger;

    public MatchmakingSelectionCounts(IServiceScopeFactory scopes, ILogger<MatchmakingSelectionCounts> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Counts every pool map's plays again.</summary>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE matchmaking_pool_beatmaps p
            SET p.selection_count = (
                SELECT COUNT(*)
                FROM multiplayer_playlist_items i
                JOIN multiplayer_rooms r ON r.id = i.room_id
                WHERE i.beatmap_id = p.beatmap_id
                  AND i.played_at IS NOT NULL
                  AND r.type IN ('matchmaking', 'ranked_play')
                  AND (EXISTS (SELECT 1 FROM matchmaking_user_elo_history h WHERE h.room_id = r.id AND h.pool_id = p.pool_id)
                       OR NOT EXISTS (SELECT 1 FROM matchmaking_user_elo_history h WHERE h.room_id = r.id))
            )
            """, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await UpdateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Counting Ranked Play pool plays failed; trying again in {Minutes} minutes.", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
