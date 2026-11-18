// Pla-ra: star rating and pp for custom lazer rulesets. Keep this file the same in TomYum (src/Somtum.Plara) and
// TumThai (osu.Game/Rulesets/Plara): the game's numbers must be the server's.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace Somtum.Plara;

/// <summary>A play's Pla-ra pp. <see cref="PerformanceAttributes.Total"/> is 0 when the play gives none (<see cref="HasPp"/>).</summary>
public class PlaraPerformanceAttributes : PerformanceAttributes
{
    /// <summary>False when a mod (or the ruleset) gives no pp: the play counts by score only.</summary>
    public bool HasPp { get; set; }
}

/// <summary>
/// One pp formula for every custom ruleset, on its stars (Pla-ra's or its own):
/// <c>pp = 1.2 · ★³ · acc⁵ · (1 − misses/N)⁴ · length · mods</c>. N is the map's judged notes. The miss penalty is the
/// share of notes missed, not osu!'s per-miss curve (which takes nearly every play in these rulesets to 0).
/// </summary>
public class PlaraPerformanceCalculator : PerformanceCalculator
{
    public const double K = 1.2;

    private readonly PlaraProfile profile;

    public PlaraPerformanceCalculator(Ruleset ruleset, PlaraProfile profile)
        : base(ruleset)
    {
        this.profile = profile;
    }

    /// <summary>
    /// The pp of a play: null when one of its mods (or the ruleset) gives none. Pass only plays that passed.
    /// </summary>
    /// <param name="profile">The ruleset's profile.</param>
    /// <param name="stars">The map's stars with the play's mods.</param>
    /// <param name="objects">The map's judged notes (basic judgements).</param>
    /// <param name="accuracy">The play's accuracy (0-1), as the ruleset worked it out.</param>
    /// <param name="misses">The play's misses (basic judgements).</param>
    /// <param name="mods">The play's mods' acronyms.</param>
    public static double? Calculate(PlaraProfile profile, double stars, int objects, double accuracy, int misses, IEnumerable<string> mods)
    {
        if (!profile.GivesPp)
            return null;

        double multiplier = 1;
        bool noFail = false;
        foreach (string acronym in mods)
        {
            if (!profile.ModMultipliers.TryGetValue(acronym, out double m))
                return null;
            multiplier *= m;
            noFail |= string.Equals(acronym, "NF", StringComparison.OrdinalIgnoreCase);
        }

        if (double.IsNaN(stars) || double.IsInfinity(stars) || stars <= 0 || objects <= 0)
            return 0;

        double missed = Math.Clamp(misses, 0, objects);
        double pp = K * Math.Pow(stars, 3);
        pp *= Math.Pow(Math.Clamp(accuracy, 0, 1), 5);
        pp *= Math.Pow(1 - missed / objects, 4);
        pp *= 0.95 + 0.4 * Math.Min(1, objects / 2000.0) + (objects > 2000 ? 0.5 * Math.Log10(objects / 2000.0) : 0);
        if (objects < 50)
            pp *= objects / 50.0;
        if (noFail)
            pp *= Math.Max(0.9, 1 - 0.02 * missed);

        return pp * multiplier;
    }

    /// <summary>A player's total: their best pp on each map, Σ pp_i · 0.95^i (no bonus for how many maps).</summary>
    public static double Total(IEnumerable<double> bestPerMap) =>
        bestPerMap.OrderByDescending(p => p).Select((p, i) => p * Math.Pow(0.95, i)).Sum();

    /// <summary>How many basic judgements are in <paramref name="statistics"/> (a play's maximum statistics: the map's judged notes).</summary>
    public static int BasicCount(IReadOnlyDictionary<HitResult, int> statistics) =>
        statistics.Where(s => s.Key.IsBasic()).Sum(s => s.Value);

    protected override PerformanceAttributes CreatePerformanceAttributes(ScoreInfo score, DifficultyAttributes attributes)
    {
        int objects = BasicCount(score.MaximumStatistics);
        if (objects == 0)
            objects = attributes is PlaraDifficultyAttributes plara ? plara.ObjectCount : BasicCount(score.Statistics);

        double? pp = Calculate(profile, attributes.StarRating, objects, score.Accuracy, score.Statistics.GetValueOrDefault(HitResult.Miss), score.Mods.Select(m => m.Acronym));

        return new PlaraPerformanceAttributes { Total = pp ?? 0, HasPp = pp != null };
    }
}

/// <summary>Pla-ra's calculators for a ruleset: what the game and the server ask for a custom ruleset's stars and pp.</summary>
public static class PlaraCalculators
{
    /// <summary>The profile for <paramref name="ruleset"/>, or null when Pla-ra doesn't rate it.</summary>
    public static PlaraProfile? ProfileFor(Ruleset ruleset) => PlaraProfiles.Find(ruleset.GetType().Assembly.GetName().Name);

    /// <summary>Pla-ra's difficulty calculator where the ruleset's own is a placeholder; the ruleset's own otherwise.</summary>
    public static DifficultyCalculator CreateDifficultyCalculator(Ruleset ruleset, IWorkingBeatmap beatmap)
    {
        var profile = ProfileFor(ruleset);
        return profile?.Source == PlaraStarSource.Plara
            ? new PlaraDifficultyCalculator(ruleset.RulesetInfo, beatmap, profile.Scale)
            : ruleset.CreateDifficultyCalculator(beatmap);
    }

    /// <summary>Pla-ra's pp for a custom ruleset it rates; the ruleset's own (or none) otherwise.</summary>
    public static PerformanceCalculator? CreatePerformanceCalculator(Ruleset ruleset)
    {
        var profile = ProfileFor(ruleset);
        return profile != null ? new PlaraPerformanceCalculator(ruleset, profile) : ruleset.CreatePerformanceCalculator();
    }
}
