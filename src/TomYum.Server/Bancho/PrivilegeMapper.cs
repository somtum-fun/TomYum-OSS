using TomYum.Bancho;
using TomYum.Domain.Enums;

namespace TomYum.Server.Bancho;

/// <summary>
/// Maps the server's protocol-independent <see cref="UserPrivileges"/> onto the client-facing
/// <see cref="BanchoPrivileges"/> bitmask the osu!stable client renders (badges, supporter perks,
/// moderation UI). The domain owns authorization; this is the stable edge's translation of it.
/// </summary>
/// <remarks>
/// A logged-in account always carries <see cref="BanchoPrivileges.Player"/>. Higher domain flags
/// light up the matching client flags so staff get the in-client tools/badges. Lives in the server
/// layer because it bridges two libraries (Domain + Bancho) that must not depend on each other.
/// </remarks>
public static class PrivilegeMapper
{
    /// <summary>Projects domain <paramref name="privileges"/> onto the client privilege bitmask.</summary>
    public static BanchoPrivileges ToBancho(UserPrivileges privileges)
    {
        // Every authenticated player is at least a Player on the wire.
        var result = BanchoPrivileges.Player;

        if (privileges.HasFlag(UserPrivileges.Supporter))
        {
            result |= BanchoPrivileges.Supporter;
        }

        if (privileges.HasFlag(UserPrivileges.Moderator))
        {
            result |= BanchoPrivileges.Moderator;
        }

        if (privileges.HasFlag(UserPrivileges.Administrator))
        {
            result |= BanchoPrivileges.Administrator;
        }

        if (privileges.HasFlag(UserPrivileges.Developer))
        {
            result |= BanchoPrivileges.Developer;
        }

        return result;
    }
}
