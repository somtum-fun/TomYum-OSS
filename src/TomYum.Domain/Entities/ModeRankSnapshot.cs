namespace TomYum.Domain.Entities;

/// <summary>A player's Relax/Autopilot rank on a day (the website keeps vanilla ranks; TomYum these).</summary>
public sealed class ModeRankSnapshot
{
    public int UserId { get; init; }

    /// <summary>TomYum's mode (4-6 Relax, 8 Autopilot).</summary>
    public int Mode { get; init; }

    public DateOnly Day { get; init; }

    public int Rank { get; set; }
}
