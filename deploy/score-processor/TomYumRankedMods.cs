// TomYum: lazer mods this server counts as ranked (for pp and medals) on top of osu!'s own, picked in
// the website's admin panel (Admin panel > Mods); see TomYum's docs/lazer.md. Built into ppy's score
// processor by deploy/score-processor/Dockerfile.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Rulesets.Mods;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.UserTotals;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class TomYumRankedMods
    {
        private static readonly TimeSpan refresh_interval = TimeSpan.FromSeconds(30);
        private static readonly object load_lock = new object();

        // "ruleset:acronym" of the mods staff made ranked.
        private static HashSet<string> ranked = new HashSet<string>();
        private static DateTime loadedAt = DateTime.MinValue;

        /// <summary>
        /// Whether a mod counts as ranked here: osu! says so (for its settings), or staff made it
        /// ranked (whatever its settings).
        /// </summary>
        public static bool IsRanked(Mod mod)
        {
            if (mod.Ranked)
                return true;

            refreshIfStale();
            var current = ranked;
            int rulesetId = RulesetOf(mod);

            return rulesetId >= 0
                ? current.Contains(key(rulesetId, mod.Acronym))
                : current.Any(k => k.EndsWith(":" + mod.Acronym, StringComparison.Ordinal));
        }

        /// <summary>The legacy ruleset id a mod belongs to, from its namespace; -1 for shared mods.</summary>
        public static int RulesetOf(Mod mod)
        {
            string ns = mod.GetType().Namespace ?? string.Empty;

            if (ns.StartsWith("osu.Game.Rulesets.Osu", StringComparison.Ordinal))
                return 0;
            if (ns.StartsWith("osu.Game.Rulesets.Taiko", StringComparison.Ordinal))
                return 1;
            if (ns.StartsWith("osu.Game.Rulesets.Catch", StringComparison.Ordinal))
                return 2;
            if (ns.StartsWith("osu.Game.Rulesets.Mania", StringComparison.Ordinal))
                return 3;

            return -1;
        }

        /// <summary>
        /// Makes the table, records which mods osu! ranks (for the panel), and starts watching for
        /// changes made there: scores with a mod that changed get their pp worked out again.
        /// </summary>
        public static void Start(CancellationToken cancellationToken)
        {
            try
            {
                setUp();
            }
            catch (Exception e)
            {
                Console.WriteLine($"TomYum ranked mods: setting up failed ({e.Message})");
            }

            Task.Run(() => watchAsync(cancellationToken), cancellationToken);
        }

        private static void setUp()
        {
            using var db = DatabaseAccess.GetConnection();

            db.Execute(
                "CREATE TABLE IF NOT EXISTS `tomyum_ranked_mods` ("
                + "`ruleset_id` smallint NOT NULL,"
                + "`acronym` varchar(8) NOT NULL,"
                // What osu! says (with the mod's default settings); written here.
                + "`osu_ranked` tinyint(1) NOT NULL DEFAULT 0,"
                // Staff: ranked here, whatever its settings; written by the website.
                + "`ranked` tinyint(1) NOT NULL DEFAULT 0,"
                // The `ranked` value old scores were last worked out for.
                + "`applied` tinyint(1) NOT NULL DEFAULT 0,"
                + "PRIMARY KEY (`ruleset_id`, `acronym`)"
                + ") CHARACTER SET utf8mb4");

            var rows = new List<object>();

            for (int rulesetId = 0; rulesetId < 4; rulesetId++)
            {
                foreach (var mod in LegacyRulesetHelper.GetRulesetFromLegacyId(rulesetId).CreateAllMods())
                    rows.Add(new { ruleset_id = rulesetId, acronym = mod.Acronym, osu_ranked = mod.Ranked });
            }

            db.Execute(
                "INSERT INTO `tomyum_ranked_mods` (`ruleset_id`, `acronym`, `osu_ranked`) VALUES (@ruleset_id, @acronym, @osu_ranked) "
                + "ON DUPLICATE KEY UPDATE `osu_ranked` = VALUES(`osu_ranked`)", rows);

            reload();
        }

        private static void refreshIfStale()
        {
            if (DateTime.UtcNow - loadedAt < refresh_interval)
                return;

            lock (load_lock)
            {
                if (DateTime.UtcNow - loadedAt < refresh_interval)
                    return;

                try
                {
                    reload();
                }
                catch (Exception e)
                {
                    // Keep the last list; try again later.
                    Console.WriteLine($"TomYum ranked mods: loading failed ({e.Message})");
                    loadedAt = DateTime.UtcNow;
                }
            }
        }

        private static void reload()
        {
            using var db = DatabaseAccess.GetConnection();
            ranked = db.Query<string>("SELECT CONCAT(`ruleset_id`, ':', `acronym`) FROM `tomyum_ranked_mods` WHERE `ranked` = 1").ToHashSet();
            loadedAt = DateTime.UtcNow;
        }

        private static string key(int rulesetId, string acronym) => $"{rulesetId}:{acronym}";

        private static async Task watchAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await applyChangesAsync(cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.WriteLine($"TomYum ranked mods: recalculating failed ({e})");
                }

                try
                {
                    await Task.Delay(refresh_interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private class Change
        {
            public long RulesetId { get; set; }
            public string Acronym { get; set; } = string.Empty;
            public bool Ranked { get; set; }
        }

        /// <summary>For each mod staff changed: the pp of lazer scores with it again, then their players' totals.</summary>
        private static async Task applyChangesAsync(CancellationToken cancellationToken)
        {
            List<Change> changes;

            using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                changes = (await db.QueryAsync<Change>(
                    "SELECT CAST(`ruleset_id` AS SIGNED) AS RulesetId, `acronym` AS Acronym, `ranked` AS Ranked FROM `tomyum_ranked_mods` WHERE `ranked` <> `applied`")).ToList();
            }

            if (changes.Count == 0)
                return;

            lock (load_lock)
                reload();

            foreach (var change in changes)
            {
                int rulesetId = (int)change.RulesetId;

                if (Regex.IsMatch(change.Acronym, "^[A-Z0-9]{1,8}$"))
                {
                    // Lazer scores only: stable ones (legacy_score_id) get their pp from TomYum.
                    string where = $"`ruleset_id` = {rulesetId} AND `legacy_score_id` IS NULL "
                                   + $"AND JSON_CONTAINS(JSON_EXTRACT(`data`, '$.mods[*].acronym'), '\"{change.Acronym}\"')";

                    ulong[] scoreIds;
                    uint[] userIds;

                    using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
                    {
                        scoreIds = (await db.QueryAsync<ulong>($"SELECT `id` FROM `scores` WHERE {where}", commandTimeout: 600)).ToArray();
                        userIds = (await db.QueryAsync<uint>($"SELECT DISTINCT `user_id` FROM `scores` WHERE {where}", commandTimeout: 600)).ToArray();

                        // From scratch: scores that no longer count lose their pp, the rest get it (again).
                        if (scoreIds.Length > 0)
                            await db.ExecuteAsync($"UPDATE `scores` SET `pp` = NULL WHERE {where}", commandTimeout: 600);
                    }

                    Console.WriteLine($"TomYum ranked mods: {change.Acronym} (ruleset {rulesetId}) is now {(change.Ranked ? "ranked" : "unranked")}; "
                                      + $"working out {scoreIds.Length} score(s) of {userIds.Length} player(s) again");

                    if (scoreIds.Length > 0)
                    {
                        await new UpdateAllScoresCommand { Where = where }.OnExecuteAsync(cancellationToken);
                        new ElasticQueuePusher().PushToQueue(scoreIds.Select(id => new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long)id }).ToList());
                        await new UpdateAllUserTotalsCommand { RulesetId = rulesetId, Where = $"`user_id` IN ({string.Join(',', userIds)})" }
                            .OnExecuteAsync(cancellationToken);
                    }
                }

                using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
                {
                    await db.ExecuteAsync("UPDATE `tomyum_ranked_mods` SET `applied` = @Ranked WHERE `ruleset_id` = @RulesetId AND `acronym` = @Acronym", change);
                }
            }
        }
    }
}
