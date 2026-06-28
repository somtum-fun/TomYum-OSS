using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// USER_PRESENCE_REQUEST_ALL (id 98): the client asks for static USER_PRESENCE (id 83) for
/// every online user (sent when its user list first opens). We reply with one USER_PRESENCE
/// per online player, including the originator. No payload.
/// </summary>
/// <remarks>
/// Stable batches these via USER_PRESENCE_BUNDLE (96) on newer clients, but a flat sequence of
/// individual USER_PRESENCE packets is accepted by every modern build and is what we emit until
/// presence bundling lands. Population here is small (single node, in-memory registry).
/// </remarks>
public sealed class UserPresenceRequestAllHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.UserPresenceRequestAll;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var player in context.Registry.All)
        {
            LoginResponseBuilder.WritePresence(context.Response, player.Presence);
        }
    }
}
