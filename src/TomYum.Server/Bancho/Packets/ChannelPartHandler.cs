using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// CHANNEL_PART (id 78): the client leaves a channel by name. We remove the player from the
/// channel's member set and from its own joined-channel set. No reply is required — the client
/// has already closed the tab locally. A part for an unknown channel or one the player is not in
/// is a harmless no-op.
/// </summary>
public sealed class ChannelPartHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ChannelPart;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var name = reader.ReadString();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        context.Channels.Get(name)?.Leave(context.Player.UserId);
        context.Player.LeaveChannel(name);
    }
}
