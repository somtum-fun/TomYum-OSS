using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A difficulty found by its file name.</summary>
public sealed class FileMap
{
    public string Filename { get; set; } = string.Empty;

    public uint BeatmapId { get; set; }

    public uint BeatmapsetId { get; set; }

    public string Checksum { get; set; } = string.Empty;

    /// <summary>osu!'s status (-2 graveyard … 4 loved).</summary>
    public int Approved { get; set; }
}

/// <summary>
/// What osu!stable's song select asks the website for: maps by file name (their status, and
/// whether the player's copy is outdated), the player's grades on them, and favourites.
/// </summary>
public sealed class OsuWebStableClient
{
    private readonly OsuWebDbContext _db;

    public OsuWebStableClient(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>The known difficulties among <paramref name="filenames"/> (matched without regard to case).</summary>
    public async Task<IReadOnlyList<FileMap>> FindByFilenamesAsync(IReadOnlyCollection<string> filenames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filenames);
        if (filenames.Count == 0)
            return [];
        var wanted = filenames.Where(f => f.Length is > 0 and <= 150).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return await _db.Beatmaps.AsNoTracking()
            .Where(b => b.Filename != null && wanted.Contains(b.Filename))
            .Select(b => new FileMap
            {
                Filename = b.Filename!,
                BeatmapId = b.BeatmapId,
                BeatmapsetId = b.BeatmapsetId ?? 0,
                Checksum = b.Checksum ?? string.Empty,
                Approved = b.Approved,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The player's best grade per difficulty and ruleset (passes only; empty for none).</summary>
    public async Task<IReadOnlyDictionary<(uint BeatmapId, int Ruleset), string>> BestGradesAsync(int userId, IReadOnlyCollection<uint> beatmapIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapIds);
        if (beatmapIds.Count == 0)
            return new Dictionary<(uint, int), string>();
        var ids = beatmapIds.ToList();
        var rows = await _db.Scores.AsNoTracking()
            .Where(s => s.UserId == (uint)userId && ids.Contains(s.BeatmapId) && s.Passed && s.Preserve)
            .Select(s => new { s.BeatmapId, s.RulesetId, s.TotalScore, s.Rank })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => (r.BeatmapId, (int)r.RulesetId))
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.TotalScore)!.Rank);
    }

    /// <summary>Adds a set to the player's favourites (the website's count too); false when it already was one.</summary>
    public async Task<bool> AddFavouriteAsync(int userId, int beatmapsetId, CancellationToken cancellationToken = default)
    {
        var added = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT IGNORE INTO osu_favouritemaps (user_id, beatmapset_id, dateadded) SELECT {userId}, beatmapset_id, UTC_TIMESTAMP() FROM osu_beatmapsets WHERE beatmapset_id = {beatmapsetId}",
            cancellationToken).ConfigureAwait(false) > 0;
        if (added)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE osu_beatmapsets SET favourite_count = favourite_count + 1 WHERE beatmapset_id = {beatmapsetId}", cancellationToken).ConfigureAwait(false);
        }
        return added;
    }

    /// <summary>Fills in file names the website doesn't have yet, from the maps' metadata (once, at start).</summary>
    public Task<int> FillMissingFilenamesAsync(CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE osu_beatmaps b JOIN osu_beatmapsets s ON s.beatmapset_id = b.beatmapset_id
            SET b.filename = LEFT(REGEXP_REPLACE(CONCAT(s.artist, ' - ', s.title, ' (', s.creator, ') [', b.version, '].osu'), '[\\\\/:*?"<>|]', ''), 150)
            WHERE b.filename IS NULL OR b.filename = ''
            """, cancellationToken);
}
