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

    /// <summary>The set's average rating (0 without votes), and whether <paramref name="userId"/> already voted.</summary>
    public async Task<(bool Voted, double Average)> RatingAsync(int userId, int beatmapsetId, CancellationToken cancellationToken = default)
    {
        var voted = await _db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM osu_user_beatmapset_ratings WHERE user_id = {userId} AND beatmapset_id = {beatmapsetId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false) > 0;
        return (voted, await AverageRatingAsync(beatmapsetId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The set's average rating, 0 without votes (what the website shows).</summary>
    public async Task<double> AverageRatingAsync(int beatmapsetId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<double>(
            $"SELECT CAST(COALESCE(AVG(rating), 0) AS DOUBLE) AS Value FROM osu_user_beatmapset_ratings WHERE beatmapset_id = {beatmapsetId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The player's 1-10 rating after playing (one per player and set, like osu!), kept where the
    /// website keeps ratings; the set's average is updated. Returns the new average.
    /// </summary>
    public async Task<double> RateAsync(int userId, int beatmapsetId, int rating, CancellationToken cancellationToken = default)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT IGNORE INTO osu_user_beatmapset_ratings (user_id, beatmapset_id, rating, date) VALUES ({userId}, {beatmapsetId}, {Math.Clamp(rating, 1, 10)}, UTC_TIMESTAMP())",
            cancellationToken).ConfigureAwait(false);
        var average = await AverageRatingAsync(beatmapsetId, cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE osu_beatmapsets SET rating = {average} WHERE beatmapset_id = {beatmapsetId}", cancellationToken).ConfigureAwait(false);
        return average;
    }

    /// <summary>
    /// The seasonal backgrounds shown now (the admin panel's: a contest's entries marked for the client,
    /// until the season's end), as their paths under the website's uploads; empty out of season.
    /// </summary>
    public async Task<IReadOnlyList<string>> SeasonalBackgroundPathsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var settings = await _db.Database.SqlQuery<SeasonalCount>(
                $"SELECT name AS Name, CAST(count AS SIGNED) AS Value FROM osu_counts WHERE name IN ('tomyum_seasonal_contest', 'tomyum_seasonal_ends_at')")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var contest = settings.FirstOrDefault(c => c.Name == "tomyum_seasonal_contest")?.Value;
        var endsAt = settings.FirstOrDefault(c => c.Name == "tomyum_seasonal_ends_at")?.Value;
        if (contest is null || endsAt is null || endsAt <= now.ToUnixTimeSeconds())
            return [];

        var entries = await _db.Database.SqlQuery<SeasonalEntry>(
                $"SELECT CAST(id AS SIGNED) AS Id, hash AS Hash FROM user_contest_entries WHERE contest_id = {contest} AND show_in_client = 1 AND deleted_at IS NULL AND hash IS NOT NULL ORDER BY id")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // The clients' copy sits next to the upload: its name without the extension, plus "_opt.jpg".
        return [.. entries.Select(e => $"user-contest-entries/{e.Id}/{Path.GetFileNameWithoutExtension(e.Hash)}_opt.jpg")];
    }

    private sealed record SeasonalCount(string Name, long Value);

    private sealed record SeasonalEntry(long Id, string Hash);

    /// <summary>The sets the player favourited, newest first (song select's favourites filter).</summary>
    public async Task<IReadOnlyList<int>> FavouriteSetIdsAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<int>(
            $"SELECT CAST(beatmapset_id AS SIGNED) AS Value FROM osu_favouritemaps WHERE user_id = {userId} ORDER BY dateadded DESC")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Fills in file names the website doesn't have yet, from the maps' metadata (once, at start).</summary>
    public Task<int> FillMissingFilenamesAsync(CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE osu_beatmaps b JOIN osu_beatmapsets s ON s.beatmapset_id = b.beatmapset_id
            SET b.filename = LEFT(REGEXP_REPLACE(CONCAT(s.artist, ' - ', s.title, ' (', s.creator, ') [', b.version, '].osu'), '[\\\\/:*?"<>|]', ''), 150)
            WHERE b.filename IS NULL OR b.filename = ''
            """, cancellationToken);
}
