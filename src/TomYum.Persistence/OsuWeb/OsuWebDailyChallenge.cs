using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>The daily challenge that's open now (a lazer playlist room).</summary>
public sealed class DailyChallengeRoom
{
    public ulong RoomId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime? StartsAt { get; set; }

    public DateTime? EndsAt { get; set; }

    public uint BeatmapId { get; set; }

    public ushort RulesetId { get; set; }

    public string? RequiredMods { get; set; }

    public string? AllowedMods { get; set; }
}

/// <summary>A player's best play in a daily challenge.</summary>
public sealed class DailyChallengeResult
{
    public uint UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    public uint TotalScore { get; set; }

    public float Accuracy { get; set; }
}

/// <summary>
/// The website's daily challenge queue (<c>daily_challenge_queue</c>). osu-web's scheduler opens a
/// playlist room from the first unused entry every day at 00:05 UTC
/// (<c>daily-challenge:create-next</c>) and works out the streaks and percentiles after; this only
/// keeps the queue filled and reads what's open.
/// </summary>
public sealed class OsuWebDailyChallenge
{
    private readonly OsuWebDbContext _db;

    public OsuWebDailyChallenge(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>Entries waiting for their day.</summary>
    public async Task<int> WaitingAsync(CancellationToken cancellationToken = default) =>
        (int)await _db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM daily_challenge_queue WHERE multiplayer_room_id IS NULL")
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A random ranked map for the challenge: made for <paramref name="ruleset"/> (no converts), in the
    /// star range, 1 to 5 minutes long, and from a set that has never been a daily challenge.
    /// </summary>
    public async Task<int?> PickMapAsync(int ruleset, double minStars, double maxStars, CancellationToken cancellationToken = default)
    {
        var ids = await _db.Database.SqlQueryRaw<uint>(
            """
            SELECT b.beatmap_id AS Value FROM osu_beatmaps b
            JOIN osu_beatmapsets s ON s.beatmapset_id = b.beatmapset_id
            WHERE b.approved IN (1, 2) AND b.deleted_at IS NULL AND s.deleted_at IS NULL AND b.playmode = {0}
              AND b.difficultyrating BETWEEN {1} AND {2} AND b.total_length BETWEEN 60 AND 300
              AND b.beatmapset_id NOT IN (
                SELECT u.beatmapset_id FROM daily_challenge_queue q JOIN osu_beatmaps u ON u.beatmap_id = q.beatmap_id)
            ORDER BY RAND() LIMIT 1
            """, ruleset, minStars, maxStars).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 1 ? (int)ids[0] : null;
    }

    /// <summary>Adds a challenge at the end of the queue.</summary>
    public async Task EnqueueAsync(int beatmapId, int ruleset, IReadOnlyList<string> allowedMods, IReadOnlyList<string> requiredMods, string? description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedMods);
        ArgumentNullException.ThrowIfNull(requiredMods);
        var allowed = ModsJson(allowedMods);
        var required = ModsJson(requiredMods);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO daily_challenge_queue (beatmap_id, ruleset_id, allowed_mods, required_mods, `order`, description)
            SELECT {beatmapId}, {ruleset}, {allowed}, {required}, COALESCE(MAX(`order`), 0) + 1, {description} FROM daily_challenge_queue
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The daily challenge that's open now, or null.</summary>
    public async Task<DailyChallengeRoom?> CurrentAsync(CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQueryRaw<DailyChallengeRoom>(
            """
            SELECT r.id AS RoomId, r.name AS Name, r.starts_at AS StartsAt, r.ends_at AS EndsAt,
                   p.beatmap_id AS BeatmapId, p.ruleset_id AS RulesetId,
                   CAST(p.required_mods AS CHAR) AS RequiredMods, CAST(p.allowed_mods AS CHAR) AS AllowedMods
            FROM multiplayer_rooms r JOIN multiplayer_playlist_items p ON p.room_id = r.id
            WHERE r.category = 'daily_challenge' AND r.deleted_at IS NULL
              AND (r.starts_at IS NULL OR r.starts_at <= UTC_TIMESTAMP()) AND r.ends_at > UTC_TIMESTAMP()
            ORDER BY r.id DESC LIMIT 1
            """).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>The room's best play per player (passes only), best first.</summary>
    public async Task<IReadOnlyList<DailyChallengeResult>> ResultsAsync(ulong roomId, int limit, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQueryRaw<DailyChallengeResult>(
            """
            SELECT t.user_id AS UserId, u.username AS Username, t.total_score AS TotalScore, t.accuracy AS Accuracy
            FROM (
              SELECT s.user_id, s.total_score, s.accuracy,
                     ROW_NUMBER() OVER (PARTITION BY s.user_id ORDER BY s.total_score DESC, s.id) AS n
              FROM multiplayer_score_links l
              JOIN multiplayer_playlist_items p ON p.id = l.playlist_item_id
              JOIN scores s ON s.id = l.score_id
              WHERE p.room_id = {0} AND s.passed = 1
            ) t JOIN phpbb_users u ON u.user_id = t.user_id
            WHERE t.n = 1 AND u.user_warnings = 0 AND u.user_type = 0
            ORDER BY t.total_score DESC LIMIT {1}
            """, roomId, limit).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>How many different players set a score in the room.</summary>
    public async Task<int> PlayersAsync(ulong roomId, CancellationToken cancellationToken = default) =>
        (int)await _db.Database.SqlQueryRaw<long>(
                """
                SELECT COUNT(DISTINCT l.user_id) AS Value FROM multiplayer_score_links l
                JOIN multiplayer_playlist_items p ON p.id = l.playlist_item_id WHERE p.room_id = {0}
                """, roomId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Mod acronyms from a playlist item's mods JSON.</summary>
    public static IReadOnlyList<string> Acronyms(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : document.RootElement.EnumerateArray()
                    .Select(m => m.TryGetProperty("acronym", out var acronym) ? acronym.GetString() : null)
                    .OfType<string>().ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ModsJson(IReadOnlyList<string> acronyms) =>
        JsonSerializer.Serialize(acronyms.Select(a => new { acronym = a, settings = new { } }));
}
