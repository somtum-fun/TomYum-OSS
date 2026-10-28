// TomYum: lazer builds from before 2024 don't send a score's total without mods, which ppy's ScoreMultiplierValidator needs
// (it re-applies the mods' multiplier from it), so it refused their scores: no pp, no stats. Their total is the score
// without mods times osu!'s multiplier for the mods (what the client applied), so that's worked back out here, kept on
// the score, and processing goes on. Built into ppy's score processor by deploy/score-processor/Dockerfile.

using System;
using System.Linq;
using Dapper;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class TomYumOldLazer
    {
        /// <summary>The score's total without mods, worked back out from its total and stored on the score (its data's total_score_without_mods).</summary>
        public static long TotalScoreWithoutMods(SoloScore score, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);
            double multiplier = ruleset.CreateScoreMultiplierCalculator(new ScoreMultiplierContext(score.beatmap!.GetLegacyBeatmapConversionDifficultyInfo()))
                                       .CalculateFor(score.ScoreData.Mods.Select(m => m.ToMod(ruleset)));
            long withoutMods = multiplier > 0 ? (long)Math.Round(score.total_score / multiplier) : score.total_score;

            score.ScoreData.TotalScoreWithoutMods = withoutMods;
            conn.Execute("UPDATE `scores` SET `data` = JSON_SET(`data`, '$.total_score_without_mods', @withoutMods) WHERE `id` = @id",
                new { withoutMods, id = score.id }, transaction);

            Console.WriteLine($"Score {score.id}: no total score without mods (a pre-2024 lazer build); worked out as {withoutMods} (x{multiplier:0.###})");
            return withoutMods;
        }
    }
}
