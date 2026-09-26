namespace TomYum.Domain.Enums;

/// <summary>
/// A beatmap's ranked status as TomYum stores and serves it, matching bancho.py's internal
/// <c>RankedStatus</c> enum. This is the value written verbatim as the first field of the
/// <c>osu-osz2-getscores.php</c> response, so the integer values are wire-significant and must
/// not be reordered.
/// </summary>
/// <remarks>
/// Distinct from the osu! v1 API's <c>approved</c> integer — convert API values in with
/// <see cref="RankedStatusExtensions.FromOsuApi"/>. Maps with a status below
/// <see cref="Ranked"/> have no leaderboard and award no pp.
/// </remarks>
public enum RankedStatus
{
    /// <summary>The map (hash) is not known to the server.</summary>
    NotSubmitted = -1,

    /// <summary>Submitted but unranked: graveyard, WIP, or pending all collapse here.</summary>
    Pending = 0,

    /// <summary>An older version of a known map; the client should update.</summary>
    UpdateAvailable = 1,

    /// <summary>Ranked: has a leaderboard and awards ranked pp.</summary>
    Ranked = 2,

    /// <summary>Approved: like ranked, awards pp.</summary>
    Approved = 3,

    /// <summary>Qualified: in the ranking queue; has a leaderboard but no ranked pp.</summary>
    Qualified = 4,

    /// <summary>Loved: has a leaderboard but awards no ranked pp.</summary>
    Loved = 5,
}

/// <summary>Conversions between <see cref="RankedStatus"/> and the osu! v1 API status integer.</summary>
public static class RankedStatusExtensions
{
    /// <summary>
    /// Maps an osu! v1 API <c>approved</c> integer to the internal <see cref="RankedStatus"/>,
    /// mirroring bancho.py's <c>RankedStatus.from_osuapi</c>. Unknown values fall back to
    /// <see cref="RankedStatus.UpdateAvailable"/>.
    /// </summary>
    public static RankedStatus FromOsuApi(int apiStatus) => apiStatus switch
    {
        -2 => RankedStatus.Pending,         // graveyard
        -1 => RankedStatus.Pending,         // WIP
        0 => RankedStatus.Pending,          // pending
        1 => RankedStatus.Ranked,
        2 => RankedStatus.Approved,
        3 => RankedStatus.Qualified,
        4 => RankedStatus.Loved,
        _ => RankedStatus.UpdateAvailable,
    };

    /// <summary>Whether maps with this status have a leaderboard (Ranked, Approved, Qualified, Loved).</summary>
    public static bool HasLeaderboard(this RankedStatus status) =>
        status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Qualified or RankedStatus.Loved;

    /// <summary>Whether maps with this status award ranked pp (Ranked, Approved).</summary>
    public static bool AwardsRankedPp(this RankedStatus status) =>
        status is RankedStatus.Ranked or RankedStatus.Approved;
}
