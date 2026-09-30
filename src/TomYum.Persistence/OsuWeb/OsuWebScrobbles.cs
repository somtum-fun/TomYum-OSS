using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A play as the website stored it (stable's and lazer's alike), with its song, for Last.fm.</summary>
public sealed class ScrobblePlay
{
    public ulong ScoreId { get; set; }

    public uint UserId { get; set; }

    public bool Passed { get; set; }

    public string Artist { get; set; } = string.Empty;

    public string ArtistUnicode { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string TitleUnicode { get; set; } = string.Empty;

    /// <summary>The map's length in seconds (at normal speed).</summary>
    public long Length { get; set; }

    /// <summary>The score's JSON (its mods, for the speed).</summary>
    public string Data { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    public DateTime EndedAt { get; set; }
}

/// <summary>A beatmap's song, for "now playing" on Last.fm.</summary>
public sealed class ScrobbleSong
{
    public string Artist { get; set; } = string.Empty;

    public string ArtistUnicode { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string TitleUnicode { get; set; } = string.Empty;

    public long Length { get; set; }
}

/// <summary>The website's plays and songs, for scrobbling to Last.fm.</summary>
public sealed class OsuWebScrobbles
{
    /// <summary>The <c>osu_counts</c> row holding the last website score looked at.</summary>
    public const string MarkName = "tomyum_lastfm_mark";

    private readonly OsuWebDbContext _db;

    public OsuWebScrobbles(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>The scores after <paramref name="afterId"/> (stable and lazer), oldest first.</summary>
    public async Task<IReadOnlyList<ScrobblePlay>> AfterAsync(ulong afterId, int limit, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQueryRaw<ScrobblePlay>(
            """
            SELECT s.id AS ScoreId, s.user_id AS UserId, s.passed AS Passed,
              bs.artist AS Artist, COALESCE(bs.artist_unicode, '') AS ArtistUnicode,
              bs.title AS Title, COALESCE(bs.title_unicode, '') AS TitleUnicode,
              CAST(b.total_length AS SIGNED) AS Length, CAST(s.data AS CHAR) AS Data,
              s.started_at AS StartedAt, s.ended_at AS EndedAt
            FROM scores s
            JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
            JOIN osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id
            WHERE s.id > {0}
            ORDER BY s.id LIMIT {1}
            """, afterId, limit).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The song of beatmap <paramref name="beatmapId"/>, or null when the website doesn't have it.</summary>
    public async Task<ScrobbleSong?> SongAsync(int beatmapId, CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQueryRaw<ScrobbleSong>(
            """
            SELECT bs.artist AS Artist, COALESCE(bs.artist_unicode, '') AS ArtistUnicode,
              bs.title AS Title, COALESCE(bs.title_unicode, '') AS TitleUnicode, CAST(b.total_length AS SIGNED) AS Length
            FROM osu_beatmaps b
            JOIN osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id
            WHERE b.beatmap_id = {0}
            """, beatmapId).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task<ulong?> MarkAsync(CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQuery<ulong>($"SELECT count AS Value FROM osu_counts WHERE name = {MarkName}")
            .ToListAsync(cancellationToken).ConfigureAwait(false)) is [var mark] ? mark : null;

    public Task SetMarkAsync(ulong scoreId, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({MarkName}, {scoreId}) ON DUPLICATE KEY UPDATE count = {scoreId}", cancellationToken);

    /// <summary>The newest website score (where following starts the first time).</summary>
    public async Task<ulong> NewestScoreIdAsync(CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking().MaxAsync(s => (ulong?)s.Id, cancellationToken).ConfigureAwait(false) ?? 0;
}
