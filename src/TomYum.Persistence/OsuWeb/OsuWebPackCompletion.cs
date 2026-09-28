using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Beatmap pack completion on the website's scores (stable and lazer alike), the way ppy's score
/// processor judges it: a set counts when the player has a passed, kept score on one of its maps, in
/// the pack's ruleset (or the map's own, for packs without one); challenge packs also exclude
/// difficulty reduction mods and scores without pp (unranked mods).
/// </summary>
public sealed class OsuWebPackCompletion
{
    private readonly OsuWebDbContext _db;

    public OsuWebPackCompletion(OsuWebDbContext db) => _db = db;

    /// <summary>When the player finished the pack (their last needed pass), or null when they haven't.</summary>
    public async Task<DateTime?> CompletedAtAsync(int userId, IReadOnlyCollection<int> sets, int? ruleset, bool noReductionMods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var wanted = sets.Distinct().ToList();
        if (wanted.Count == 0)
            return null;

        // Only numbers are spliced in; the mods test is a fixed fragment (ppy's own).
        var ids = string.Join(',', wanted.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        var rulesetTest = ruleset is { } r ? $"s.ruleset_id = {r.ToString(CultureInfo.InvariantCulture)}" : "s.ruleset_id = b.playmode";
        var modsTest = noReductionMods
            ? """
              AND JSON_SEARCH(s.data, 'one', 'EZ', NULL, '$.mods[*].acronym') IS NULL
              AND JSON_SEARCH(s.data, 'one', 'NF', NULL, '$.mods[*].acronym') IS NULL
              AND JSON_SEARCH(s.data, 'one', 'HT', NULL, '$.mods[*].acronym') IS NULL
              AND JSON_SEARCH(s.data, 'one', 'DC', NULL, '$.mods[*].acronym') IS NULL
              AND JSON_SEARCH(s.data, 'one', 'SO', NULL, '$.mods[*].acronym') IS NULL
              AND s.pp IS NOT NULL
              """
            : string.Empty;
#pragma warning disable EF1002 // see above
        var passes = await _db.Database.SqlQueryRaw<SetPass>(
            $"""
            SELECT b.beatmapset_id AS SetId, MIN(s.ended_at) AS At
            FROM scores s JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
            WHERE s.user_id = {userId.ToString(CultureInfo.InvariantCulture)} AND s.passed = 1 AND s.preserve = 1
              AND b.beatmapset_id IN ({ids}) AND {rulesetTest} {modsTest}
            GROUP BY b.beatmapset_id
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        return passes.Count == wanted.Count ? DateTime.SpecifyKind(passes.Max(p => p.At), DateTimeKind.Utc) : null;
    }

    /// <summary>Players with a passed score on any of <paramref name="sets"/>.</summary>
    public async Task<IReadOnlyList<int>> PlayersOfAsync(IReadOnlyCollection<int> sets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
            return [];
        var ids = string.Join(',', sets.Distinct().Select(s => s.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // numbers only
        return await _db.Database.SqlQueryRaw<int>(
            $"SELECT DISTINCT CAST(s.user_id AS SIGNED) AS Value FROM scores s JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id WHERE s.passed = 1 AND b.beatmapset_id IN ({ids})")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>The player, map and set of each of these website scores (passed ones only).</summary>
    public async Task<IReadOnlyList<(int UserId, int BeatmapId, int SetId)>> PassesAsync(IReadOnlyCollection<ulong> scoreIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scoreIds);
        if (scoreIds.Count == 0)
            return [];
        var ids = string.Join(',', scoreIds.Select(s => s.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // numbers only
        var rows = await _db.Database.SqlQueryRaw<PassRow>(
            $"""
            SELECT CAST(s.user_id AS SIGNED) AS UserId, CAST(s.beatmap_id AS SIGNED) AS BeatmapId, CAST(b.beatmapset_id AS SIGNED) AS SetId
            FROM scores s JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
            WHERE s.id IN ({ids}) AND s.passed = 1
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        return rows.Select(r => ((int)r.UserId, (int)r.BeatmapId, (int)r.SetId)).Distinct().ToList();
    }

    /// <summary>The players of these website scores.</summary>
    public async Task<IReadOnlyList<int>> PlayersOfScoresAsync(IReadOnlyCollection<ulong> scoreIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scoreIds);
        if (scoreIds.Count == 0)
            return [];
        var ids = string.Join(',', scoreIds.Select(s => s.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // numbers only
        return await _db.Database.SqlQueryRaw<int>($"SELECT DISTINCT CAST(user_id AS SIGNED) AS Value FROM scores WHERE id IN ({ids})")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>The medal ids the player has.</summary>
    public async Task<IReadOnlySet<int>> UnlockedAsync(int userId, CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQuery<int>($"SELECT CAST(achievement_id AS SIGNED) AS Value FROM osu_user_achievements WHERE user_id = {userId}")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

    /// <summary>
    /// Moves this server's packs out of the numbers osu! uses: ppy's score processor looks packs up
    /// by osu!'s numbers, and a local pack that happened to share one would give its medal away.
    /// Returns how many packs were moved.
    /// </summary>
    public async Task<int> MovePacksOutOfOsusNumbersAsync(int floor, CancellationToken cancellationToken = default)
    {
        var low = await _db.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM osu_beatmappacks WHERE pack_id < {floor}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (low == 0)
            return 0;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlAsync($"UPDATE osu_beatmappacks_items SET pack_id = pack_id + {floor} WHERE pack_id < {floor}", cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlAsync($"UPDATE osu_beatmappacks SET pack_id = pack_id + {floor} WHERE pack_id < {floor}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // New packs carry on above them.
#pragma warning disable EF1002 // a number
        await _db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE osu_beatmappacks AUTO_INCREMENT = {floor.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        return (int)low;
    }

    private sealed class SetPass
    {
        public uint SetId { get; set; }
        public DateTime At { get; set; }
    }

    private sealed class PassRow
    {
        public long UserId { get; set; }
        public long BeatmapId { get; set; }
        public long SetId { get; set; }
    }
}
