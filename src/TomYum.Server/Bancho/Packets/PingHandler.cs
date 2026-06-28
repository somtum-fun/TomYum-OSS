using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// PING (id 4): the client's keep-alive. There is no payload and nothing to reply with —
/// touching <see cref="OnlinePlayer.LastActivityUtc"/> (done centrally by the dispatcher)
/// is all that is required so the session-reaper keeps the player online.
/// </summary>
public sealed class PingHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.Ping;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        // No-op: the dispatcher already refreshed the player's activity timestamp.
    }
}
