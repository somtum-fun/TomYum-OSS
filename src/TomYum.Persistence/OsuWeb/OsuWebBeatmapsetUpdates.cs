using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Tells lazer a beatmap set changed: a row in osu-web's <c>bss_process_queue</c>, as osu-web adds when it
/// qualifies, ranks or loves a set. The spectator server announces new rows to every lazer client, which
/// looks the set up again (its new status). TomYum's own status changes (osu! sets imported or refreshed,
/// staff ranking, uploads, the graveyard) write the tables directly and so add the row themselves.
/// </summary>
public static class OsuWebBeatmapsetUpdates
{
    public static async Task QueueAsync(OsuWebDbContext db, IEnumerable<int> setIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(setIds);

        foreach (var setId in setIds.Distinct())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO bss_process_queue (beatmapset_id) VALUES ({setId})", cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task QueueAsync(OsuWebDbContext db, int setId, CancellationToken cancellationToken = default) =>
        QueueAsync(db, [setId], cancellationToken);
}
