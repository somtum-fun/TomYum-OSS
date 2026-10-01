using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// The website's first places (profile "First Place Scores", team first places). ppy's servers keep
/// them in tables nothing here filled:
/// <list type="bullet">
/// <item><c>beatmap_leaders</c>: each map's #1 in each ruleset by total score, stable and lazer alike
/// (the website's default view);</item>
/// <item><c>osu_leaders</c>, <c>_taiko</c>, <c>_fruits</c>, <c>_mania</c>: the #1 among stable scores by
/// stable's score, keyed by the stable score id (the "legacy" view).</item>
/// </list>
/// Only maps with a leaderboard (ranked, approved, qualified, loved) and players who aren't
/// restricted count, like the leaderboards themselves.
/// </summary>
public sealed class OsuWebLeaders
{
    private static readonly string[] LegacyTables = ["osu_leaders", "osu_leaders_taiko", "osu_leaders_fruits", "osu_leaders_mania"];

    private const string Eligible = """
        s.passed = 1 AND s.preserve = 1 AND u.user_type <> 1 AND u.user_warnings = 0
          AND b.approved IN (1, 2, 3, 4) AND b.deleted_at IS NULL
        """;

    private readonly OsuWebDbContext _db;

    public OsuWebLeaders(OsuWebDbContext db) => _db = db;

    /// <summary>Every map's first places again (after restrictions, removed scores, status changes).</summary>
    public Task RebuildAsync(CancellationToken cancellationToken = default) => SyncCoreAsync(null, cancellationToken);

    /// <summary>The first places of these (map, ruleset) pairs again.</summary>
    public Task SyncAsync(IReadOnlyCollection<(int BeatmapId, int Ruleset)> maps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(maps);
        return maps.Count == 0 ? Task.CompletedTask : SyncCoreAsync(maps, cancellationToken);
    }

    /// <summary>The (map, ruleset) pairs with scores after <paramref name="afterScoreId"/>, and the newest score id.</summary>
    public async Task<(IReadOnlyList<(int BeatmapId, int Ruleset)> Maps, ulong Newest)> ChangedSinceAsync(ulong afterScoreId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Scores.AsNoTracking()
            .Where(s => s.Id > afterScoreId)
            .GroupBy(s => new { s.BeatmapId, s.RulesetId })
            .Select(g => new { g.Key.BeatmapId, g.Key.RulesetId, Newest = g.Max(s => s.Id) })
            // Oldest changes first: the mark then never passes a map that wasn't looked at.
            .OrderBy(g => g.Newest)
            .Take(2000)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return (rows.Select(r => ((int)r.BeatmapId, (int)r.RulesetId)).ToList(), rows.Count == 0 ? afterScoreId : rows.Max(r => r.Newest));
    }

    /// <summary>The newest score id (where the incremental sync starts).</summary>
    public async Task<ulong> NewestScoreIdAsync(CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking().MaxAsync(s => (ulong?)s.Id, cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>Tries again (a few times) when MySQL picked this as a deadlock's victim or a lock wait ran out.</summary>
    private const int Attempts = 3;

    private async Task SyncCoreAsync(IReadOnlyCollection<(int BeatmapId, int Ruleset)>? maps, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SyncOnceAsync(maps, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (MySqlConnector.MySqlException ex) when (attempt < Attempts
                && ex.ErrorCode is MySqlConnector.MySqlErrorCode.LockDeadlock or MySqlConnector.MySqlErrorCode.LockWaitTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SyncOnceAsync(IReadOnlyCollection<(int BeatmapId, int Ruleset)>? maps, CancellationToken cancellationToken)
    {
        // Only numbers from the pairs are spliced in.
        var only = maps is null
            ? "1 = 1"
            : "(" + string.Join(" OR ", maps.Select(m => string.Create(CultureInfo.InvariantCulture, $"(s.beatmap_id = {m.BeatmapId} AND s.ruleset_id = {m.Ruleset})"))) + ")";
        var target = maps is null
            ? "1 = 1"
            : "(" + string.Join(" OR ", maps.Select(m => string.Create(CultureInfo.InvariantCulture, $"(beatmap_id = {m.BeatmapId} AND ruleset_id = {m.Ruleset})"))) + ")";

        // READ COMMITTED: the INSERT … SELECT reads the scores without locking them, so it doesn't
        // deadlock with ppy's score processor writing new ones (it did, now and then, at MySQL's default).
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
#pragma warning disable EF1002 // see above
        await _db.Database.ExecuteSqlRawAsync($"DELETE FROM beatmap_leaders WHERE {target}", cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO beatmap_leaders (score_id, beatmap_id, ruleset_id, user_id)
            SELECT id, beatmap_id, ruleset_id, user_id FROM (
              SELECT s.id, s.beatmap_id, s.ruleset_id, s.user_id,
                ROW_NUMBER() OVER (PARTITION BY s.beatmap_id, s.ruleset_id ORDER BY s.total_score DESC, s.id) AS place
              FROM scores s
              JOIN phpbb_users u ON u.user_id = s.user_id
              JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
              WHERE {Eligible} AND {only}
            ) ranked WHERE place = 1
            """, cancellationToken).ConfigureAwait(false);

        for (var ruleset = 0; ruleset < LegacyTables.Length; ruleset++)
        {
            var mode = ruleset.ToString(CultureInfo.InvariantCulture);
            var legacyTarget = maps is null
                ? "1 = 1"
                : maps.Any(m => m.Ruleset == ruleset)
                    ? "beatmap_id IN (" + string.Join(", ", maps.Where(m => m.Ruleset == ruleset).Select(m => m.BeatmapId.ToString(CultureInfo.InvariantCulture))) + ")"
                    : null;
            if (legacyTarget is null)
                continue;
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM `{LegacyTables[ruleset]}` WHERE {legacyTarget}", cancellationToken).ConfigureAwait(false);
            await _db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO `{LegacyTables[ruleset]}` (beatmap_id, user_id, score_id)
                SELECT beatmap_id, user_id, legacy_score_id FROM (
                  SELECT s.beatmap_id, s.user_id, s.legacy_score_id,
                    ROW_NUMBER() OVER (PARTITION BY s.beatmap_id ORDER BY COALESCE(NULLIF(s.legacy_total_score, 0), s.total_score) DESC, s.id) AS place
                  FROM scores s
                  JOIN phpbb_users u ON u.user_id = s.user_id
                  JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
                  WHERE {Eligible} AND s.legacy_score_id IS NOT NULL AND s.ruleset_id = {mode} AND {only}
                ) ranked WHERE place = 1
                """, cancellationToken).ConfigureAwait(false);
        }
#pragma warning restore EF1002
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
