namespace TomYum.Domain.Entities;

/// <summary>
/// Extra osu! API metadata for a <see cref="Beatmap"/> that TomYum itself doesn't use but the
/// osu-web website shows (difficulty settings, object counts, BPM, set info). Travels with the
/// beatmap from the API to the osu-web mirror; not persisted in TomYum's own table.
/// </summary>
/// <remarks>
/// Every value is nullable: the keyless osu.direct mirror omits some fields (object counts) and
/// reports 0 for genre/language, which callers treat as unknown.
/// </remarks>
public sealed class BeatmapDetails
{
    /// <summary>Circle size (CS).</summary>
    public float? CircleSize { get; set; }

    /// <summary>Approach rate (AR).</summary>
    public float? ApproachRate { get; set; }

    /// <summary>Overall difficulty (OD).</summary>
    public float? OverallDifficulty { get; set; }

    /// <summary>HP drain rate (HP).</summary>
    public float? DrainRate { get; set; }

    /// <summary>Beats per minute.</summary>
    public float? Bpm { get; set; }

    /// <summary>Drain time in seconds (total length minus breaks).</summary>
    public int? HitLength { get; set; }

    /// <summary>Number of circles.</summary>
    public int? CountCircles { get; set; }

    /// <summary>Number of sliders.</summary>
    public int? CountSliders { get; set; }

    /// <summary>Number of spinners.</summary>
    public int? CountSpinners { get; set; }

    /// <summary>osu! genre id (1 = unspecified … 14 = jazz), or <c>null</c> when unknown.</summary>
    public int? GenreId { get; set; }

    /// <summary>osu! language id (1 = unspecified … 14 = other), or <c>null</c> when unknown.</summary>
    public int? LanguageId { get; set; }

    /// <summary>Song source (anime, game, …).</summary>
    public string? Source { get; set; }

    /// <summary>Space-separated search tags.</summary>
    public string? Tags { get; set; }

    /// <summary>Whether the set has a video.</summary>
    public bool? Video { get; set; }

    /// <summary>Whether the set has a storyboard.</summary>
    public bool? Storyboard { get; set; }

    /// <summary>When the set was ranked/approved/loved upstream.</summary>
    public DateTimeOffset? ApprovedDate { get; set; }
}
