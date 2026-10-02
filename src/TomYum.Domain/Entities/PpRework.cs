namespace TomYum.Domain.Entities;

/// <summary>
/// A pp rework: every score in some modes worked out again, as osu! does when its pp changes (a new
/// pp library, or by hand). While it runs, nothing in those modes moves: the new pp is kept aside
/// (<see cref="PpReworkScore"/>), rankings, leaderboards and totals stay as they were, and new scores
/// wait for their pp (<see cref="Score.PpHeld"/>). Then everything switches over at once.
/// </summary>
public sealed class PpRework
{
    public const string Running = "running";
    public const string Website = "website";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public int Id { get; init; }

    /// <summary>The pp library that changed: <c>vanilla</c> (ppy's), <c>akatsuki</c> (Relax/Autopilot), or <c>manual</c>.</summary>
    public string Library { get; init; } = "manual";

    /// <summary>The library's version before and after (empty for a rework by hand).</summary>
    public string FromVersion { get; init; } = string.Empty;

    public string ToVersion { get; init; } = string.Empty;

    /// <summary>The modes reworked, TomYum's ids comma-separated (0-3 vanilla, 4-6 Relax, 8 Autopilot).</summary>
    public string Modes { get; init; } = string.Empty;

    /// <summary>
    /// <c>running</c> (the scores), <c>website</c> (vanilla: waiting for the website's score processor
    /// to redo its copies), <c>done</c>, <c>cancelled</c> or <c>failed</c>.
    /// </summary>
    public string State { get; set; } = Running;

    /// <summary>Scores to work out, and how many are done (scores made meanwhile join in).</summary>
    public int Total { get; set; }

    public int Completed { get; set; }

    /// <summary>The last score done (they go by id), so a restart carries on from there.</summary>
    public long Cursor { get; set; }

    /// <summary>For the website stage: scores handed to its processor, and how many it still has.</summary>
    public int WebsiteTotal { get; set; }

    public int WebsiteLeft { get; set; }

    public int ActorId { get; init; }

    public string? Reason { get; init; }

    public string? Error { get; set; }

    public DateTime StartedAt { get; init; }

    public DateTime? FinishedAt { get; set; }

    public IReadOnlyList<int> ModeList =>
        [.. Modes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => int.Parse(m, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Whether it's still going (the scores, or the website's copies).</summary>
    public bool IsActive => State is Running or Website;
}

/// <summary>A score's new pp in a <see cref="PpRework"/>, kept aside until it switches over.</summary>
public sealed class PpReworkScore
{
    public int ReworkId { get; init; }

    public long ScoreId { get; init; }

    public double Pp { get; init; }
}
