namespace TomYum.Domain.Entities;

/// <summary>Status values stored in <see cref="Score.Status"/>.</summary>
public static class ScoreStatus
{
    public const int Failed    = 0;
    public const int Submitted = 1; // saved but not the player's best on this map
    public const int Best      = 2; // the player's current best on this map
}

public sealed class Score
{
    public long Id { get; init; }
    public int UserId { get; init; }
    public int BeatmapId { get; init; }
    public string BeatmapMd5 { get; init; } = string.Empty;
    public int Mode { get; init; }
    public long ScoreValue { get; init; }
    public int MaxCombo { get; init; }
    public int Count300 { get; init; }
    public int Count100 { get; init; }
    public int Count50 { get; init; }
    public int CountMiss { get; init; }
    public int CountGeki { get; init; }
    public int CountKatu { get; init; }
    public string Grade { get; init; } = "F";
    public int Mods { get; init; }
    public bool Perfect { get; init; }
    public double Pp { get; set; }
    public bool Passed { get; init; }
    public int Status { get; set; } = ScoreStatus.Failed;
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    /// Played in lazer (a Relax play TomYum took from the website); false for osu!stable's own
    /// submissions. The website shows stable plays with the Classic mod, like osu!.
    /// </summary>
    public bool FromLazer { get; init; }

    /// <summary>
    /// For a lazer play without Classic: its slider ticks/repeats hit and slider ends hit, which its pp
    /// counts (null otherwise: a stable play, or a lazer one with Classic, scored as stable).
    /// </summary>
    public int? LargeTickHits { get; init; }

    public int? SliderEndHits { get; init; }

    /// <summary>For a lazer play: its score on the website (the row its pp is shown on), else null.</summary>
    public ulong? OsuWebScoreId { get; set; }

    /// <summary>
    /// Made while its mode's pp was being reworked: its pp waits (not shown, not a best, not in the
    /// totals) until the recalculation is done.
    /// </summary>
    public bool PpHeld { get; set; }
}
