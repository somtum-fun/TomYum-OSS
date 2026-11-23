// TomYum: a player's global rank (rank_score_index) the way TomYum and the website work it out: only
// visible players count (not restricted or banned), and with the rankings' inactivity rule
// (TOMYUM_RANKING_INACTIVE_DAYS, TomYum's Ranking:InactiveDays) a player without a score in the mode
// for that many days has no rank (0) and doesn't count towards anyone else's. ppy's processor counted
// every row, so a play briefly put the player back among inactive players (and TomYum's next pass moved
// them again). Staff can set the rule in the website's admin panel (tomyum_site_settings ranking_inactive_days,
// and ranking_inactive_mode: "grey" keeps inactive players ranked), read here every minute; without it the env
// value stands. Built into ppy's score processor by deploy/score-processor/Dockerfile.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class TomYumRankIndex
    {
        private static readonly int env_inactive_days =
            int.TryParse(Environment.GetEnvironmentVariable("TOMYUM_RANKING_INACTIVE_DAYS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0 ? days : 0;

        // Shared by the processor's threads: an int and a long (ticks), each read and written whole.
        private static volatile int settingDays = env_inactive_days;
        private static long settingReadTicks;

        /// <summary>The days after which a player is off the rankings (0: never), from the admin panel's settings or the env.</summary>
        private static async Task<int> inactiveDaysAsync(MySqlConnection connection, MySqlTransaction? transaction)
        {
            if (DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref settingReadTicks) < TimeSpan.TicksPerMinute)
                return settingDays;
            // One thread reads the settings; the others keep the last value meanwhile.
            Interlocked.Exchange(ref settingReadTicks, DateTimeOffset.UtcNow.UtcTicks);

            try
            {
                var rows = (await connection.QueryAsync<(string name, string? value)>(
                    "SELECT name, value FROM tomyum_site_settings WHERE name IN ('ranking_inactive_days', 'ranking_inactive_mode')",
                    transaction: transaction)).ToDictionary(r => r.name, r => r.value);

                if (string.Equals(rows.GetValueOrDefault("ranking_inactive_mode")?.Trim(), "grey", StringComparison.OrdinalIgnoreCase))
                    settingDays = 0;
                else
                    settingDays = int.TryParse(rows.GetValueOrDefault("ranking_inactive_days")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stored) && stored >= 0
                        ? stored
                        : env_inactive_days;
            }
            catch (MySqlException)
            {
                // No settings table (or no access): the env value, or the last one read.
            }

            return settingDays;
        }

        public static async Task<int> GetAsync(UserStats userStats, string statsTable, MySqlConnection connection, MySqlTransaction? transaction)
        {
            if (userStats.rank_score <= 0)
                return 0;

            int inactive_days = await inactiveDaysAsync(connection, transaction);

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
