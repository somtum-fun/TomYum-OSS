using System.Text.Json;
using TomYum.Bancho;
using TomYum.Domain.Enums;

namespace TomYum.Server.Achievements;

/// <summary>What a lazer score's data says, for judging medals the stable way (<see cref="MedalCatalog"/>).</summary>
/// <param name="Mods">The score's mods as stable mod bits.</param>
/// <param name="UnrankedMods">Mods stable has no bit for, or non-default mod settings: no skill or introduction medal.</param>
/// <param name="FullCombo">The combo covered every combo-counting judgement.</param>
public sealed record LazerMedalFacts(Mods Mods, bool UnrankedMods, bool FullCombo);

/// <summary>
/// Reads a lazer score's <c>data</c> (mods and judgement counts) so its skill and mod introduction
/// medals can be judged with the same rules as stable's, as ppy's processor does: Classic and
/// system mods (touch) don't count as mods; a full combo is a max combo equal to all
/// combo-counting judgements the map has.
/// </summary>
public static class LazerMedalJudge
{
    private static readonly Dictionary<string, Mods> Acronyms = new(StringComparer.Ordinal)
    {
        ["NF"] = Mods.NoFail, ["EZ"] = Mods.Easy, ["HD"] = Mods.Hidden, ["HR"] = Mods.HardRock,
        ["SD"] = Mods.SuddenDeath, ["PF"] = Mods.Perfect | Mods.SuddenDeath, ["DT"] = Mods.DoubleTime,
        ["NC"] = Mods.Nightcore | Mods.DoubleTime, ["HT"] = Mods.HalfTime, ["FL"] = Mods.Flashlight,
        ["SO"] = Mods.SpunOut, ["RX"] = Mods.Relax, ["AP"] = Mods.Autopilot, ["FI"] = Mods.FadeIn, ["MR"] = Mods.Mirror,
        ["1K"] = Mods.Key1, ["2K"] = Mods.Key2, ["3K"] = Mods.Key3, ["4K"] = Mods.Key4, ["5K"] = Mods.Key5,
        ["6K"] = Mods.Key6, ["7K"] = Mods.Key7, ["8K"] = Mods.Key8, ["9K"] = Mods.Key9, ["DS"] = Mods.KeyCoop,
    };

    /// <summary>Mods that don't count at all (Classic marks legacy scoring; touch is a system mod).</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "CL", "TD" };

    /// <summary>The judgements that count toward combo, as in lazer's <c>HitResult.AffectsCombo</c>.</summary>
    private static readonly HashSet<string> ComboJudgements = new(StringComparer.Ordinal)
    {
        "miss", "meh", "ok", "good", "great", "perfect", "large_tick_hit", "large_tick_miss", "slider_tail_hit",
    };

    public static LazerMedalFacts Read(string? data, int maxCombo)
    {
        var mods = Mods.None;
        var unranked = false;
        var fullCombo = false;
        if (string.IsNullOrEmpty(data))
            return new LazerMedalFacts(mods, true, false);

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("mods", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in list.EnumerateArray())
                {
                    var acronym = mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("acronym", out var a) ? a.GetString() : mod.GetString();
                    if (acronym is null || Ignored.Contains(acronym))
                        continue;
                    var customised = mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("settings", out var settings)
                        && settings.ValueKind == JsonValueKind.Object && settings.EnumerateObject().Any();
                    if (Acronyms.TryGetValue(acronym, out var bits) && !customised)
                        mods |= bits;
                    else
                        unranked = true;
                }
            }

            if (root.TryGetProperty("maximum_statistics", out var maximum) && maximum.ValueKind == JsonValueKind.Object)
            {
                var comboTotal = maximum.EnumerateObject()
                    .Where(p => ComboJudgements.Contains(p.Name) && p.Value.TryGetInt32(out _))
                    .Sum(p => p.Value.GetInt32());
                fullCombo = comboTotal > 0 && maxCombo >= comboTotal;
            }
        }
        catch (JsonException)
        {
            unranked = true;
        }
        return new LazerMedalFacts(mods, unranked, fullCombo);
    }

    /// <summary>The medals (<see cref="MedalCatalog"/>, but not combo medals: those are stable-only) a lazer play earns.</summary>
    public static IEnumerable<Medal> Earned(LazerMedalFacts facts, int ruleset, double? stars, RankedStatus status, int beatmapId)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.UnrankedMods)
            return [];
        var check = new MedalCheck(ruleset, facts.Mods, 0, stars, facts.FullCombo, status, beatmapId);
        return MedalCatalog.All.Where(m => !m.File.StartsWith("osu-combo-", StringComparison.Ordinal) && m.Condition(check));
    }

    /// <summary>A website <c>approved</c> value as a status.</summary>
    public static RankedStatus Status(int approved) => approved switch
    {
        1 => RankedStatus.Ranked,
        2 => RankedStatus.Approved,
        3 => RankedStatus.Qualified,
        4 => RankedStatus.Loved,
        _ => RankedStatus.Pending,
    };
}
