using System.IO;

using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Rulesets.Taiko;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace TomYum.Performance;

/// <summary>A stable score as the website (and lazer) store it.</summary>
/// <param name="TotalScore">The standardised score (lazer's <c>total_score</c>).</param>
/// <param name="DataJson">The <c>data</c> column: mods (with Classic), statistics and maximum statistics.</param>
/// <param name="Accuracy">Accuracy, 0-1.</param>
public sealed record StandardisedLegacyScore(long TotalScore, string DataJson, double Accuracy);

/// <summary>
/// Converts stable scores to lazer's standardised scoring exactly the way ppy imports stable
/// scores into the website (<c>osu-queue-score-statistics</c> BatchInserter): Classic mod added,
/// maximum statistics padded with <c>legacy_combo_increase</c> up to the map's max combo, and
/// <c>StandardisedScoreMigrationTools.UpdateFromLegacy</c> with the map's legacy scoring
/// attributes, which ppy keeps in a table and this simulates from the <c>.osu</c> file.
/// </summary>
public sealed class LegacyScoreConverter
{
    private readonly OsuFileCache _files;
    private readonly IMemoryCache _cache;

    public LegacyScoreConverter(OsuFileCache files, IMemoryCache cache)
    {
        _files = files;
        _cache = cache;
    }

    /// <summary>Converts a stable score; null when the map's file can't be had.</summary>
    public async Task<StandardisedLegacyScore?> ConvertAsync(PerformanceRequest request, long legacyTotalScore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ruleset = CreateRuleset(request.RulesetId);
        var map = await GetMapAsync(request.BeatmapId, ruleset, cancellationToken).ConfigureAwait(false);
        if (map is null)
            return null;

        var score = new ScoreInfo
        {
            Ruleset = ruleset.RulesetInfo,
            Mods = ruleset.ConvertFromLegacyMods((LegacyMods)request.Mods).Append(ruleset.CreateMod<ModClassic>()!).ToArray(),
            Statistics = new Dictionary<HitResult, int>(),
            MaximumStatistics = new Dictionary<HitResult, int>(),
            MaxCombo = request.MaxCombo,
            LegacyTotalScore = legacyTotalScore,
            IsLegacyScore = true,
        };
        score.SetCount50(request.Count50);
        score.SetCount100(request.Count100);
        score.SetCount300(request.Count300);
        score.SetCountMiss(request.CountMiss);
        score.SetCountGeki(request.CountGeki);
        score.SetCountKatu(request.CountKatu);
        score.Statistics = score.Statistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        HitResult maxBasic;
        using (var scoreProcessor = ruleset.CreateScoreProcessor())
            maxBasic = ruleset.GetHitResultsForDisplay().Where(h => h.result.IsBasic()).Select(h => h.result).MaxBy(scoreProcessor.GetBaseScoreForResult);
        foreach (var (result, count) in score.Statistics)
        {
            switch (result)
            {
                case HitResult.LargeTickHit:
                case HitResult.LargeTickMiss:
                    score.MaximumStatistics[HitResult.LargeTickHit] = score.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit) + count;
                    break;

                case HitResult.SmallTickHit:
                case HitResult.SmallTickMiss:
                    score.MaximumStatistics[HitResult.SmallTickHit] = score.MaximumStatistics.GetValueOrDefault(HitResult.SmallTickHit) + count;
                    break;

                case HitResult.IgnoreHit:
                case HitResult.IgnoreMiss:
                case HitResult.SmallBonus:
                case HitResult.LargeBonus:
                    break;

                default:
                    score.MaximumStatistics[maxBasic] = score.MaximumStatistics.GetValueOrDefault(maxBasic) + count;
                    break;
            }
        }

        // Some judgements affect combo without being stored: pad up to the map's max combo.
        var comboFromStatistics = score.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Sum(kvp => kvp.Value);
#pragma warning disable CS0618 // LegacyComboIncrease is obsolete, and exactly what ppy stores for stable scores
        if (map.Attributes.MaxCombo > comboFromStatistics)
            score.MaximumStatistics[HitResult.LegacyComboIncrease] = map.Attributes.MaxCombo - comboFromStatistics;
#pragma warning restore CS0618

        StandardisedScoreMigrationTools.UpdateFromLegacy(score, ruleset, map.Difficulty, map.Attributes);

        var data = JsonConvert.SerializeObject(new ScoreData
        {
            Mods = score.Mods.Select(m => new APIMod(m)).ToArray(),
            Statistics = score.Statistics,
            MaximumStatistics = score.MaximumStatistics,
        }, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });

        return new StandardisedLegacyScore(score.TotalScore, data, score.Accuracy);
    }

    /// <summary>The website's <c>data</c> column, as ppy's processor writes it (its SoloScoreData).</summary>
    private sealed class ScoreData
    {
        [JsonProperty("mods")]
        public APIMod[] Mods { get; set; } = [];

        [JsonProperty("statistics")]
        public Dictionary<HitResult, int> Statistics { get; set; } = [];

        [JsonProperty("maximum_statistics")]
        public Dictionary<HitResult, int> MaximumStatistics { get; set; } = [];
    }

    /// <summary>A map's legacy scoring attributes and conversion info, cached like difficulty attributes.</summary>
    private sealed record MapScoring(LegacyScoreAttributes Attributes, LegacyBeatmapConversionDifficultyInfo Difficulty);

    private async Task<MapScoring?> GetMapAsync(int beatmapId, Ruleset ruleset, CancellationToken cancellationToken) =>
        await _cache.GetOrCreateAsync($"legacy-scoring:{beatmapId}:{ruleset.RulesetInfo.OnlineID}", async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            var bytes = await _files.GetAsync(beatmapId, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
                return null;

            using var stream = new MemoryStream(bytes, writable: false);
            var working = new OfficialPerformanceCalculator.StreamedWorkingBeatmap(stream, ruleset);
            var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, []);
            var attributes = ((ILegacyRuleset)ruleset).CreateLegacyScoreSimulator().Simulate(working, playable);
            return new MapScoring(attributes, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(playable));
        }).ConfigureAwait(false);

    private static Ruleset CreateRuleset(int id) => id switch
    {
        0 => new OsuRuleset(),
        1 => new TaikoRuleset(),
        2 => new CatchRuleset(),
        3 => new ManiaRuleset(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported ruleset id"),
    };
}
