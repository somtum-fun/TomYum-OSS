using System.Text.Json;
using System.Text.Json.Nodes;

namespace TomYum.Performance;

/// <summary>
/// A lazer play as <see cref="PerformanceRequest.LazerScore"/> carries it: its mods (with settings) and
/// judgements, the parts of the website's score data its pp is worked out from.
/// </summary>
public static class LazerScoreJson
{
    private static readonly string[] Kept = ["mods", "statistics", "maximum_statistics"];

    /// <summary>The parts of a website score's data (its <c>data</c> column) that pp needs; null when it has none.</summary>
    public static string? FromScoreData(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || JsonNode.Parse(data) is not JsonObject root || root["statistics"] is not JsonObject)
            return null;
        var kept = new JsonObject();
        foreach (var name in Kept)
        {
            if (root[name] is { } value)
                kept[name] = value.DeepClone();
        }
        return kept.ToJsonString();
    }

    /// <summary><paramref name="lazerScore"/> without the mods <paramref name="acronyms"/>.</summary>
    public static string? WithoutMods(string? lazerScore, params string[] acronyms)
    {
        if (string.IsNullOrWhiteSpace(lazerScore) || JsonNode.Parse(lazerScore) is not JsonObject root || root["mods"] is not JsonArray mods)
            return lazerScore;
        foreach (var mod in mods.ToList())
        {
            if (mod?["acronym"]?.GetValue<string>() is { } acronym && acronyms.Contains(acronym))
                mods.Remove(mod);
        }
        return root.ToJsonString();
    }

    /// <summary>
    /// <paramref name="lazerScore"/> as a full combo ("pp if FC"): its misses hit as greats, and every slider
    /// tick and end (and catch's droplets) caught, as many as the map has.
    /// </summary>
    public static string? AsFullCombo(string? lazerScore)
    {
        if (string.IsNullOrWhiteSpace(lazerScore) || JsonNode.Parse(lazerScore) is not JsonObject root || root["statistics"] is not JsonObject statistics)
            return lazerScore;
        int Count(JsonObject? counts, string name) => counts?[name] is JsonValue value && value.TryGetValue(out int n) ? n : 0;

        statistics["great"] = Count(statistics, "great") + Count(statistics, "miss");
        var maximum = root["maximum_statistics"] as JsonObject;
        foreach (var (hit, miss) in TickResults)
        {
            var most = Math.Max(Count(maximum, hit), Count(statistics, hit) + Count(statistics, miss));
            if (most > 0)
                statistics[hit] = most;
            statistics.Remove(miss);
        }
        statistics.Remove("miss");
        return root.ToJsonString();
    }

    /// <summary>
    /// <paramref name="lazerScore"/> as an SS with its own mods (the map's max pp as the player played it, a DT's
    /// speed and DA's stats included): every result the map has, hit as well as it can be (its maximum_statistics).
    /// </summary>
    public static string? AsPerfect(string? lazerScore)
    {
        if (string.IsNullOrWhiteSpace(lazerScore) || JsonNode.Parse(lazerScore) is not JsonObject root
            || root["maximum_statistics"] is not JsonObject maximum || maximum.Count == 0)
            return lazerScore;
        root["statistics"] = maximum.DeepClone();
        root["accuracy"] = 1.0;
        return root.ToJsonString();
    }

    /// <summary>Lazer's tick results that a full combo hits all of, and their misses.</summary>
    private static readonly (string Hit, string Miss)[] TickResults =
        [("large_tick_hit", "large_tick_miss"), ("small_tick_hit", "small_tick_miss"), ("slider_tail_hit", "slider_tail_miss")];

    /// <summary>Lazer's basic judgements (a map's objects, without ticks or bonus): what classic score counts.</summary>
    private static readonly string[] BasicResults = ["great", "ok", "meh", "miss", "perfect", "good"];

    /// <summary>
    /// A lazer play's <paramref name="standardised"/> score as classic, as lazer shows it (and osu! adds it to a
    /// player's ranked and total score): osu!'s conversion by ruleset, from the map's objects (the play's
    /// maximum_statistics). <paramref name="standardised"/> as it is when the objects aren't known, or for mania.
    /// </summary>
    public static long Classic(string? lazerScore, int ruleset, long standardised)
    {
        if (string.IsNullOrWhiteSpace(lazerScore) || JsonNode.Parse(lazerScore) is not JsonObject root || root["maximum_statistics"] is not JsonObject maximum)
            return standardised;
        long objects = 0;
        foreach (var name in BasicResults)
        {
            if (maximum[name] is JsonValue value && value.TryGetValue(out int n))
                objects += n;
        }
        if (objects <= 0)
            return standardised;
        const double max = 1_000_000;
        return ruleset switch
        {
            0 => (long)Math.Round((objects * objects * 32.57 + 100_000) * standardised / max, MidpointRounding.ToEven),
            1 => (long)Math.Round((objects * 1109 + 100_000) * standardised / max, MidpointRounding.ToEven),
            2 => (long)Math.Round(Math.Pow(standardised / max * objects, 2) * 21.62 + standardised / 10d, MidpointRounding.ToEven),
            _ => standardised,
        };
    }

    /// <summary>A lazer play's judgements as JSON: <c>{"statistics":{...},"maximum_statistics":{...}}</c>.</summary>
    public static string StatisticsKey(string lazerScore)
    {
        var root = JsonNode.Parse(lazerScore);
        return new JsonObject
        {
            ["statistics"] = root?["statistics"]?.DeepClone() ?? new JsonObject(),
            ["maximum_statistics"] = root?["maximum_statistics"]?.DeepClone() ?? new JsonObject(),
        }.ToJsonString();
    }

    /// <summary>The play's mods as JSON (the difficulty cache's key for them).</summary>
    public static string ModsKey(string lazerScore) =>
        JsonNode.Parse(lazerScore)?["mods"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "[]";
}
