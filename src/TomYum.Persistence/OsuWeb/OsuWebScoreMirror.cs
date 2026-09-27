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

    private readonly TomYumDbContext _tomyum;
    private readonly OsuWebDbContext _osuWeb;

    public OsuWebScoreMirror(TomYumDbContext tomyum, OsuWebDbContext osuWeb)
    {
        _tomyum = tomyum;
        _osuWeb = osuWeb;
    }

    /// <summary>Creates TomYum's link table if missing (idempotent; run at startup).</summary>
    public Task EnsureLinkTableAsync(CancellationToken cancellationToken = default) =>
        _tomyum.Database.ExecuteSqlRawAsync(LinkTableDdl, cancellationToken);

    /// <summary>
    /// Writes <paramref name="score"/> to osu-web's <c>scores</c>. Returns the osu-web score id, or
    /// <c>null</c> when the score isn't mirrored (relax/autopilot, or its map isn't on osu-web).
    /// Writing the same score again returns the existing id.
    /// </summary>
    public async Task<ulong?> WriteScoreAsync(Score score, CancellationToken cancellationToken = default)
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
}
