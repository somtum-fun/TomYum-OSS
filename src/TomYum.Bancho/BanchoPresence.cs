namespace TomYum.Bancho;

/// <summary>
/// The client-facing snapshot of a player used to encode USER_PRESENCE (id 83) and
/// USER_STATS (id 11). It bundles the static identity fields and the dynamic status
/// fields the osu!stable client renders for a user. Payload layouts are in
/// <c>memories/protocols/bancho-stable.md</c> §5.
/// </summary>
/// <remarks>
/// This is a protocol-edge DTO, not a domain entity: <c>TomYum.Server</c> projects a
/// <c>TomYum.Domain</c> <c>User</c> (plus live session state) onto this shape before
/// handing it to <see cref="LoginResponseBuilder"/>. Mutable so the host can update the
/// dynamic status fields in place as the player changes action.
/// </remarks>
public sealed class BanchoPresence
{
    /// <summary>The player's osu! user id (sent as the USER_ID login reply when non-negative).</summary>
    public required int UserId { get; init; }

    /// <summary>Display username.</summary>
    public required string Username { get; init; }

    // --- Static identity (USER_PRESENCE, id 83) ---

    /// <summary>Client UTC offset in hours; encoded on the wire as <c>offset + 24</c>.</summary>
    public int UtcOffset { get; init; }

    /// <summary>ISO-3166 country code as the osu! numeric country id.</summary>
    public byte CountryCode { get; init; }

    /// <summary>Client-side privilege bitmask shown to other players.</summary>
    public BanchoPrivileges Privileges { get; init; } = BanchoPrivileges.Player;

    /// <summary>Geolocated longitude shown on the in-game map.</summary>
    public float Longitude { get; init; }

    /// <summary>Geolocated latitude shown on the in-game map.</summary>
    public float Latitude { get; init; }

    /// <summary>Cached global leaderboard rank (0 = unranked).</summary>
    public int GlobalRank { get; set; }

    // --- Dynamic status (USER_STATS, id 11) ---

    /// <summary>Current action (idle, afk, playing, …) — the CHANGE_ACTION action byte.</summary>
    public byte Action { get; set; }

    /// <summary>Free-text describing the current action (e.g. the beatmap title being played).</summary>
    public string ActionInfo { get; set; } = string.Empty;

    /// <summary>MD5 of the beatmap tied to the current action, or empty.</summary>
    public string BeatmapMd5 { get; set; } = string.Empty;

    /// <summary>Mods active for the current action.</summary>
    public Mods Mods { get; set; } = Mods.None;

    /// <summary>Current game mode (0 = osu!, 1 = taiko, 2 = catch, 3 = mania).</summary>
    public int Mode { get; set; }

    /// <summary>Beatmap id tied to the current action, or 0.</summary>
    public int BeatmapId { get; set; }

    /// <summary>Ranked score for the current mode.</summary>
    public long RankedScore { get; set; }

    /// <summary>Accuracy as a 0..100 percentage.</summary>
    public float Accuracy { get; set; }

    /// <summary>Total play count for the current mode.</summary>
    public int PlayCount { get; set; }

    /// <summary>Total (unranked) score for the current mode.</summary>
    public long TotalScore { get; set; }

    /// <summary>Performance points, sent as an i16 (capped at 65535 by the wire format).</summary>
    public short Performance { get; set; }
}
