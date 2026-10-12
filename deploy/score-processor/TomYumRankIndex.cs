// TomYum: a player's global rank (rank_score_index) the way TomYum and the website work it out: only
// visible players count (not restricted or banned), and with the rankings' inactivity rule
// (TOMYUM_RANKING_INACTIVE_DAYS, TomYum's Ranking:InactiveDays) a player without a score in the mode
// for that many days has no rank (0) and doesn't count towards anyone else's. ppy's processor counted
// every row, so a play briefly put the player back among inactive players (and TomYum's next pass moved
// them again). Built into ppy's score processor by deploy/score-processor/Dockerfile.

using System;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class TomYumRankIndex
    {
        private static readonly int inactive_days =
            int.TryParse(Environment.GetEnvironmentVariable("TOMYUM_RANKING_INACTIVE_DAYS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0 ? days : 0;

        public static async Task<int> GetAsync(UserStats userStats, string statsTable, MySqlConnection connection, MySqlTransaction? transaction)
        {
            if (userStats.rank_score <= 0)
                return 0;

            // The player's own row decides whether they're ranked (it holds this play's last_played).
            if (inactive_days > 0 && userStats.last_played < DateTimeOffset.UtcNow.AddDays(-inactive_days))
                return 0;

            string active = inactive_days > 0
                ? $" AND s.last_played >= UTC_TIMESTAMP() - INTERVAL {inactive_days.ToString(CultureInfo.InvariantCulture)} DAY"
                : string.Empty;

            int above = await connection.QuerySingleAsync<int>(
                $"SELECT COUNT(*) FROM {statsTable} s JOIN phpbb_users u ON u.user_id = s.user_id"
                + $" WHERE s.rank_score > @pp AND s.user_id <> @userId AND u.user_type <> 1 AND u.user_warnings = 0{active}",
                new { pp = userStats.rank_score, userId = userStats.user_id }, transaction: transaction);

            return above + 1;
        }
    }
}
