using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// The website's side of custom (uploaded) beatmap sets beyond what the beatmap mirror writes: who
/// owns the set, whether it has a cover (served by TomYum), its unicode names, and which of its
/// difficulties still exist after an update.
/// </summary>
public sealed class OsuWebCustomSets
{
    private readonly OsuWebDbContext _db;

    public OsuWebCustomSets(OsuWebDbContext db) => _db = db;

    /// <summary>The set's owner on the website, or null when the set isn't there.</summary>
    public async Task<int?> OwnerAsync(int setId, CancellationToken cancellationToken = default)
    {
        var owner = await _db.Database
            .SqlQuery<uint>($"SELECT user_id AS Value FROM osu_beatmapsets WHERE beatmapset_id = {setId}")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return owner.Count == 0 ? null : (int)owner[0];
    }

    /// <summary>
    /// Finishes an upload the mirror has written: <paramref name="ownerId"/> owns the set and its
    /// difficulties, the cover is marked as updated (or absent), and difficulties of the set not in
    /// <paramref name="beatmapIds"/> (dropped by an update) are deleted.
    /// </summary>
    public async Task FinishUploadAsync(
        int setId, int ownerId, IReadOnlyCollection<int> beatmapIds, bool hasCover, long fileSize,
        string? artistUnicode, string? titleUnicode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapIds);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE osu_beatmapsets
            SET user_id = {ownerId}, cover_updated_at = {(hasCover ? DateTime.UtcNow : null)}, filesize = {fileSize},
                artist_unicode = {Truncate(artistUnicode)}, title_unicode = {Truncate(titleUnicode)},
                active = 1, deleted_at = NULL, discussion_enabled = 1
            WHERE beatmapset_id = {setId}
            """,
            cancellationToken).ConfigureAwait(false);

        // Only numbers are spliced in.
        var ids = string.Join(',', beatmapIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var set = setId.ToString(CultureInfo.InvariantCulture);
        var owner = ownerId.ToString(CultureInfo.InvariantCulture);
#pragma warning disable EF1002 // see above
        await _db.Database.ExecuteSqlRawAsync(
            $"UPDATE osu_beatmaps SET user_id = {owner}, deleted_at = NULL WHERE beatmapset_id = {set} AND beatmap_id IN ({ids})",
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            $"UPDATE osu_beatmaps SET deleted_at = NOW() WHERE beatmapset_id = {set} AND deleted_at IS NULL AND beatmap_id NOT IN ({ids})",
            cancellationToken).ConfigureAwait(false);
        // An unranked set shows what its remaining difficulties are (a work in progress or pending),
        // now that the dropped ones don't count.
        await _db.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE osu_beatmapsets SET approved = (
                SELECT MAX(b.approved) FROM osu_beatmaps b WHERE b.beatmapset_id = {set} AND b.deleted_at IS NULL)
            WHERE beatmapset_id = {set} AND approved IN (-1, 0)
              AND EXISTS (SELECT 1 FROM osu_beatmaps b WHERE b.beatmapset_id = {set} AND b.deleted_at IS NULL)
            """,
            cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Sets an unranked set's status to a work in progress (-1) or pending (0), as its mapper chose;
    /// ranked, qualified and loved sets are left alone.
    /// </summary>
    public async Task SetUnrankedStatusAsync(int setId, int approved, CancellationToken cancellationToken = default)
    {
        if (approved is not (-1 or 0))
            throw new ArgumentOutOfRangeException(nameof(approved), "Only work in progress (-1) or pending (0).");
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE osu_beatmapsets SET approved = {approved}, last_update = UTC_TIMESTAMP() WHERE beatmapset_id = {setId} AND approved IN (-1, 0)",
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE osu_beatmaps SET approved = {approved} WHERE beatmapset_id = {setId} AND deleted_at IS NULL AND approved IN (-1, 0)",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The set's forum thread (its description), 0 when it has none.</summary>
    public async Task<int> ThreadIdAsync(int setId, CancellationToken cancellationToken = default)
    {
        var thread = await _db.Database
            .SqlQuery<uint>($"SELECT thread_id AS Value FROM osu_beatmapsets WHERE beatmapset_id = {setId}")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return thread.Count == 0 ? 0 : (int)thread[0];
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : OsuWebBeatmaps.Truncate(value);
}
