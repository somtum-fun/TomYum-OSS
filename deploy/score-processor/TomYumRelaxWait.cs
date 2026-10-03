// TomYum: a lazer Relax/Autopilot play counts in TomYum's relax/autopilot statistics, which TomYum
// takes in from the website's scores (LazerRelaxImport) on its own. The game asks for the player's
// statistics as soon as this processor says the score is processed, so such a score waits here
// until TomYum has taken it in: then the results screen shows the play counted, not the totals
// from before it. Built into ppy's score processor by deploy/score-processor/Dockerfile.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class TomYumRelaxWait
    {
        // TomYum's mark: the last website score its import has looked at (osu_counts).
        private const string mark_name = "tomyum_lazer_relax_mark";

        // TomYum looks every second; past this, the score goes on without it (TomYum may be down).
        private static readonly TimeSpan max_wait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan poll = TimeSpan.FromMilliseconds(200);

        public static void WaitForTomYum(MySqlConnection conn, SoloScore score)
        {
            if (!IsRelaxOrAutopilot(score))
                return;

            var waited = Stopwatch.StartNew();

            while (waited.Elapsed < max_wait)
            {
                ulong? mark = conn.QueryFirstOrDefault<ulong?>("SELECT `count` FROM osu_counts WHERE name = @Name", new { Name = mark_name });

                // No mark: TomYum's import isn't running here.
                if (mark == null || mark >= score.id)
                    return;

                Thread.Sleep(poll);
            }

            Console.WriteLine($"TomYum: score {score.id} wasn't taken into relax/autopilot within {max_wait.TotalSeconds}s; processing it anyway.");
        }

        /// <summary>The plays TomYum takes in: lazer RX in osu!, taiko or catch, AP in osu!.</summary>
        public static bool IsRelaxOrAutopilot(SoloScore score)
        {
            if (score.legacy_score_id != null || score.ruleset_id > 2)
                return false;

            return score.ScoreData.Mods.Any(m => m.Acronym == "RX" || (m.Acronym == "AP" && score.ruleset_id == 0));
        }
    }
}
