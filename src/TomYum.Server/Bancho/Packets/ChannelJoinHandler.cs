using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// CHANNEL_JOIN (id 63): the client asks to join a channel by name. On success we add the player
/// to the channel's member set and confirm with CHANNEL_JOIN_SUCCESS (id 64) so the client opens
/// the tab. A join request for an unknown channel is answered with CHANNEL_KICK (id 66) so the
/// client closes the (failed) tab rather than waiting.
/// </summary>
public sealed class ChannelJoinHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ChannelJoin;

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

        var channel = context.Channels.Get(name);
        if (channel is null)
        {
            ChatEncoder.WriteChannelKick(context.Response, name);
            return;
        }

        channel.Join(context.Player);
        context.Player.JoinChannel(name);
        ChatEncoder.WriteChannelJoinSuccess(context.Response, name);
    }
}
