using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A week on the website, for the weekly recap: its best plays, busiest players and maps, and totals.</summary>
public sealed record WeekNumbers(
    IReadOnlyList<ScoreCard> TopPlays,
    IReadOnlyList<(int UserId, string Username, long Plays)> MostActive,
    IReadOnlyList<(int BeatmapId, int BeatmapsetId, string Name, long Plays)> MostPlayed,
    long Plays,
    long NewPlayers);

public sealed partial class OsuWebCommunity
{
    /// <summary>
    /// The week from <paramref name="from"/> to <paramref name="to"/> (UTC): the <paramref name="count"/> vanilla
    /// plays worth the most pp (one per player and map), the players with the most plays, the most played maps,
    /// and how many plays and new players there were. Restricted players left out.
    /// </summary>
    public async Task<WeekNumbers> GetWeekAsync(DateTime from, DateTime to, int count, CancellationToken cancellationToken = default)
    {
        var top = await QueryScoresAsync(
            "s.ended_at >= {0} AND s.ended_at < {1} AND s.passed = 1 AND s.preserve = 1 AND s.pp IS NOT NULL AND " + Visible + " AND " + NotRelaxOrAutopilot,
            "s.pp DESC",
            [from, to],
            200,
            cancellationToken).ConfigureAwait(false);
        var topPlays = top.GroupBy(s => (s.UserId, s.BeatmapId)).Select(g => g.First()).Take(count).ToList();

        // Only fixed fragments and the count go into the SQL; the dates go in as parameters.
        var activeSql = $$"""
            SELECT s.user_id AS UserId, u.username AS Username, COUNT(*) AS Plays
            FROM scores s JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE s.ended_at >= {0} AND s.ended_at < {1} AND {{Visible}}
            GROUP BY s.user_id, u.username ORDER BY Plays DESC LIMIT {{count}}
            """;
        var active = await _db.Database.SqlQueryRaw<ActiveRow>(activeSql, from, to).ToListAsync(cancellationToken).ConfigureAwait(false);

        var mapsSql = $$"""
            SELECT s.beatmap_id AS BeatmapId, COALESCE(b.beatmapset_id, 0) AS BeatmapsetId,
              CONCAT(COALESCE(bs.artist, ''), ' - ', COALESCE(bs.title, ''), ' [', COALESCE(b.version, ''), ']') AS Name, COUNT(*) AS Plays
            FROM scores s JOIN phpbb_users u ON u.user_id = s.user_id
              LEFT JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id LEFT JOIN osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id
            WHERE s.ended_at >= {0} AND s.ended_at < {1} AND {{Visible}}
            GROUP BY s.beatmap_id, b.beatmapset_id, bs.artist, bs.title, b.version ORDER BY Plays DESC LIMIT {{count}}
            """;
        var maps = await _db.Database.SqlQueryRaw<PlayedRow>(mapsSql, from, to).ToListAsync(cancellationToken).ConfigureAwait(false);

        var plays = await _db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM scores WHERE ended_at >= {0} AND ended_at < {1}", from, to)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var joined = await _db.Database.SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS Value FROM phpbb_users u WHERE {Visible} AND u.user_regdate >= {{0}} AND u.user_regdate < {{1}}",
                new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeSeconds(), new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeSeconds())
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        return new WeekNumbers(
            topPlays,
            active.Select(a => ((int)a.UserId, a.Username, a.Plays)).ToList(),
            maps.Select(m => ((int)m.BeatmapId, (int)m.BeatmapsetId, m.Name, m.Plays)).ToList(),
            plays,
            joined);
    }

    private sealed class ActiveRow
    {
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public long Plays { get; set; }
    }

    private sealed class PlayedRow
    {
        public uint BeatmapId { get; set; }
        public uint BeatmapsetId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Plays { get; set; }
    }
}
