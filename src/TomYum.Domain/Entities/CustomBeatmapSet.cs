namespace TomYum.Domain.Entities;

/// <summary>
/// A beatmap set that exists only on this server (ids from <see cref="Beatmap.CustomIdFloor"/>):
/// who owns it and, while its mapper submits it from a game client, the ids handed out for it.
/// The difficulties themselves are <see cref="Beatmap"/> rows once files are uploaded.
/// </summary>
public sealed class CustomBeatmapSet
{
    /// <summary>The set id.</summary>
    public int SetId { get; init; }

    /// <summary>The owner's user id (the mapper, who may update it).</summary>
    public int OwnerId { get; set; }

    /// <summary>What the mapper submitted it as: <see cref="Wip"/> or <see cref="Pending"/> (osu! API status numbers).</summary>
    public int Target { get; set; }

    /// <summary>When its ids were first handed out.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When its ids or files last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When files were last uploaded; null while only ids have been handed out.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>osu! API status for a work in progress.</summary>
    public const int Wip = -1;

    /// <summary>osu! API status for a set waiting for nominations.</summary>
    public const int Pending = 0;
}

/// <summary>A difficulty id that belongs to a <see cref="CustomBeatmapSet"/> (handed out before its file exists).</summary>
public sealed class CustomBeatmapSlot
{
    /// <summary>The beatmap (difficulty) id.</summary>
    public int BeatmapId { get; init; }

    /// <summary>The set it belongs to.</summary>
    public int SetId { get; init; }
}
