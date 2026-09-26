namespace TomYum.Domain.Enums;

/// <summary>
/// The combined "gamemode" stored in <c>scores.Mode</c> and <c>user_stats.Mode</c>: the vanilla
/// ruleset (0-3) folded together with the Relax/Autopilot variant, matching bancho.py's
/// <c>GameMode</c>. This keeps relax and autopilot on their own leaderboards and stat rows
/// instead of mixing them into vanilla.
/// <code>
///   0 vn!std   1 vn!taiko  2 vn!catch  3 vn!mania
///   4 rx!std   5 rx!taiko  6 rx!catch  (7 rx!mania — unused)
///   8 ap!std   (9-11 — unused)
/// </code>
/// </summary>
public static class GameMode
{
    /// <summary>Added to the vanilla mode for a Relax score.</summary>
    public const int RelaxOffset = 4;

    /// <summary>Added to the vanilla mode for an Autopilot score.</summary>
    public const int AutopilotOffset = 8;

    /// <summary>The underlying ruleset id (0=osu!, 1=taiko, 2=catch, 3=mania), used for pp and
    /// accuracy calculation, which are ruleset- not variant-specific.</summary>
    public static int AsVanilla(int gameMode) => gameMode % 4;

    /// <summary>Whether this gamemode is a Relax variant (4-6).</summary>
    public static bool IsRelax(int gameMode) => gameMode is >= RelaxOffset and < AutopilotOffset;

    /// <summary>Whether this gamemode is an Autopilot variant (8+).</summary>
    public static bool IsAutopilot(int gameMode) => gameMode >= AutopilotOffset;

    /// <summary>
    /// Relax and Autopilot leaderboards are ranked by pp; vanilla is ranked by score. Mirrors
    /// bancho.py's scoring metric (<c>mode &gt;= RELAX_OSU ? "pp" : "score"</c>).
    /// </summary>
    public static bool UsesPpLeaderboard(int gameMode) => gameMode >= RelaxOffset;

    private const int RelaxBit = 1 << 7;       // osu! legacy mod bit for Relax
    private const int AutopilotBit = 1 << 13;  // osu! legacy mod bit for Autopilot (Relax2)

    /// <summary>
    /// Folds a vanilla ruleset (0-3) and a legacy mod bitmask into the combined gamemode, without
    /// depending on the protocol Mods enum. Autopilot takes precedence over Relax.
    /// </summary>
    public static int FromLegacyMods(int vanillaMode, int legacyMods)
    {
        if ((legacyMods & AutopilotBit) != 0) return vanillaMode + AutopilotOffset;
        if ((legacyMods & RelaxBit) != 0) return vanillaMode + RelaxOffset;
        return vanillaMode;
    }

    /// <summary>The bancho.py-style short name for a gamemode, e.g. <c>rx!catch</c> for 6.</summary>
    private static readonly string[] Repr =
    [
        "vn!std", "vn!taiko", "vn!catch", "vn!mania",
        "rx!std", "rx!taiko", "rx!catch", "rx!mania",
        "ap!std", "ap!taiko", "ap!catch", "ap!mania",
    ];

    /// <summary>The <c>rx!catch</c>-style short name for a gamemode (empty if out of range).</summary>
    public static string ToShortName(int gameMode) =>
        gameMode >= 0 && gameMode < Repr.Length ? Repr[gameMode] : string.Empty;

    /// <summary>
    /// Whether a config token matches a gamemode. Accepts the numeric value ("6"), the short name
    /// ("rx!catch"), or a variant group: "vanilla"/"vn", "relax"/"rx", "autopilot"/"ap" (all four
    /// rulesets of that variant), or "all"/"*" (everything).
    /// </summary>
    public static bool MatchesToken(string token, int gameMode)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim().ToLowerInvariant();

        if (t is "all" or "*") return true;
        if (int.TryParse(t, out var n)) return n == gameMode;

        switch (t)
        {
            case "vanilla" or "vn": return gameMode is >= 0 and < RelaxOffset;
            case "relax" or "rx": return gameMode is >= RelaxOffset and < AutopilotOffset;
            case "autopilot" or "ap": return gameMode >= AutopilotOffset;
        }

        for (var i = 0; i < Repr.Length; i++)
            if (Repr[i] == t) return i == gameMode;

        return false;
    }
}
