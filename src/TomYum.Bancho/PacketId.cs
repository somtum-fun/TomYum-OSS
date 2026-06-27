namespace TomYum.Bancho;

/// <summary>
/// Bancho (osu!stable) binary protocol packet identifiers. Values are the on-wire
/// <c>uint16</c> ids and are stable across the ecosystem (confirmed identical across
/// the official 2016 source, bancho.py, BanchoNET, and Sunrise).
/// </summary>
/// <remarks>
/// Direction is encoded in the member name prefix: <c>Client*</c> packets travel
/// client→server, <c>Server*</c> packets server→client. Only a representative subset
/// is stubbed here; the full table lives in
/// <c>memories/protocols/bancho-stable.md</c>. TODO: complete the enum when the
/// packet layer is implemented.
/// </remarks>
public enum PacketId : ushort
{
    /// <summary>server→client: result of login (user id or negative error sentinel).</summary>
    ServerUserId = 5,

    /// <summary>server→client: a chat message.</summary>
    ServerSendMessage = 7,

    /// <summary>client→server: request for fresh stats on the listed users.</summary>
    ClientStatsRequest = 85,

    /// <summary>server→client: a user's presence block.</summary>
    ServerUserPresence = 83,

    /// <summary>server→client: a user's stats block.</summary>
    ServerUserStats = 11,
}
