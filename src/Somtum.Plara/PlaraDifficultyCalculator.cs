// Pla-ra: star rating and pp for custom lazer rulesets. Keep this file the same in TomYum (src/Somtum.Plara) and
// TumThai (osu.Game/Rulesets/Plara): the game's numbers must be the server's.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Scoring;
using osu.Game.Utils;

namespace Somtum.Plara;

/// <summary>Pla-ra's stars of a map in a ruleset, and what they're made of.</summary>
public class PlaraDifficultyAttributes : DifficultyAttributes
{
    public double Density { get; set; }

    public double Stamina { get; set; }

    public double Rhythm { get; set; }

    /// <summary>The map's judged notes (basic judgements, nested ones too) in the ruleset.</summary>
    public int ObjectCount { get; set; }
}

/// <summary>
/// Pla-ra's timing model, for a ruleset whose own difficulty calculator is a placeholder. It rates the map after
/// the ruleset has converted it (with the play's mods), from what every ruleset has: its judged notes' times,
/// chords (notes within 15 ms) and holds. Three strain skills, as osu!'s own are built: Density (bursts), Stamina
/// (long streams) and Rhythm (gaps that change by awkward ratios).
/// </summary>
public class PlaraDifficultyCalculator : DifficultyCalculator
{
    private const double chord_window = 15;
    private const double hold_min_duration = 100;

    private readonly double scale;

    public PlaraDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap, double scale)
        : base(ruleset, beatmap)
    {
        this.scale = scale;
    }

    public override int Version => PlaraProfiles.VERSION;

    /// <summary>Every judged note (nested ones too) whose best result is a basic judgement, in time order.</summary>
    public static List<HitObject> JudgedObjects(IEnumerable<HitObject> hitObjects)
    {
        var list = new List<HitObject>();

        void add(HitObject h)
        {
            if (h.Judgement.MaxResult.IsBasic())
                list.Add(h);
            foreach (var nested in h.NestedHitObjects)
                add(nested);
        }

        foreach (var h in hitObjects)
            add(h);

        return list.OrderBy(h => h.StartTime).ToList();
    }

    /// <summary>How many judgements of the map affect combo.</summary>
    public static int MaxCombo(IEnumerable<HitObject> hitObjects)
    {
        int combo = 0;

        void add(HitObject h)
        {
            if (h.Judgement.MaxResult.AffectsCombo())
                combo++;
            foreach (var nested in h.NestedHitObjects)
                add(nested);
        }

        foreach (var h in hitObjects)
            add(h);

        return combo;
    }

    protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
    {
        double clockRate = ModUtils.CalculateRateWithMods(mods);

        var chords = new List<List<HitObject>>();
        foreach (var h in JudgedObjects(beatmap.HitObjects))
        {
            if (chords.Count > 0 && h.StartTime - chords[^1][0].StartTime < chord_window)
                chords[^1].Add(h);
            else
                chords.Add(new List<HitObject> { h });
        }

        // Holds by start time, to count the ones still held at each note.
        var holds = beatmap.HitObjects.Where(h => h is IHasDuration d && d.Duration >= hold_min_duration).OrderBy(h => h.StartTime).ToList();
        var held = new List<HitObject>();
        int nextHold = 0;

        var objects = new List<DifficultyHitObject>();
        for (int i = 1; i < chords.Count; i++)
        {
            double start = chords[i][0].StartTime;
            while (nextHold < holds.Count && holds[nextHold].StartTime < start - chord_window)
                held.Add(holds[nextHold++]);
            held.RemoveAll(h => h.GetEndTime() <= start);

            objects.Add(new PlaraHitObject(chords[i][0], chords[i - 1][0], clockRate, objects, objects.Count, chords[i].Count, held.Count));
        }

        return objects;
    }

    protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods) =>
        new Skill[] { new PlaraDensity(mods), new PlaraStamina(mods), new PlaraRhythm(mods) };

    protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
    {
        double density = Math.Sqrt(skills[0].DifficultyValue());
        double stamina = Math.Sqrt(skills[1].DifficultyValue());
        double rhythm = Math.Sqrt(skills[2].DifficultyValue());
        double raw = Math.Pow(Math.Pow(density, 1.1) + Math.Pow(stamina, 1.1) + Math.Pow(rhythm, 1.1), 1 / 1.1);

        return new PlaraDifficultyAttributes
        {
            Mods = mods,
            StarRating = scale * raw,
            Density = density,
            Stamina = stamina,
            Rhythm = rhythm,
            ObjectCount = JudgedObjects(beatmap.HitObjects).Count,
            MaxCombo = MaxCombo(beatmap.HitObjects),
        };
    }
}

/// <summary>A chord of judged notes (one note, most of the time), with how many holds are held through it.</summary>
public class PlaraHitObject : DifficultyHitObject
{
    public int ChordSize { get; }

    public int ActiveHolds { get; }

    public PlaraHitObject(HitObject hitObject, HitObject lastObject, double clockRate, List<DifficultyHitObject> objects, int index, int chordSize, int activeHolds)
        : base(hitObject, lastObject, clockRate, objects, index)
    {
        ChordSize = chordSize;
        ActiveHolds = activeHolds;
    }

    /// <summary>How hard the chord is to hit on time: how soon it comes, how many notes are in it, how many holds are held.</summary>
    public double Load => (1 + 0.3 * (ChordSize - 1)) * (1 + 0.2 * ActiveHolds) * 1000 / Math.Max(DeltaTime, 40);
}

/// <summary>Bursts: decays fast, so only the densest moments count.</summary>
public class PlaraDensity : StrainDecaySkill
{
    public PlaraDensity(Mod[] mods)
        : base(mods)
    {
    }

    protected override double SkillMultiplier => 1;
    protected override double StrainDecayBase => 0.3;
    protected override double StrainValueOf(DifficultyHitObject current) => ((PlaraHitObject)current).Load;
}

/// <summary>Long streams: decays slowly, so it builds over sustained sections.</summary>
public class PlaraStamina : StrainDecaySkill
{
    public PlaraStamina(Mod[] mods)
        : base(mods)
    {
    }

    protected override double SkillMultiplier => 0.06;
    protected override double StrainDecayBase => 0.85;
    protected override double StrainValueOf(DifficultyHitObject current) => ((PlaraHitObject)current).Load;
}

/// <summary>Rhythm changes: nothing for gaps that stay the same, double or halve; most for awkward ratios (1.5x, 0.75x).</summary>
public class PlaraRhythm : StrainDecaySkill
{
    public PlaraRhythm(Mod[] mods)
        : base(mods)
    {
    }

    protected override double SkillMultiplier => 3;
    protected override double StrainDecayBase => 0.4;

    protected override double StrainValueOf(DifficultyHitObject current)
    {
        if (current.Index == 0)
            return 0;

        double ratio = Math.Max(current.DeltaTime, 1) / Math.Max(current.Previous(0).DeltaTime, 1);
        double s = Math.Sin(Math.PI * Math.Log2(ratio));
        return s * s;
    }
}
