namespace TomYum.Persistence;

/// <summary>
/// The inactivity rule for the rankings (setting <c>Ranking:InactiveDays</c>,
/// <c>TOMYUM_RANKING__INACTIVEDAYS</c>): a player who hasn't set a score in a mode for this many
/// days drops out of that mode's rankings (global and country, on the website and in-game) and
/// comes back with their next score. 0 (the default) turns it off. Set once at startup. The scores
/// and stats stay as they are; only ranks are withheld, like osu!'s own inactivity rule.
/// </summary>
public static class RankingInactivity
{
    /// <summary>The setting's key.</summary>
    public const string Key = "Ranking:InactiveDays";

    private static volatile int _days;

    /// <summary>Days without a score after which a player is inactive; 0 = off.</summary>
    public static int Days
    {
        get => _days;
        set => _days = Math.Max(0, value);
    }

    /// <summary>Whether the rule is on.</summary>
    public static bool Enabled => _days > 0;

    /// <summary>The oldest "last played" (UTC) that still counts as active, or null when the rule is off.</summary>
    public static DateTime? CutoffUtc(DateTimeOffset now) => Enabled ? now.UtcDateTime.AddDays(-_days) : null;

    /// <summary>SQL for "last played after the cutoff" over column <paramref name="column"/> (an empty string when the rule is off).</summary>
    public static string SqlCondition(string column, DateTimeOffset now) =>
        CutoffUtc(now) is { } cutoff
            ? $" AND {column} >= '{cutoff.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}'"
            : string.Empty;
}
