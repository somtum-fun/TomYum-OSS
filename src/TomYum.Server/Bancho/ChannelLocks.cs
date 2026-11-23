using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// <c>!lock</c> / <c>!unlock</c>: staff say it in a channel (any of the server's channels, <c>#spectator</c> or
/// <c>#multiplayer</c>) and from then on only staff can talk there. Players' messages to a locked channel are
/// dropped on the server without a reply, so a flood just stops. Staff = moderators, developers and owners.
/// </summary>
/// <remarks>
/// The lock lives on the channel (<see cref="ChatChannel.Locked"/>): a room's goes away with the room, a spectating's
/// when its last spectator leaves, and a restart unlocks everything. The website's copy of a server channel is made
/// moderated while locked, so lazer and website chat follow it too.
/// </remarks>
public static class ChannelLocks
{
    private const BanchoPrivileges Staff = BanchoPrivileges.Moderator | BanchoPrivileges.Developer | BanchoPrivileges.Owner;

    /// <summary>Whether <paramref name="privileges"/> may lock channels and talk in locked ones.</summary>
    public static bool IsStaff(BanchoPrivileges privileges) => (privileges & Staff) != 0;

    /// <summary>True for <c>!lock</c>, false for <c>!unlock</c>, null for anything else.</summary>
    public static bool? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = text.Trim().Split(' ', 2)[0];
        return word.Equals("!lock", StringComparison.OrdinalIgnoreCase) ? true
            : word.Equals("!unlock", StringComparison.OrdinalIgnoreCase) ? false
            : null;
    }

    /// <summary>BanchoBot's answer in the channel.</summary>
    public static string Reply(string channel, bool locked) => locked
        ? $"{channel} is locked: only staff can talk here for now."
        : $"{channel} is unlocked.";
}
