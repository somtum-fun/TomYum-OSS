namespace TomYum.Server.OsuWeb;

/// <summary>Ruleset names as players read them, and the short names commands and settings use.</summary>
public static class Rulesets
{
    /// <summary>The short names of the rulesets, by id.</summary>
    public static readonly IReadOnlyList<string> Keys = ["osu", "taiko", "catch", "mania"];

    private static readonly string[] Names = ["osu!", "osu!taiko", "osu!catch", "osu!mania"];

    /// <summary>A ruleset's display name ("osu!" for an unknown id).</summary>
    /// <param name="ruleset">The ruleset id (0-3).</param>
    public static string Name(int ruleset) => ruleset is >= 0 and < 4 ? Names[ruleset] : "osu!";

    /// <summary>A ruleset id from its short name (or null for unknown).</summary>
    /// <param name="key">The short name, e.g. <c>taiko</c>, <c>std</c> or <c>ctb</c>.</param>
    public static int? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var index = Keys.ToList().FindIndex(k => string.Equals(k, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : key.Trim().ToLowerInvariant() switch { "std" or "standard" => 0, "fruits" or "ctb" => 2, _ => null };
    }
}
