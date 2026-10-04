using System.Globalization;
using TomYum.Bancho;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// What keeps old clients' scores (docs/old-clients.md) fair on the leaderboards they share with
/// today's: score v1 and the mod multipliers haven't changed since, so their scores count as they
/// are, with these exceptions (osuTitanic's server does the same for the mods).
/// </summary>
public static class LegacyScoreRules
{
    /// <summary>Before 2014, mod bit 4 was NoVideo; today it's TouchDevice (which lowers pp).</summary>
    public static readonly DateOnly NoVideoUntil = new(2014, 1, 1);

    /// <summary>
    /// The build that played the score, from the version at the end of the score string (field 18).
    /// Only builds before b1553 send fewer fields, and early ones send an empty version: those are old
    /// builds too (the player's session build when it's known, else an unknown numbered build).
    /// </summary>
    /// <param name="parts">The score string's fields.</param>
    /// <param name="session">The build the player logged in with, if they're online.</param>
    public static ClientBuild BuildOf(IReadOnlyList<string> parts, ClientBuild? session = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        // The version is padded with spaces (anti-cheat flags); "20140624", "b20140624" or "1553".
        var version = parts.Count >= 18 ? parts[17].Trim() : string.Empty;
        if (version.Length > 0)
            return ClientBuild.Parse(version.StartsWith('b') ? version : "b" + version);
        return session is { IsLegacy: true } ? session : UnknownNumbered;
    }

    /// <summary>A numbered build whose number isn't known (a score without a version).</summary>
    internal static readonly ClientBuild UnknownNumbered = new(string.Empty, null) { Number = 0 };

    /// <summary>The score's mods as today's numbers: NoVideo (bit 4 before 2014) isn't TouchDevice.</summary>
    public static int Mods(int mods, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.IsBefore(NoVideoUntil) ? mods & ~(int)TomYum.Bancho.Mods.TouchDevice : mods;
    }

    /// <summary>Mods an old build's score is refused for: Co-op (made unranked in April 2015; old builds still send it).</summary>
    public static bool IsRefused(int mods, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.IsLegacy && (mods & (int)TomYum.Bancho.Mods.KeyCoop) != 0;
    }

    /// <summary>
    /// Whether a passed play's judgements cover the map as today's client converts it. In taiko and
    /// catch every object that counts for combo is judged a 300, a 100 or a miss, so those add up to the
    /// map's max combo in that mode; an old client that converted the map differently doesn't match,
    /// and its score stays off the leaderboard. Other modes, or an unknown max combo, always match.
    /// </summary>
    public static bool JudgementsMatch(int mode, int count300, int count100, int countMiss, int? maxCombo) =>
        mode is not (1 or 2) || maxCombo is not { } expected || expected <= 0
        || count300 + count100 + countMiss == expected;

    /// <summary>For the log: the build's version as sent, or "unknown".</summary>
    public static string Describe(ClientBuild build) => build switch
    {
        { Date: { } date } => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        { Number: > 0 and var number } => string.Create(CultureInfo.InvariantCulture, $"b{number}"),
        { Number: 0 } => "a numbered build",
        _ => "unknown",
    };
}
