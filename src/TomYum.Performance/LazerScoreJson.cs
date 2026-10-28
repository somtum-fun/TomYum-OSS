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

    /// <summary>The play's mods as JSON (the difficulty cache's key for them).</summary>
    public static string ModsKey(string lazerScore) =>
        JsonNode.Parse(lazerScore)?["mods"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "[]";
}
