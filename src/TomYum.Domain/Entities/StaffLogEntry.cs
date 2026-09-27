namespace TomYum.Domain.Entities;

/// <summary>
/// One action taken from a staff panel (beatmap import, restriction, group change, …), kept so the
/// panels can show who did what and when. Written by the admin API; never edited.
/// </summary>
public sealed class StaffLogEntry
{
    /// <summary>Auto-increment id (also the newest-first ordering).</summary>
    public long Id { get; init; }

    /// <summary>When it happened (UTC).</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>The acting staff member's user id (0 when unknown).</summary>
    public int ActorId { get; init; }

    /// <summary>What was done, dotted lowercase, e.g. <c>beatmapset.import</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>What it was done to (beatmap set id, user id, score id, …), if anything.</summary>
    public long? TargetId { get; init; }

    /// <summary>Human-readable description, e.g. <c>Artist - Title (7 difficulties)</c>.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Outcome, e.g. <c>imported</c>, <c>not_found</c>, <c>failed</c>; null when there's only one.</summary>
    public string? Result { get; init; }

    /// <summary>The bulk job this was part of, if any.</summary>
    public string? JobId { get; init; }
}
