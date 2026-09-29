namespace TomYum.Domain.Entities;

/// <summary>
/// An in-game comment from osu!stable: shown on the timeline at <see cref="Time"/> while a map
/// plays, attached to the song (all its difficulties), one difficulty, or one replay.
/// </summary>
public sealed class BeatmapComment
{
    public long Id { get; init; }

    /// <summary><c>song</c> (the set), <c>map</c> (a difficulty) or <c>replay</c> (a score).</summary>
    public string TargetType { get; init; } = string.Empty;

    /// <summary>The set, difficulty or score id, by <see cref="TargetType"/>.</summary>
    public long TargetId { get; init; }

    public int UserId { get; init; }

    /// <summary>Where in the map (milliseconds).</summary>
    public int Time { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>A supporter's chosen colour (hex, e.g. <c>ff66aa</c>); null for the default.</summary>
    public string? Colour { get; init; }

    public DateTime CreatedAt { get; init; }
}
