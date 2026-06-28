namespace TomYum.Domain.Enums;

/// <summary>
/// The server's authorization model for a player: what a user is permitted to do, independent
/// of any protocol. Distinct from the client-facing privilege bitmask the osu! client renders
/// (badges, supporter perks) — the protocol edges map this onto their own wire flags. Mirrors
/// bancho.py's split between internal <c>Privileges</c> and <c>ClientPrivileges</c>.
/// </summary>
/// <remarks>
/// Stored on <see cref="Entities.User"/>. A freshly registered account is
/// <see cref="Normal"/>; the server owner (first real registration) is granted
/// <see cref="Owner"/>. Flags compose, so a developer is also an administrator, etc.
/// </remarks>
[Flags]
public enum UserPrivileges
{
    /// <summary>No privileges (e.g. a restricted or banned account).</summary>
    None = 0,

    /// <summary>A standard, logged-in player.</summary>
    Normal = 1 << 0,

    /// <summary>osu!supporter: cosmetic and feature perks.</summary>
    Supporter = 1 << 1,

    /// <summary>Moderator: access to moderation tooling.</summary>
    Moderator = 1 << 2,

    /// <summary>Administrator: full server administration.</summary>
    Administrator = 1 << 3,

    /// <summary>Developer / peppy-tier privileges.</summary>
    Developer = 1 << 4,

    /// <summary>
    /// The server owner: a normal player who is also supporter, administrator, and developer.
    /// Staff tiers carry <see cref="Supporter"/> so they get supporter perks/badge too.
    /// </summary>
    Owner = Normal | Supporter | Administrator | Developer,
}
