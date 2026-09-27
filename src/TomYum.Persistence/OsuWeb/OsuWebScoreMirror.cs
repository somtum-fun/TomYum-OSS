using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Copies TomYum's stable scores and per-mode statistics into osu-web (<c>scores</c>,
/// <c>osu_user_stats*</c>) so profiles, rankings and beatmap leaderboards on the website show them.
/// TomYum's own tables stay the source of truth; this is a one-way mirror (docs/osu-web.md).
/// </summary>
/// <remarks>
/// Only vanilla rulesets are mirrored (<see cref="OsuWebScores.IsMirrored"/>). Stable scores are
/// written the way osu-web stores legacy scores: <c>legacy_score_id</c> set (0: TomYum has no
/// <c>osu_scores_high</c> row), <c>legacy_total_score</c> = the stable score, <c>CL</c> mod. osu-web
/// ranks scores by <c>total_score</c>, which is also set to the stable score, so boards order the
/// way they do in-game. Which TomYum score became which osu-web score is recorded in TomYum's
/// <c>osu_web_score_links</c> table, so a score is never copied twice.
/// </remarks>
public sealed class OsuWebScoreMirror
{
    private const string LinkTableDdl =
        """
        CREATE TABLE IF NOT EXISTS `osu_web_score_links` (
          `ScoreId` bigint NOT NULL,
          `OsuWebScoreId` bigint unsigned NOT NULL,
          PRIMARY KEY (`ScoreId`)
        ) CHARACTER SET=utf8mb4;
        """;

    /// <summary>
    /// With the score processor: the stable counters TomYum last added to each <c>osu_user_stats*</c>
    /// row, so later writes add only the difference and lazer's share of the row survives.
    /// </summary>
    private const string WrittenStatsTableDdl =
        """
        CREATE TABLE IF NOT EXISTS `osu_web_stats_written` (
          `UserId` int NOT NULL,
          `Mode` int NOT NULL,
          `Count300` bigint NOT NULL,
          `Count100` bigint NOT NULL,
          `Count50` bigint NOT NULL,
          `CountMiss` bigint NOT NULL,
          `PlayCount` bigint NOT NULL,
          `SecondsPlayed` bigint NOT NULL,
          `RankedScore` bigint NOT NULL,
          `TotalScore` bigint NOT NULL,
          PRIMARY KEY (`UserId`, `Mode`)
        ) CHARACTER SET=utf8mb4;
        """;

    private readonly TomYumDbContext _tomyum;
    private readonly OsuWebDbContext _osuWeb;
    private readonly OsuWebMirrorOptions _options;

    public OsuWebScoreMirror(TomYumDbContext tomyum, OsuWebDbContext osuWeb, OsuWebMirrorOptions? options = null)
    {
        _tomyum = tomyum;
        _osuWeb = osuWeb;
        _options = options ?? new OsuWebMirrorOptions();
    }

    /// <summary>Whether ppy's score processor owns pp, accuracy, ranks and grade counts on the website.</summary>
    public bool UsesScoreProcessor => _options.ScoreProcessor;

    /// <summary>Creates TomYum's link tables if missing (idempotent; run at startup).</summary>
    public async Task EnsureLinkTableAsync(CancellationToken cancellationToken = default)
    {
        await _tomyum.Database.ExecuteSqlRawAsync(LinkTableDdl, cancellationToken).ConfigureAwait(false);
        await _tomyum.Database.ExecuteSqlRawAsync(WrittenStatsTableDdl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="score"/> to osu-web's <c>scores</c>. Returns the osu-web score id, or
    /// <c>null</c> when the score isn't mirrored (relax/autopilot, or its map isn't on osu-web).
    /// Writing the same score again returns the existing id.
    /// </summary>
    public async Task<ulong?> WriteScoreAsync(Score score, CancellationToken cancellationToken = default) =>
        await WriteScoreAsync(score, hasReplay: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Like <see cref="WriteScoreAsync(Score, CancellationToken)"/>; <paramref name="hasReplay"/> marks
    /// the website copy as having a replay (osu-web fetches it from TomYum when downloaded).
    /// </summary>
    public async Task<ulong?> WriteScoreAsync(Score score, bool hasReplay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (!OsuWebScores.IsMirrored(score.Mode) || score.UserId <= 0 || score.BeatmapId <= 0)
            return null;

        var linked = await _tomyum.Database
            .SqlQuery<ulong>($"SELECT OsuWebScoreId AS Value FROM osu_web_score_links WHERE ScoreId = {score.Id}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (linked.Count > 0)
            return linked[0];

        var beatmapId = (uint)score.BeatmapId;
        var beatmap = await _osuWeb.Beatmaps.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BeatmapId == beatmapId, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
            return null;

        var ranked = score.Passed && beatmap.Approved > 0;
        var stableScore = (uint)Math.Clamp(score.ScoreValue, 0, uint.MaxValue);
        var row = new OsuWebScore
        {
            UserId = (uint)score.UserId,
            RulesetId = (ushort)score.Mode,
            BeatmapId = beatmapId,
            Preserve = score.Passed,
            Ranked = ranked,
            HasReplay = hasReplay,
            Rank = score.Passed ? score.Grade : "F",
            Passed = score.Passed,
            Accuracy = (float)AccuracyCalculator.Compute(
                score.Mode, score.Count300, score.Count100, score.Count50, score.CountGeki, score.CountKatu, score.CountMiss),
            MaxCombo = (uint)Math.Max(0, score.MaxCombo),
            TotalScore = stableScore,
            LegacyTotalScore = stableScore,
            LegacyScoreId = 0,
            Data = OsuWebScores.DataJson(score),
            Pp = ranked && score.Pp > 0 ? (float)score.Pp : null,
            EndedAt = OsuWebBeatmaps.ToTimestamp(score.SubmittedAt, DateTime.UtcNow),
        };

        _osuWeb.Scores.Add(row);
        await _osuWeb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _osuWeb.ChangeTracker.Clear();

        await _tomyum.Database
            .ExecuteSqlInterpolatedAsync($"INSERT INTO osu_web_score_links (ScoreId, OsuWebScoreId) VALUES ({score.Id}, {row.Id})", cancellationToken)
            .ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>
    /// Rewrites the player's <c>osu_user_stats*</c> row for <paramref name="mode"/> from TomYum's
    /// data: pp, accuracy, ranked/total score and play count from TomYum's stats; hit counts, max
    /// combo, grade counts (of best scores) and play time (passed plays' map lengths) from TomYum's
    /// scores. Recomputing instead of adding keeps the row from drifting. The global rank is set by
    /// <see cref="RecomputeGlobalRanksAsync"/>.
    /// </summary>
    public async Task WriteStatsAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        var table = OsuWebScores.StatsTable(mode);

        var stats = await _tomyum.UserStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken).ConfigureAwait(false);
        if (stats is null)
            return;

        var uid = (uint)userId;
        var country = await _osuWeb.Users.AsNoTracking().Where(u => u.UserId == uid)
            .Select(u => u.CountryAcronym).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (country is null)
            return; // not an osu-web account

        var scores = _tomyum.Scores.AsNoTracking().Where(s => s.UserId == userId && s.Mode == mode);
        var totals = await scores
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count300 = g.Sum(s => (long)s.Count300),
                Count100 = g.Sum(s => (long)s.Count100),
                Count50 = g.Sum(s => (long)s.Count50),
                CountMiss = g.Sum(s => (long)s.CountMiss),
                MaxCombo = g.Max(s => s.MaxCombo),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false); // one group at most
        var grades = await scores
            .Where(s => s.Status == ScoreStatus.Best)
            .GroupBy(s => s.Grade)
            .Select(g => new { Grade = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Grade, g => g.Count, cancellationToken).ConfigureAwait(false);
        var secondsPlayed = await (
                from s in scores
                join b in _tomyum.Beatmaps.AsNoTracking() on s.BeatmapId equals b.Id
                where s.Passed
                select (long?)b.TotalLength)
            .SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;

        int Grade(string grade) => grades.TryGetValue(grade, out var n) ? n : 0;

        if (_options.ScoreProcessor)
        {
            var counters = new StableCounters(
                totals?.Count300 ?? 0, totals?.Count100 ?? 0, totals?.Count50 ?? 0, totals?.CountMiss ?? 0,
                stats.PlayCount, secondsPlayed, stats.RankedScore, stats.TotalScore);
            await WriteStableCountersAsync(table, userId, mode, country, counters, Math.Clamp(totals?.MaxCombo ?? 0, 0, ushort.MaxValue), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Table name comes from a fixed whitelist (OsuWebScores.StatsTable), never from input.
        var sql = $$"""
            INSERT INTO `{{table}}` (user_id, count300, count100, count50, countMiss, accuracy_total, accuracy_count,
              accuracy, accuracy_new, playcount, total_seconds_played, ranked_score, total_score,
              x_rank_count, xh_rank_count, s_rank_count, sh_rank_count, a_rank_count, `rank`, level, max_combo,
              country_acronym, rank_score, rank_score_index, last_played)
            VALUES ({0}, {1}, {2}, {3}, {4}, 0, 0, {5}, {6}, {7}, {8}, {9}, {10},
              {11}, {12}, {13}, {14}, {15}, 0, {16}, {17}, {18}, {19}, 0, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
              count300 = VALUES(count300), count100 = VALUES(count100), count50 = VALUES(count50),
              countMiss = VALUES(countMiss), accuracy = VALUES(accuracy), accuracy_new = VALUES(accuracy_new),
              playcount = VALUES(playcount), total_seconds_played = VALUES(total_seconds_played),
              ranked_score = VALUES(ranked_score), total_score = VALUES(total_score),
              x_rank_count = VALUES(x_rank_count), xh_rank_count = VALUES(xh_rank_count),
              s_rank_count = VALUES(s_rank_count), sh_rank_count = VALUES(sh_rank_count),
              a_rank_count = VALUES(a_rank_count), level = VALUES(level), max_combo = VALUES(max_combo),
              country_acronym = VALUES(country_acronym), rank_score = VALUES(rank_score),
              last_played = VALUES(last_played)
            """;

        await _osuWeb.Database.ExecuteSqlRawAsync(
            sql,
            [
                uid,
                totals?.Count300 ?? 0, totals?.Count100 ?? 0, totals?.Count50 ?? 0, totals?.CountMiss ?? 0,
                stats.Accuracy,                 // legacy column: 0-1
                stats.Accuracy * 100.0,         // accuracy_new: percent (osu-web hit_accuracy)
                stats.PlayCount, secondsPlayed, stats.RankedScore, stats.TotalScore,
                Grade("X"), Grade("XH"), Grade("S"), Grade("SH"), Grade("A"),
                OsuWebScores.Level(stats.TotalScore),
                Math.Clamp(totals?.MaxCombo ?? 0, 0, ushort.MaxValue),
                country,
                Math.Max(0, stats.Pp),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>TomYum's stable-only counters for one stats row.</summary>
    private sealed record StableCounters(
        long Count300, long Count100, long Count50, long CountMiss, long PlayCount, long SecondsPlayed, long RankedScore, long TotalScore);

    /// <summary>Row of <c>osu_web_stats_written</c>.</summary>
    private sealed class WrittenCounters
    {
        public long Count300 { get; set; }
        public long Count100 { get; set; }
        public long Count50 { get; set; }
        public long CountMiss { get; set; }
        public long PlayCount { get; set; }
        public long SecondsPlayed { get; set; }
        public long RankedScore { get; set; }
        public long TotalScore { get; set; }
    }

    /// <summary>
    /// Score-processor mode: adds what changed in TomYum's stable counters since the last write, so
    /// the processor's lazer counts in the same row are kept. The first write for a row (from before
    /// the processor, when the row held only stable data) sets the counters instead. pp, accuracy,
    /// ranks and grade counts are left to the processor and <see cref="RecomputeTotalPerformanceAsync"/>.
    /// </summary>
    private async Task WriteStableCountersAsync(string table, int userId, int mode, string country, StableCounters now, int maxCombo, CancellationToken cancellationToken)
    {
        var before = (await _tomyum.Database
                .SqlQuery<WrittenCounters>($"""
                    SELECT Count300, Count100, Count50, CountMiss, PlayCount, SecondsPlayed, RankedScore, TotalScore
                    FROM osu_web_stats_written WHERE UserId = {userId} AND Mode = {mode}
                    """)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

        // Either "set to the value" (first write) or "add the difference" to each counter column.
        string Column(string name, long value, long? previous) => previous is { } old
            ? string.Create(CultureInfo.InvariantCulture, $"{name} = GREATEST(0, CAST({name} AS SIGNED) + {value - old})")
            : string.Create(CultureInfo.InvariantCulture, $"{name} = {value}");

        var updates = string.Join(", ",
            Column("count300", now.Count300, before?.Count300),
            Column("count100", now.Count100, before?.Count100),
            Column("count50", now.Count50, before?.Count50),
            Column("countMiss", now.CountMiss, before?.CountMiss),
            Column("playcount", now.PlayCount, before?.PlayCount),
            Column("total_seconds_played", now.SecondsPlayed, before?.SecondsPlayed),
            Column("ranked_score", now.RankedScore, before?.RankedScore),
            Column("total_score", now.TotalScore, before?.TotalScore));

        // Table name from a fixed whitelist and numbers formatted invariantly: nothing here is user text.
        var sql = string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO `{{table}}` (user_id, count300, count100, count50, countMiss, accuracy_total, accuracy_count,
              accuracy, accuracy_new, playcount, total_seconds_played, ranked_score, total_score,
              x_rank_count, xh_rank_count, s_rank_count, sh_rank_count, a_rank_count, `rank`, level, max_combo,
              country_acronym, rank_score, rank_score_index, last_played)
            VALUES ({{userId}}, {{now.Count300}}, {{now.Count100}}, {{now.Count50}}, {{now.CountMiss}}, 0, 0, 0, 0,
              {{now.PlayCount}}, {{now.SecondsPlayed}}, {{now.RankedScore}}, {{now.TotalScore}},
              0, 0, 0, 0, 0, 0, {{OsuWebScores.Level(now.TotalScore)}}, {{maxCombo}}, {0}, 0, 0, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE {{updates}}, max_combo = GREATEST(max_combo, {{maxCombo}}),
              country_acronym = VALUES(country_acronym), last_played = UTC_TIMESTAMP()
            """);
#pragma warning disable EF1002 // see above; the country goes in as a parameter
        await _osuWeb.Database.ExecuteSqlRawAsync(sql, [country], cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002

        // The level follows the row's total score, which now includes lazer's.
        var totalSql = "SELECT CAST(total_score AS SIGNED) AS Value FROM `" + table + "` WHERE user_id = {0}";
        var total = (await _osuWeb.Database.SqlQueryRaw<long>(totalSql, (uint)userId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();
        var levelSql = "UPDATE `" + table + "` SET level = {0} WHERE user_id = {1}";
        await _osuWeb.Database.ExecuteSqlRawAsync(levelSql, [OsuWebScores.Level(total), (uint)userId], cancellationToken).ConfigureAwait(false);

        await _tomyum.Database.ExecuteSqlInterpolatedAsync($"""
            REPLACE INTO osu_web_stats_written (UserId, Mode, Count300, Count100, Count50, CountMiss, PlayCount, SecondsPlayed, RankedScore, TotalScore)
            VALUES ({userId}, {mode}, {now.Count300}, {now.Count100}, {now.Count50}, {now.CountMiss}, {now.PlayCount}, {now.SecondsPlayed}, {now.RankedScore}, {now.TotalScore})
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A website score as the pp total reads it.</summary>
    private sealed class PerformanceRow
    {
        public uint BeatmapId { get; set; }
        public float Pp { get; set; }
        public float Accuracy { get; set; }
    }

    /// <summary>
    /// Sets the player's total pp (<c>rank_score</c>) and accuracy from their website scores in
    /// <paramref name="mode"/>, stable and lazer together, exactly as ppy's processor does
    /// (<see cref="OsuWebScores.TotalPerformance"/>). For changes the processor doesn't see: removed
    /// scores, staff status changes. Returns the new values, or null without a stats row.
    /// </summary>
    public async Task<OsuWebPerformance?> RecomputeTotalPerformanceAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        var table = OsuWebScores.StatsTable(mode);
        var uid = (uint)userId;
        var scores = await _osuWeb.Database
            .SqlQuery<PerformanceRow>($"""
                SELECT beatmap_id AS BeatmapId, pp AS Pp, accuracy AS Accuracy FROM scores
                WHERE user_id = {uid} AND ruleset_id = {mode} AND pp IS NOT NULL AND preserve = 1 AND ranked = 1
                ORDER BY pp DESC LIMIT 1000
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var (pp, accuracy) = OsuWebScores.TotalPerformance(scores.Select(s => (s.BeatmapId, (double)s.Pp, (double)s.Accuracy)).ToList());

        var updateSql = "UPDATE `" + table + "` SET rank_score = {0}, accuracy_new = {1}, accuracy = {2} WHERE user_id = {3}";
        var updated = await _osuWeb.Database.ExecuteSqlRawAsync(updateSql, [pp, accuracy, accuracy / 100.0, uid], cancellationToken).ConfigureAwait(false);
        return updated == 0 ? null : new OsuWebPerformance(pp, (float)(accuracy / 100.0));
    }

    /// <summary>The player's total pp and accuracy (0-1) on the website, or null without a stats row.</summary>
    public async Task<OsuWebPerformance?> ReadPerformanceAsync(int userId, int mode, CancellationToken cancellationToken = default)
    {
        var table = OsuWebScores.StatsTable(mode);
        var sql = "SELECT 0 AS BeatmapId, rank_score AS Pp, accuracy_new AS Accuracy FROM `" + table + "` WHERE user_id = {0}";
        var rows = await _osuWeb.Database.SqlQueryRaw<PerformanceRow>(sql, (uint)userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : new OsuWebPerformance(rows[0].Pp, rows[0].Accuracy / 100f);
    }

    /// <summary>A <c>score_process_history</c> row.</summary>
    private sealed class ProcessedRow
    {
        public ulong ScoreId { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Queue items for ppy's score processor for the website scores <paramref name="osuWebScoreIds"/>:
    /// <c>{"Score": row}</c>, the shape osu-web's <c>Solo\Score::queueForProcessing</c> sends. With
    /// <paramref name="reprocess"/>, scores the processor already handled get their history one version
    /// back, which makes it undo and redo them (it skips scores at its current version). Used after a
    /// staff status change, when their pp changes.
    /// </summary>
    public async Task<IReadOnlyList<string>> BuildProcessingItemsAsync(IReadOnlyCollection<ulong> osuWebScoreIds, bool reprocess = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(osuWebScoreIds);

        var items = new List<string>();
        foreach (var chunk in osuWebScoreIds.Chunk(500))
        {
            var rows = await _osuWeb.Scores.AsNoTracking().Where(s => chunk.Contains(s.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var versions = new Dictionary<ulong, int>();
            if (reprocess && rows.Count > 0)
            {
                var list = string.Join(",", rows.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)));
                var sql = "SELECT score_id AS ScoreId, processed_version AS Version FROM score_process_history WHERE score_id IN (" + list + ")";
                versions = (await _osuWeb.Database.SqlQueryRaw<ProcessedRow>(sql).ToListAsync(cancellationToken).ConfigureAwait(false))
                    .ToDictionary(r => r.ScoreId, r => r.Version);
            }
            items.AddRange(rows.Select(r => OsuWebScores.ProcessingItem(r, versions.TryGetValue(r.Id, out var v) && v > 0 ? v - 1 : null)));
        }
        return items;
    }

    /// <summary>A lazer score's owner and ruleset.</summary>
    private sealed class PlayerRow
    {
        public uint UserId { get; set; }
        public int RulesetId { get; set; }
    }

    /// <summary>
    /// After a staff status change: lazer scores on <paramref name="beatmapIds"/> whose map no longer
    /// gives pp (not ranked/approved on the website) lose their pp. ppy's processor never takes pp
    /// away, it only skips unranked maps. Returns every player with lazer scores on those maps, whose
    /// totals need redoing.
    /// </summary>
    public async Task<IReadOnlyList<(int UserId, int Mode)>> ClearLazerPpOnUnrankedAsync(IReadOnlyCollection<int> beatmapIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapIds);
        if (beatmapIds.Count == 0)
            return [];

        var list = string.Join(",", beatmapIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var clear = "UPDATE scores s JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id SET s.pp = NULL "
            + "WHERE s.legacy_score_id IS NULL AND s.pp IS NOT NULL AND b.approved NOT IN (1, 2) AND s.beatmap_id IN (" + list + ")";
        await _osuWeb.Database.ExecuteSqlRawAsync(clear, cancellationToken).ConfigureAwait(false);

        var players = "SELECT DISTINCT user_id AS UserId, ruleset_id AS RulesetId FROM scores WHERE legacy_score_id IS NULL AND beatmap_id IN (" + list + ")";
        return (await _osuWeb.Database.SqlQueryRaw<PlayerRow>(players).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(p => ((int)p.UserId, p.RulesetId))
            .ToList();
    }

    /// <summary>Every website score (stable and lazer) on <paramref name="beatmapIds"/>, to re-process after a status change.</summary>
    public async Task<IReadOnlyList<ulong>> GetScoreIdsOnBeatmapsAsync(IReadOnlyCollection<int> beatmapIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapIds);
        var ids = beatmapIds.Select(id => (uint)id).ToList();
        return await _osuWeb.Scores.AsNoTracking().Where(s => ids.Contains(s.BeatmapId)).Select(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the processor has handled website score <paramref name="osuWebScoreId"/> (its
    /// <c>score_process_history</c> row exists), up to <paramref name="timeout"/>. False on timeout.
    /// </summary>
    public async Task<bool> WaitForProcessingAsync(ulong osuWebScoreId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var done = await _osuWeb.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM score_process_history WHERE score_id = {osuWebScoreId}")
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            if (done > 0)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies TomYum's existing history to osu-web: every vanilla score not copied yet (oldest
    /// first, so osu-web ids follow play order), then every player's stats, then global ranks.
    /// Returns the new osu-web score ids (queue them for osu-web's score indexer). Only safe once
    /// TomYum's user ids are osu-web's — after <see cref="OsuWebAccountMigration"/>, or on a server
    /// that has only ever used osu-web accounts. Re-running copies only what's missing.
    /// </summary>
    public async Task<IReadOnlyList<ulong>> CopyHistoryAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await EnsureLinkTableAsync(cancellationToken).ConfigureAwait(false);

        var linked = (await _tomyum.Database.SqlQueryRaw<long>("SELECT ScoreId AS Value FROM osu_web_score_links")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();
        var pending = (await _tomyum.Scores.AsNoTracking()
                .Where(s => s.Mode >= 0 && s.Mode < GameMode.RelaxOffset)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(id => !linked.Contains(id))
            .ToList();

        var copied = new List<ulong>();
        foreach (var batch in pending.Chunk(500))
        {
            var scores = await _tomyum.Scores.AsNoTracking()
                .Where(s => batch.Contains(s.Id))
                .OrderBy(s => s.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var score in scores)
            {
                if (await WriteScoreAsync(score, cancellationToken).ConfigureAwait(false) is { } id)
                    copied.Add(id);
            }
            progress?.Report(copied.Count);
        }

        var players = await _tomyum.UserStats.AsNoTracking()
            .Where(s => s.Mode >= 0 && s.Mode < GameMode.RelaxOffset)
            .Select(s => new { s.UserId, s.Mode })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var player in players)
            await WriteStatsAsync(player.UserId, player.Mode, cancellationToken).ConfigureAwait(false);

        for (var mode = 0; mode < GameMode.RelaxOffset; mode++)
            await RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);

        return copied;
    }

    /// <summary>
    /// Sets <c>rank_score_index</c> (osu-web's global rank) for everyone in <paramref name="mode"/>:
    /// ordered by pp, ties sharing a rank, restricted/banned accounts and players without pp at 0
    /// (osu-web shows 0 as unranked). osu-web itself doesn't compute this; its ranking page and
    /// profile rank read it.
    /// </summary>
    public Task RecomputeGlobalRanksAsync(int mode, CancellationToken cancellationToken = default)
    {
        var table = OsuWebScores.StatsTable(mode);
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            UPDATE `{table}` s
            LEFT JOIN (
              SELECT st.user_id, RANK() OVER (ORDER BY st.rank_score DESC) AS position
              FROM `{table}` st
              JOIN phpbb_users u ON u.user_id = st.user_id
              WHERE st.rank_score > 0 AND u.user_type <> 1 AND u.user_warnings = 0
            ) ranked ON ranked.user_id = s.user_id
            SET s.rank_score_index = COALESCE(ranked.position, 0)
            """);
        return _osuWeb.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    /// <summary>
    /// Counts a play on osu-web: the player's play count for the map ("Most Played Beatmaps" on
    /// profiles), the map's play/pass counts and its set's play count. Once per submitted score,
    /// passed or failed; vanilla rulesets only, like the rest of the mirror.
    /// </summary>
    public async Task RecordPlayAsync(Score score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!OsuWebScores.IsMirrored(score.Mode) || score.UserId <= 0 || score.BeatmapId <= 0)
            return;

        var beatmapId = (uint)score.BeatmapId;
        var setId = await _osuWeb.Beatmaps.AsNoTracking().Where(b => b.BeatmapId == beatmapId)
            .Select(b => b.BeatmapsetId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (setId is null)
            return; // the map isn't on the website

        var userId = (uint)score.UserId;
        var passed = score.Passed ? 1 : 0;
        await _osuWeb.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO osu_user_beatmap_playcount (user_id, beatmap_id, playcount) VALUES ({userId}, {beatmapId}, 1) ON DUPLICATE KEY UPDATE playcount = LEAST(playcount + 1, 65535)",
            cancellationToken).ConfigureAwait(false);
        await _osuWeb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE osu_beatmaps SET playcount = playcount + 1, passcount = passcount + {passed} WHERE beatmap_id = {beatmapId}",
            cancellationToken).ConfigureAwait(false);
        await _osuWeb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE osu_beatmapsets SET play_count = play_count + 1 WHERE beatmapset_id = {setId}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds every osu-web play count from TomYum's scores (the source of truth), so plays from
    /// before counting existed show up. Returns how many player/map pairs were written. Run at startup.
    /// </summary>
    public async Task<int> RebuildPlayCountsAsync(CancellationToken cancellationToken = default)
    {
        var plays = await _tomyum.Scores.AsNoTracking()
            .Where(s => s.Mode >= 0 && s.Mode < GameMode.RelaxOffset && s.UserId > 0 && s.BeatmapId > 0)
            .GroupBy(s => new { s.UserId, s.BeatmapId })
            .Select(g => new { g.Key.UserId, g.Key.BeatmapId, Plays = g.Count(), Passes = g.Count(s => s.Passed) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var onWebsite = (await _osuWeb.Beatmaps.AsNoTracking().Select(b => b.BeatmapId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();
        plays = plays.Where(p => onWebsite.Contains((uint)p.BeatmapId)).ToList();

        await _osuWeb.Database.ExecuteSqlRawAsync("DELETE FROM osu_user_beatmap_playcount", cancellationToken).ConfigureAwait(false);
        await _osuWeb.Database.ExecuteSqlRawAsync("UPDATE osu_beatmaps SET playcount = 0, passcount = 0 WHERE playcount <> 0 OR passcount <> 0", cancellationToken).ConfigureAwait(false);
        await _osuWeb.Database.ExecuteSqlRawAsync("UPDATE osu_beatmapsets SET play_count = 0 WHERE play_count <> 0", cancellationToken).ConfigureAwait(false);

        foreach (var chunk in plays.Chunk(500))
        {
            var rows = string.Join(",", chunk.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"({(uint)p.UserId},{(uint)p.BeatmapId},{Math.Min(p.Plays, 65535)})")));
#pragma warning disable EF1002 // numbers formatted invariantly, not user text
            await _osuWeb.Database.ExecuteSqlRawAsync(
                $"INSERT INTO osu_user_beatmap_playcount (user_id, beatmap_id, playcount) VALUES {rows}", cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        foreach (var map in plays.GroupBy(p => p.BeatmapId))
        {
            var beatmapId = (uint)map.Key;
            var total = map.Sum(p => p.Plays);
            var passes = map.Sum(p => p.Passes);
            await _osuWeb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE osu_beatmaps SET playcount = {total}, passcount = {passes} WHERE beatmap_id = {beatmapId}", cancellationToken).ConfigureAwait(false);
        }

        await _osuWeb.Database.ExecuteSqlRawAsync(
            """
            UPDATE osu_beatmapsets s
            JOIN (SELECT beatmapset_id, SUM(playcount) AS plays FROM osu_beatmaps GROUP BY beatmapset_id) b ON b.beatmapset_id = s.beatmapset_id
            SET s.play_count = b.plays
            """, cancellationToken).ConfigureAwait(false);

        return plays.Count;
    }

    /// <summary>The TomYum score an osu-web score is a copy of (for replay downloads), or null.</summary>
    public async Task<long?> GetTomYumScoreIdAsync(ulong osuWebScoreId, CancellationToken cancellationToken = default)
    {
        var ids = await _tomyum.Database
            .SqlQuery<long>($"SELECT ScoreId AS Value FROM osu_web_score_links WHERE OsuWebScoreId = {osuWebScoreId}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>
    /// Marks the website copies of scores that have a replay (<paramref name="hasReplay"/> gets the
    /// TomYum score id). For scores copied before replays were linked; returns how many were marked.
    /// </summary>
    public async Task<int> MarkReplaysAsync(Func<long, bool> hasReplay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasReplay);

        var links = await _tomyum.Database
            .SqlQuery<ScoreLink>($"SELECT ScoreId, OsuWebScoreId FROM osu_web_score_links")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var withReplay = links.Where(l => hasReplay(l.ScoreId)).Select(l => l.OsuWebScoreId).ToList();

        var marked = 0;
        foreach (var chunk in withReplay.Chunk(500))
        {
            marked += await _osuWeb.Scores.Where(s => chunk.Contains(s.Id) && !s.HasReplay)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.HasReplay, true), cancellationToken).ConfigureAwait(false);
        }
        return marked;
    }

    /// <summary>A row of <c>osu_web_score_links</c>.</summary>
    public sealed record ScoreLink(long ScoreId, ulong OsuWebScoreId);

    /// <summary>The player's osu-web country code (e.g. <c>TH</c>), or null when it's not an osu-web account.</summary>
    public Task<string?> GetCountryAsync(int userId, CancellationToken cancellationToken = default)
    {
        var uid = (uint)userId;
        return _osuWeb.Users.AsNoTracking().Where(u => u.UserId == uid)
            .Select(u => u.CountryAcronym).FirstOrDefaultAsync(cancellationToken)!;
    }

    /// <summary>The osu-web copies of <paramref name="userId"/>'s scores (for re-indexing after a restriction change).</summary>
    public async Task<IReadOnlyList<ulong>> GetLinkedScoreIdsAsync(int userId, CancellationToken cancellationToken = default) =>
        await _tomyum.Database
            .SqlQuery<ulong>($"SELECT l.OsuWebScoreId AS Value FROM osu_web_score_links l JOIN scores s ON s.Id = l.ScoreId WHERE s.UserId = {userId}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Deletes the osu-web copies of TomYum scores <paramref name="scoreIds"/> (and their links);
    /// returns the deleted osu-web score ids, to queue for the search index (which drops missing scores).
    /// </summary>
    public async Task<IReadOnlyList<ulong>> DeleteScoresAsync(IReadOnlyCollection<long> scoreIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scoreIds);
        if (scoreIds.Count == 0)
            return [];

        var ids = scoreIds.ToArray();
        var linked = new List<ulong>();
        foreach (var chunk in ids.Chunk(500))
        {
            var list = string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // ids are longs formatted invariantly, not user text
            linked.AddRange(await _tomyum.Database
                .SqlQueryRaw<ulong>($"SELECT OsuWebScoreId AS Value FROM osu_web_score_links WHERE ScoreId IN ({list})")
                .ToListAsync(cancellationToken).ConfigureAwait(false));
            await _tomyum.Database.ExecuteSqlRawAsync($"DELETE FROM osu_web_score_links WHERE ScoreId IN ({list})", cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore EF1002
        }

        foreach (var chunk in linked.Chunk(500))
            await _osuWeb.Scores.Where(s => chunk.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return linked;
    }

    /// <summary>
    /// Brings the website copies of <paramref name="scores"/> in line after their pp or their map's
    /// status changed: <c>ranked</c> follows the map's current <c>approved</c>, pp the TomYum score.
    /// Scores not on the website yet are copied. Returns the osu-web ids written, to queue for the
    /// search index.
    /// </summary>
    public async Task<IReadOnlyList<ulong>> RefreshScoresAsync(IReadOnlyCollection<Score> scores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var written = new List<ulong>();
        foreach (var score in scores.Where(s => OsuWebScores.IsMirrored(s.Mode)))
        {
            var linked = await _tomyum.Database
                .SqlQuery<ulong>($"SELECT OsuWebScoreId AS Value FROM osu_web_score_links WHERE ScoreId = {score.Id}")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (linked.Count == 0)
            {
                if (await WriteScoreAsync(score, cancellationToken).ConfigureAwait(false) is { } id)
                    written.Add(id);
                continue;
            }

            var row = await _osuWeb.Scores.FirstOrDefaultAsync(s => s.Id == linked[0], cancellationToken).ConfigureAwait(false);
            var approved = await _osuWeb.Beatmaps.AsNoTracking().Where(b => b.BeatmapId == (uint)score.BeatmapId)
                .Select(b => (sbyte?)b.Approved).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null || approved is null)
                continue;

            row.Ranked = score.Passed && approved > 0;
            row.Pp = row.Ranked && score.Pp > 0 ? (float)score.Pp : null;
            written.Add(row.Id);
        }

        await _osuWeb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _osuWeb.ChangeTracker.Clear();
        return written;
    }
}
