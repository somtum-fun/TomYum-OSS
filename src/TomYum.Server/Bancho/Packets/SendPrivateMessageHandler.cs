using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// SEND_PRIVATE_MESSAGE (id 25): the client sends a direct message to another user. Payload is a
/// message composite; the client-supplied sender fields are ignored and filled from the session.
/// We relay SEND_MESSAGE (id 7) to the target's queue. If the target is <c>BanchoBot</c>, the bot
/// answers any <c>!</c>command (or a default greeting) straight back to the sender.
/// </summary>
/// <remarks>
/// DM blocking (USER_DM_BLOCKED, id 100), silence (TARGET_IS_SILENCED, id 101), and away messages
/// are later slices; for now any online target receives the message.
/// </remarks>
public sealed class SendPrivateMessageHandler : IClientPacketHandler
{
    private readonly BanchoBot _bot;

    /// <summary>Creates the handler over the resident <see cref="BanchoBot"/> (DM command target).</summary>
    public SendPrivateMessageHandler(BanchoBot bot) => _bot = bot;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SendPrivateMessage;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        _ = reader.ReadString();          // sender (client-supplied, ignored)
        var text = reader.ReadString();
        var target = reader.ReadString(); // recipient username
        _ = reader.ReadInt32();           // sender_id (client-supplied, ignored)

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
        {
            return;
        }

        var sender = context.Player.Presence.Username;

        // A DM to BanchoBot: answer back to the sender rather than routing to a real session.
        if (string.Equals(target, BanchoBot.Name, StringComparison.OrdinalIgnoreCase))
        {
            var reply = _bot.TryHandleCommand(sender, text, context.Player.Presence.Privileges)
                ?? "Hi! I only respond to commands — try !help.";
            ChatEncoder.WriteMessage(
                context.Response,
                new BanchoMessage(BanchoBot.Name, reply, sender, BanchoBot.UserId));
            return;
        }

        // Relay to a real online recipient, if present.
        var recipient = FindByName(context, target);
        if (recipient is null)
        {
            return;
        }

        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(sender, text, target, context.Player.UserId));
        recipient.Enqueue(writer.ToArray());
    }

    /// <summary>Resolves an online player by display name (case-insensitive), or <c>null</c>.</summary>
    private static OnlinePlayer? FindByName(ClientPacketContext context, string name)
    {
        foreach (var player in context.Registry.All)
        {
            if (string.Equals(player.Presence.Username, name, StringComparison.OrdinalIgnoreCase))
            {
                return player;
            }
        }

        return null;
    }
}
