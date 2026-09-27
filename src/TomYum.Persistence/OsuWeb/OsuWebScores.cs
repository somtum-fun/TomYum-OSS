using System.Text.Json;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Translation rules from TomYum's stable scores to osu-web's <c>scores</c> rows and
/// <c>osu_user_stats*</c> tables. Free of I/O so the rules are unit-testable;
/// <see cref="OsuWebScoreMirror"/> applies them.
/// </summary>
public static class OsuWebScores
{
    // osu-web App\Singletons\Mods::LEGACY_BITSET (the stable mod bits osu-web knows).
    private static readonly (int Bit, string Acronym)[] LegacyBitset =
    [
        (1 << 0, "NF"), (1 << 1, "EZ"), (1 << 2, "TD"), (1 << 3, "HD"), (1 << 4, "HR"),
        (1 << 5, "SD"), (1 << 6, "DT"), (1 << 7, "RX"), (1 << 8, "HT"), (1 << 9, "NC"),
        (1 << 10, "FL"), (1 << 12, "SO"), (1 << 13, "AP"), (1 << 14, "PF"), (1 << 20, "FI"),
        (1 << 21, "RD"), (1 << 29, "V2"), (1 << 30, "MR"),
        (1 << 15, "4K"), (1 << 16, "5K"), (1 << 17, "6K"), (1 << 18, "7K"), (1 << 19, "8K"), (1 << 24, "9K"),
    ];

    // osu-web Mods::IMPLIED_MODS: stable sets both bits, lazer stores only the implying mod.
    private static readonly (string Mod, string Implied)[] ImpliedMods = [("NC", "DT"), ("PF", "SD"), ("DC", "HT")];

    /// <summary>Stats table per vanilla ruleset (osu-web <c>UserStatistics\*</c> models).</summary>
    private static readonly string[] StatsTables = ["osu_user_stats", "osu_user_stats_taiko", "osu_user_stats_fruits", "osu_user_stats_mania"];

    /// <summary>
    /// Whether a TomYum game mode goes to osu-web. osu-web only has the four vanilla rulesets;
    /// relax/autopilot boards (<see cref="GameMode.RelaxOffset"/>+) stay TomYum-only.
    /// </summary>
    public static bool IsMirrored(int gameMode) => gameMode is >= 0 and < GameMode.RelaxOffset;

    /// <summary>The <c>osu_user_stats*</c> table for a vanilla ruleset.</summary>
    public static string StatsTable(int mode) =>
        IsMirrored(mode) ? StatsTables[mode] : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only vanilla rulesets 0-3 have osu-web stats.");

    /// <summary>
    /// Lazer mod acronyms for a stable mod bitmask, as osu-web's <c>Mods::bitsetToIds</c> produces
    /// them, plus <c>CL</c> (Classic), which marks every legacy score on osu-web.
    /// </summary>
    public static IReadOnlyList<string> ModAcronyms(int legacyMods)
    {
        var acronyms = LegacyBitset.Where(m => (legacyMods & m.Bit) != 0).Select(m => m.Acronym).ToList();
        foreach (var (mod, implied) in ImpliedMods)
        {
            if (acronyms.Contains(mod))
                acronyms.Remove(implied);
        }
        acronyms.Add("CL");
        return acronyms;
    }

    /// <summary>
    /// osu-web's per-ruleset hit statistics for a stable score (the inverse of osu-web's
    /// <c>Score::makeLegacyEntry</c>), and the maximum statistics a perfect play would have.
    /// Zero counts are left out, as osu-web does.
    /// </summary>
    public static (Dictionary<string, int> Statistics, Dictionary<string, int> Maximum) HitStatistics(Score score)
    {
        ArgumentNullException.ThrowIfNull(score);

        Dictionary<string, int> statistics;
        Dictionary<string, int> maximum;
        switch (GameMode.AsVanilla(score.Mode))
        {
            case 1: // taiko
                statistics = new() { ["great"] = score.Count300, ["ok"] = score.Count100, ["miss"] = score.CountMiss };
                maximum = new() { ["great"] = score.Count300 + score.Count100 + score.CountMiss };
                break;
            case 2: // catch
                statistics = new()
                {
                    ["great"] = score.Count300,
                    ["large_tick_hit"] = score.Count100,
                    ["small_tick_hit"] = score.Count50,
                    ["small_tick_miss"] = score.CountKatu,
                    ["miss"] = score.CountMiss,
                };
                maximum = new()
                {
                    ["great"] = score.Count300 + score.CountMiss,
                    ["large_tick_hit"] = score.Count100,
                    ["small_tick_hit"] = score.Count50 + score.CountKatu,
                };
                break;
            case 3: // mania
                statistics = new()
                {
                    ["perfect"] = score.CountGeki,
                    ["great"] = score.Count300,
                    ["good"] = score.CountKatu,
                    ["ok"] = score.Count100,
                    ["meh"] = score.Count50,
                    ["miss"] = score.CountMiss,
                };
                maximum = new()
                {
                    ["perfect"] = score.CountGeki + score.Count300 + score.CountKatu + score.Count100 + score.Count50 + score.CountMiss,
                };
                break;
            default: // osu!
                statistics = new() { ["great"] = score.Count300, ["ok"] = score.Count100, ["meh"] = score.Count50, ["miss"] = score.CountMiss };
                maximum = new() { ["great"] = score.Count300 + score.Count100 + score.Count50 + score.CountMiss };
                break;
        }

        return (WithoutZeros(statistics), WithoutZeros(maximum));
    }

    /// <summary>The JSON for osu-web's <c>scores.data</c> column.</summary>
    public static string DataJson(Score score)
    {
        ArgumentNullException.ThrowIfNull(score);

        var (statistics, maximum) = HitStatistics(score);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mods"] = ModAcronyms(score.Mods).Select(a => new Dictionary<string, string> { ["acronym"] = a }).ToList(),
            // osu-web never stores an empty statistics object (ScoreDataStatistics::jsonSerialize).
            ["statistics"] = statistics.Count > 0 ? statistics : new Dictionary<string, int> { ["miss"] = 0 },
            ["maximum_statistics"] = maximum,
        });
    }

    /// <summary>
    /// osu! level for a total score (the game's formula): level n needs
    /// 5000/3·(4n³−3n²−n) + 1.25·1.8^(n−60) up to 100, then 26,931,190,827 + 99,999,999,999·(n−100).
    /// The fraction is progress towards the next level.
    /// </summary>
    public static double Level(long totalScore)
    {
        if (totalScore <= 0)
            return 1;

        var level = 1;
        while (RequiredScore(level + 1) <= totalScore)
            level++;

        var current = RequiredScore(level);
        var next = RequiredScore(level + 1);
        return level + (totalScore - current) / (next - current);
    }

    private static double RequiredScore(int level) => level <= 100
        ? 5000.0 / 3 * (4.0 * level * level * level - 3.0 * level * level - level) + 1.25 * Math.Pow(1.8, level - 60)
        : 26_931_190_827.0 + 99_999_999_999.0 * (level - 100);

    private static Dictionary<string, int> WithoutZeros(Dictionary<string, int> values) =>
        values.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// A player's total pp and accuracy (percent) from their scores, as ppy's
    /// <c>UserTotalPerformanceAggregateHelper</c> computes it: the best pp per beatmap (from any
    /// client), weighted by 0.95 per place, plus bonus pp for the number of scores (before
    /// de-duplication); accuracy weighted the same way and normalised. <paramref name="scores"/> are
    /// the player's preserved, ranked scores with pp, best first, at most 1000.
    /// </summary>
    public static (double Pp, double Accuracy) TotalPerformance(IReadOnlyList<(uint BeatmapId, double Pp, double Accuracy)> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var best = scores.GroupBy(s => s.BeatmapId)
            .Select(g => g.OrderByDescending(s => s.Pp).First())
            .OrderByDescending(s => s.Pp)
            .ToList();

        double factor = 1, pp = 0, accuracy = 0;
        foreach (var score in best)
        {
            pp += score.Pp * factor;
            accuracy += score.Accuracy * factor;
            factor *= 0.95;
        }

        pp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.995, scores.Count));
        if (best.Count > 0)
            accuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, best.Count)));

        return (Math.Max(0, pp), Math.Clamp(accuracy, 0, 100));
    }

    /// <summary>
    /// A queue item for ppy's score processor: <c>{"Score": row}</c> with the website row's columns,
    /// like osu-web's <c>Solo\Score::queueForProcessing</c> (<c>data</c> stays a JSON string).
    /// </summary>
    public static string ProcessingItem(OsuWebScore row, int? previousVersion = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        var item = new Dictionary<string, object?>
        {
            ["Score"] = new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["user_id"] = row.UserId,
                ["ruleset_id"] = row.RulesetId,
                ["beatmap_id"] = row.BeatmapId,
                ["has_replay"] = row.HasReplay,
                ["preserve"] = row.Preserve,
                ["ranked"] = row.Ranked,
                ["rank"] = row.Rank,
                ["passed"] = row.Passed,
                ["accuracy"] = row.Accuracy,
                ["max_combo"] = row.MaxCombo,
                ["total_score"] = row.TotalScore,
                ["data"] = row.Data,
                ["pp"] = row.Pp,
                ["legacy_score_id"] = row.LegacyScoreId,
                ["legacy_total_score"] = row.LegacyTotalScore,
                ["started_at"] = null,
                ["ended_at"] = new DateTimeOffset(DateTime.SpecifyKind(row.EndedAt, DateTimeKind.Utc)).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["build_id"] = null,
            },
        };
        if (previousVersion is { } version)
            item["ProcessHistory"] = new Dictionary<string, object> { ["score_id"] = row.Id, ["processed_version"] = version };
        return JsonSerializer.Serialize(item);
    }
}

/// <summary>Website options for the mirror (config section <c>OsuWeb</c>).</summary>
public sealed class OsuWebMirrorOptions
{
    /// <summary>
    /// ppy's score processor runs next to osu-web (lazer support, docs/lazer.md): it owns pp,
    /// accuracy, ranks and grade counts on the website for stable and lazer scores together, and
    /// TomYum adds only its stable counters.
    /// </summary>
    public bool ScoreProcessor { get; set; }
}

/// <summary>A player's total pp and accuracy (0-1) on the website.</summary>
public sealed record OsuWebPerformance(double Pp, float Accuracy);

/// <summary>
/// A stable score in lazer's scoring, as ppy stores stable scores on the website: the standardised
/// <c>total_score</c> and the <c>data</c> column (mods with Classic, statistics, maximum statistics
/// padded to the map's max combo). TomYum's pp engine computes it.
/// </summary>
public sealed record OsuWebStandardisedScore(long TotalScore, string DataJson);
