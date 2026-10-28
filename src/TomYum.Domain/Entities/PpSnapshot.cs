namespace TomYum.Domain.Entities;

/// <summary>A player's total pp in a mode on a day, for the profile's pp-over-time graph (osu! keeps no such history).</summary>
public sealed class PpSnapshot
{
    public int UserId { get; init; }

    /// <summary>TomYum's mode: 0-3 the rulesets (the website's pp), 4-6 Relax, 8 Autopilot.</summary>
    public int Mode { get; init; }

    public DateOnly Day { get; init; }

    public double Pp { get; set; }
}
