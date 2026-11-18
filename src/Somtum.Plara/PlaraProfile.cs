// Pla-ra: star rating and pp for custom lazer rulesets. Keep this file the same in TomYum (src/Somtum.Plara) and
// TumThai (osu.Game/Rulesets/Plara): the game's numbers must be the server's.

using System;
using System.Collections.Generic;

namespace Somtum.Plara;

/// <summary>Where a ruleset's star rating comes from.</summary>
public enum PlaraStarSource
{
    /// <summary>Pla-ra's timing model: the ruleset's own difficulty calculator is a placeholder.</summary>
    Plara,

    /// <summary>The ruleset's own difficulty calculator, which is a real one (tau, soyokaze; osu!fps and FPSosu use osu!'s).</summary>
    Own,
}

/// <summary>
/// How Pla-ra rates one custom ruleset: where its stars come from, how they're scaled to osu!'s, and what each of
/// its mods does to pp. A mod that isn't in <see cref="ModMultipliers"/> gives no pp (the play still counts on
/// score boards): a ruleset's new mod can't be farmed before someone has looked at it.
/// </summary>
public sealed class PlaraProfile
{
    public PlaraProfile(string assembly, PlaraStarSource source, double scale, IReadOnlyDictionary<string, double> modMultipliers, bool givesPp = true)
    {
        Assembly = assembly;
        Source = source;
        Scale = scale;
        ModMultipliers = modMultipliers;
        GivesPp = givesPp;
    }

    /// <summary>The ruleset DLL's assembly name (osu.Game.Rulesets.Sentakki): how both the server and the game find it.</summary>
    public string Assembly { get; }

    public PlaraStarSource Source { get; }

    /// <summary>Pla-ra stars are multiplied by this, so that across ranked maps their median is osu!'s stars of the same map.</summary>
    public double Scale { get; }

    /// <summary>Mods that give pp, by acronym (upper case), with what they multiply it by. NF is in here, and costs more on top.</summary>
    public IReadOnlyDictionary<string, double> ModMultipliers { get; }

    /// <summary>False for a ruleset that isn't really a game (sandbox): stars only, no pp.</summary>
    public bool GivesPp { get; }
}

/// <summary>Every ruleset Pla-ra knows. Scales are from calibration against 150 ranked osu! maps (2026-11-18).</summary>
public static class PlaraProfiles
{
    /// <summary>
    /// The version of Pla-ra's stars and pp: change it with any change to the numbers here or in the calculators, and
    /// recalculate every custom ruleset's scores (osu-web: php artisan tomyum:plara-recalculate).
    /// </summary>
    public const int VERSION = 1;

    private const double easy = 0.5;
    private const double lenient = 0.8;
    private const double hidden = 1.05;
    private const double blind = 1.10;

    // Rate mods are in the stars already; so are mods that change the notes (the map is rated after the ruleset
    // converts it with them). Relax and Autopilot have boards of their own.
    private static readonly string[] rates = { "DT", "NC", "HT", "DC" };

    private static readonly Dictionary<string, PlaraProfile> byAssembly = new Dictionary<string, PlaraProfile>(StringComparer.OrdinalIgnoreCase);

    static PlaraProfiles()
    {
        add("osu.Game.Rulesets.Sentakki", PlaraStarSource.Plara, 0.105,
            ok: new[] { "HR", "SD", "PF", "C", "AC", "EX", "CL", "MR", "TD" },
            multiplied: new Dictionary<string, double> { ["HD"] = hidden });
        add("osu.Game.Rulesets.Tau", PlaraStarSource.Own, 1,
            ok: new[] { "NF", "HR", "SD", "PF", "ST", "IS", "RX", "AP" },
            multiplied: new Dictionary<string, double> { ["EZ"] = easy, ["LN"] = lenient, ["LT"] = lenient, ["FO"] = hidden, ["FI"] = hidden, ["FL"] = blind });
        add("osu.Game.Rulesets.Rush", PlaraStarSource.Plara, 0.109,
            ok: new[] { "NF", "SD", "PF" },
            multiplied: new Dictionary<string, double> { ["FL"] = blind });
        add("osu.Game.Rulesets.Hishigata", PlaraStarSource.Plara, 0.123,
            ok: new[] { "NF", "HR", "SD", "IN", "UW" },
            multiplied: new Dictionary<string, double> { ["HD"] = hidden, ["TW"] = lenient });
        add("osu.Game.Rulesets.Soyokaze", PlaraStarSource.Own, 1,
            ok: new[] { "NF", "HR", "SD", "PF", "ST", "RD" },
            multiplied: new Dictionary<string, double> { ["EZ"] = easy, ["HD"] = hidden });

        // osu!'s own stars: HR, EZ and FL are in them already.
        foreach (string fps in new[] { "osu.Game.Rulesets.Fposu", "osu.Game.Rulesets.FPSosu" })
        {
            add(fps, PlaraStarSource.Own, 1,
                ok: new[] { "NF", "EZ", "HR", "SD", "PF", "FL", "ST", "AC", "TP", "CL", "RD", "MR", "AL", "SG", "TD", "RX", "AP" },
                multiplied: new Dictionary<string, double> { ["HD"] = hidden, ["TC"] = hidden, ["BL"] = blind, ["SO"] = 0.95 });
        }

        add("osu.Game.Rulesets.Swing", PlaraStarSource.Plara, 0.106,
            ok: new[] { "NF", "SD", "NS", "AN" },
            multiplied: new Dictionary<string, double> { ["EZ"] = easy });
        add("osu.Game.Rulesets.Bosu", PlaraStarSource.Plara, 0.071,
            ok: new[] { "NF", "SD" },
            multiplied: new Dictionary<string, double> { ["EZ"] = easy, ["HD"] = hidden });
        add("osu.Game.Rulesets.Touhosu", PlaraStarSource.Plara, 0.071,
            ok: new[] { "NF", "SD", "DM", "NR" },
            multiplied: new Dictionary<string, double> { ["EZ"] = easy, ["HD"] = hidden });
        add("osu.Game.Rulesets.Sandbox", PlaraStarSource.Plara, 0.123, ok: Array.Empty<string>(), multiplied: new Dictionary<string, double>(), givesPp: false);
    }

    /// <summary>Every profile, for a list of what Pla-ra rates.</summary>
    public static IReadOnlyCollection<PlaraProfile> All => byAssembly.Values;

    /// <summary>The profile of the ruleset in <paramref name="assembly"/>, or null when Pla-ra doesn't rate it (osu!'s own rulesets).</summary>
    public static PlaraProfile? Find(string? assembly) =>
        assembly != null && byAssembly.TryGetValue(assembly, out var profile) ? profile : null;

    private static void add(string assembly, PlaraStarSource source, double scale, string[] ok, Dictionary<string, double> multiplied, bool givesPp = true)
    {
        var mods = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string acronym in rates)
            mods[acronym] = 1;
        foreach (string acronym in ok)
            mods[acronym] = 1;
        foreach (var pair in multiplied)
            mods[pair.Key] = pair.Value;

        byAssembly[assembly] = new PlaraProfile(assembly, source, scale, mods, givesPp);
    }
}
