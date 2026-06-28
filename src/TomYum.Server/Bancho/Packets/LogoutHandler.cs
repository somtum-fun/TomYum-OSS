using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// LOGOUT (id 2): the client is disconnecting. We remove the session from the registry and
/// broadcast USER_LOGOUT (id 12) to every remaining player so their user lists drop the player.
/// </summary>
/// <remarks>
/// The client sends a trailing i32 (an unused timestamp/zero) which we do not need to read.
/// Stable also fires a spurious LOGOUT immediately after login on some builds; guarding against
/// that (e.g. ignoring a logout within the first second) is a refinement for a later slice.
/// </remarks>
public sealed class LogoutHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.Logout;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Registry.Remove(context.Player.Token);

        var writer = new BanchoPacketWriter(16);
        writer.WriteInt32Packet(ServerPacketId.UserLogout, context.Player.UserId);
        context.Registry.EnqueueToAll(writer.ToArray(), exceptUserId: context.Player.UserId);
    }
}
