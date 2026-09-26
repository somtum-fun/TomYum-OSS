using TomYum.Domain.Enums;

namespace TomYum.Domain.Entities;

/// <summary>
/// A single difficulty within a beatmap set.
/// </summary>
/// <remarks>
/// Server-local (custom-submitted) beatmaps reserve ids at or above
/// <see cref="CustomIdFloor"/> so they never collide with official osu! ids.
/// See <c>memories/architecture/decisions/0009-custom-beatmap-ids.md</c>.
/// Scaffolding stub.
/// </remarks>
public sealed class Beatmap
{
    /// <summary>
    /// Inclusive lower bound for server-local beatmap and set ids. Ids at or above this
    /// value are owned by this server; ids below it mirror official osu! content.
    /// Must stay within osu-web's <c>MEDIUMINT UNSIGNED</c> id columns (max 16,777,215), which
    /// TomYum shares (docs/osu-web.md); official ids are well below 10M, leaving headroom.
    /// </summary>
    public const int CustomIdFloor = 10_000_000;

    /// <summary>Beatmap (difficulty) id.</summary>
    public int Id { get; set; }

    /// <summary>Owning beatmap set id.</summary>
    public int SetId { get; set; }

    /// <summary>MD5 of the <c>.osu</c> file, used as the wire identity in both protocols.</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>Whether this beatmap id is in the server-local custom range.</summary>
    public bool IsCustom => Id >= CustomIdFloor;

    // --- Metadata (populated from the osu! v1 API or custom submission) ---

    /// <summary>Track artist.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Difficulty (version) name, e.g. <c>Insane</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Mapper (creator) username.</summary>
    public string Creator { get; set; } = string.Empty;

    /// <summary>Ranked/approval status. Drives leaderboard visibility and pp eligibility.</summary>
    public RankedStatus Status { get; set; } = RankedStatus.Pending;

    /// <summary>
    /// When set, <see cref="Status"/> is preserved across osu!-API refreshes even if the API
    /// reports a different status (mirrors bancho.py's <c>frozen</c> flag). Map metadata is still
    /// updated; only the status is held.
    /// </summary>
    public bool StatusFrozen { get; set; }

    /// <summary>Game mode (0 = osu!, 1 = taiko, 2 = catch, 3 = mania).</summary>
    public int Mode { get; set; }

    /// <summary>Star difficulty rating.</summary>
    public double StarRating { get; set; }

    /// <summary>Total length in seconds.</summary>
    public int TotalLength { get; set; }

    /// <summary>Maximum achievable combo (0 when unknown).</summary>
    public int MaxCombo { get; set; }

    /// <summary>When the map was last updated upstream (drives the osu!-API recheck cadence).</summary>
    public DateTimeOffset LastUpdate { get; set; }

    /// <summary>Full display name: <c>{Artist} - {Title} [{Version}]</c>.</summary>
    public string FullName => $"{Artist} - {Title} [{Version}]";

    /// <summary>Whether this map has a leaderboard (ranked, approved, or loved).</summary>
    public bool HasLeaderboard =>
        Status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Loved;
}
