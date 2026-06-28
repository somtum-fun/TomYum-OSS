using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// SEND_PUBLIC_MESSAGE (id 1): the client posts a message to a channel. Payload is a message
/// composite (sender + text + target + sender_id) but the client→server sender fields are
/// ignored — the server fills them from the session. We broadcast SEND_MESSAGE (id 7) to every
/// member of the target channel except the sender, then let BanchoBot answer any <c>!</c>command.
/// </summary>
/// <remarks>
/// Messages to a channel the player is not a member of are dropped. Channel read/write privilege
/// gating is a later slice; for now membership is the only gate. BanchoBot's reply (if any) is
/// posted back to the same channel so everyone sees it.
/// </remarks>
public sealed class SendPublicMessageHandler : IClientPacketHandler
{
    private readonly BanchoBot _bot;

    /// <summary>Creates the handler over the resident <see cref="BanchoBot"/> (for <c>!</c>commands).</summary>
    public SendPublicMessageHandler(BanchoBot bot) => _bot = bot;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SendPublicMessage;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        _ = reader.ReadString();          // sender (client-supplied, ignored)
        var text = reader.ReadString();
        var target = reader.ReadString(); // channel name, e.g. #osu
        _ = reader.ReadInt32();           // sender_id (client-supplied, ignored)

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
        {
            return;
        }

        var channel = context.Channels.Get(target);
        if (channel is null || !channel.Contains(context.Player.UserId))
        {
            return;
        }

        var sender = context.Player.Presence.Username;
        var message = new BanchoMessage(sender, text, target, context.Player.UserId);

        // Fan the message out to every other member of the channel.
        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, message);
        var framed = writer.ToArray();
        foreach (var member in channel.Members)
        {
            if (member.UserId != context.Player.UserId)
            {
                member.Enqueue(framed);
            }
        }

        // Let BanchoBot answer a command, posting its reply to the same channel.
        var reply = _bot.TryHandleCommand(sender, text);
        if (reply is not null)
        {
            var botWriter = new BanchoPacketWriter(128);
            ChatEncoder.WriteMessage(botWriter, new BanchoMessage(BanchoBot.Name, reply, target, BanchoBot.UserId));
            var botFramed = botWriter.ToArray();
            foreach (var member in channel.Members)
            {
                member.Enqueue(botFramed);
            }
        }
    }
}
