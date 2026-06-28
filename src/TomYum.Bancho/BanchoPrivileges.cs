namespace TomYum.Bancho;

/// <summary>
/// Client-facing privilege bitmask sent in PRIVILEGES (id 71) and the low bits of the
/// USER_PRESENCE privilege/mode byte. These are the flags the osu!stable client itself
/// understands (badge colours, supporter perks, tournament client) — distinct from the
/// server's internal authorization model, which lives in <c>TomYum.Domain</c>.
/// </summary>
/// <remarks>
/// Values match the ecosystem-standard <c>ClientPrivileges</c> bitmask (bancho.py,
/// BanchoNET, Sunrise). See <c>memories/protocols/bancho-stable.md</c> §3/§5.
/// </remarks>
[Flags]
public enum BanchoPrivileges
{
    /// <summary>No special privileges.</summary>
    None = 0,

    /// <summary>A normal, logged-in player.</summary>
    Player = 1 << 0,

    /// <summary>Moderator: access to moderation tooling.</summary>
    Moderator = 1 << 1,

    /// <summary>osu!supporter: cosmetic and feature perks.</summary>
    Supporter = 1 << 2,

    /// <summary>Administrator.</summary>
    Administrator = 1 << 3,

    /// <summary>Developer / peppy-tier privileges.</summary>
    Developer = 1 << 4,

    /// <summary>Tournament staff client (referee).</summary>
    Tournament = 1 << 5,
}
