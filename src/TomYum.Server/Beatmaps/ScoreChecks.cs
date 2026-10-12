using System.Globalization;
using System.Numerics;
using TomYum.Bancho;
using TomYum.Domain.Entities;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// What a stable score must look like to be taken: numbers a client can send, mods osu! lets a player
/// pick together, and hit counts and a combo the map has room for (when TomYum knows the map's).
/// Each check gives the reason a score is refused (for the log), or null when it's fine.
/// </summary>
public static class ScoreChecks
{
    /// <summary>The grades osu! gives (silver SS and S with Hidden or Flashlight; F a failed play).</summary>
    public static readonly IReadOnlySet<string> Grades = new HashSet<string>(StringComparer.Ordinal) { "XH", "X", "SH", "S", "A", "B", "C", "D", "F" };

    private const Mods KeyMods = Mods.Key1 | Mods.Key2 | Mods.Key3 | Mods.Key4 | Mods.Key5 | Mods.Key6 | Mods.Key7 | Mods.Key8 | Mods.Key9;

    /// <summary>Why the score's own numbers can't be a play (negative, an unknown mode or grade), or null.</summary>
    /// <param name="mode">The ruleset (0 osu!, 1 taiko, 2 catch, 3 mania).</param>
    /// <param name="counts">300s, 100s, 50s, gekis, katus and misses.</param>
    /// <param name="score">The total score.</param>
    /// <param name="maxCombo">The play's max combo.</param>
    /// <param name="grade">The grade.</param>
    public static string? ValuesProblem(int mode, (int N300, int N100, int N50, int Geki, int Katu, int Miss) counts, long score, int maxCombo, string grade)
    {
        if (mode is < 0 or > 3)
            return string.Create(CultureInfo.InvariantCulture, $"mode {mode}");
        if (counts.N300 < 0 || counts.N100 < 0 || counts.N50 < 0 || counts.Geki < 0 || counts.Katu < 0 || counts.Miss < 0)
            return "a negative hit count";
        if (score < 0 || maxCombo < 0)
            return "a negative score or combo";
        if (!Grades.Contains(grade))
            return $"grade '{grade}'";
        return null;
    }

    /// <summary>The mods as osu! sends them: Nightcore with DoubleTime and Perfect with SuddenDeath (added when missing).</summary>
    /// <param name="mods">The score's mods.</param>
    public static int NormalizeMods(int mods)
    {
        if ((mods & (int)Mods.Nightcore) != 0)
            mods |= (int)Mods.DoubleTime;
        if ((mods & (int)Mods.Perfect) != 0)
            mods |= (int)Mods.SuddenDeath;
        return mods;
    }

    /// <summary>Why no score can have these mods (Autoplay, Cinema, or ones osu! won't pick together), or null.</summary>
    /// <param name="mods">The score's mods, after <see cref="NormalizeMods"/>.</param>
    public static string? ModsProblem(int mods)
    {
        var m = (Mods)mods;
        if ((m & (Mods.Autoplay | Mods.Cinema)) != 0)
            return "Autoplay or Cinema";
        if (m.HasFlag(Mods.Easy | Mods.HardRock))
            return "Easy with Hard Rock";
        if (m.HasFlag(Mods.HalfTime | Mods.DoubleTime))
            return "Half Time with Double Time";
        if (m.HasFlag(Mods.Relax | Mods.Autopilot))
            return "Relax with Autopilot";
        if (m.HasFlag(Mods.NoFail) && (m & Mods.SuddenDeath) != 0)
            return "No Fail with Sudden Death";
        if (BitOperations.PopCount((uint)(m & KeyMods)) > 1)
            return "more than one key count";
        return null;
    }

    /// <summary>
    /// Why the play doesn't fit its map, or null: more judgements than the map has objects, or a combo
    /// above the map's. Checked only in the map's own mode (converts differ) and with what's known of the map.
    /// </summary>
    /// <param name="mode">The ruleset.</param>
    /// <param name="counts">300s, 100s, 50s, gekis, katus and misses.</param>
    /// <param name="maxCombo">The play's max combo.</param>
    /// <param name="beatmap">The map.</param>
    public static string? MapProblem(int mode, (int N300, int N100, int N50, int Geki, int Katu, int Miss) counts, int maxCombo, Beatmap beatmap)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        if (mode != beatmap.Mode)
            return null;

        // One judgement per object at most (a slider, spinner or hold note is one). Catch judges every
        // droplet too, so it isn't checked; mania's MAX and 200 judgements are the geki and katu counts.
        if (mode != 2 && beatmap.Details is { CountCircles: { } circles, CountSliders: { } sliders, CountSpinners: { } spinners }
            && circles + sliders + spinners is > 0 and var objects)
        {
            long judged = (long)counts.N300 + counts.N100 + counts.N50 + counts.Miss + (mode == 3 ? (long)counts.Geki + counts.Katu : 0);
            if (judged > objects)
                return string.Create(CultureInfo.InvariantCulture, $"{judged} judgements on a map of {objects} objects");
        }

        // The map's max combo (osu!'s, worked out by today's client) can be a little under what an old
        // client counts on some sliders, so there's a margin. Mania's combo isn't checked.
        if (mode != 3 && beatmap.MaxCombo > 0 && maxCombo > beatmap.MaxCombo + Math.Max(5, beatmap.MaxCombo / 50))
            return string.Create(CultureInfo.InvariantCulture, $"a combo of {maxCombo} on a map with {beatmap.MaxCombo}");
        return null;
    }
}
