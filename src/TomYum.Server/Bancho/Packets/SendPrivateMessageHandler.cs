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
    private readonly ChatLog? _chatLog;

    /// <summary>Creates the handler over the resident <see cref="BanchoBot"/> (DM command target) and the chat log.</summary>
    public SendPrivateMessageHandler(BanchoBot bot, ChatLog? chatLog = null)
    {
        _bot = bot;
        _chatLog = chatLog;
    }

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SendPrivateMessage;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (text, target) = ParseMessage(context.Payload.Span);

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
        {
            return;
        }

        var sender = context.Player.Presence.Username;

        // A DM to BanchoBot: answer back to the sender rather than routing to a real session.
        // Always allowed, also for silenced/restricted players (commands like !help keep working).
        if (string.Equals(target, BanchoBot.Name, StringComparison.OrdinalIgnoreCase))
        {
            _chatLog?.Record(context.Player.UserId, sender, BanchoBot.Name, text, BanchoBot.UserId);
            var reply = await _bot.TryHandleCommandAsync(
                    context.Player, text, context.Player.Presence.Privileges, cancellationToken).ConfigureAwait(false)
                ?? "Hi! I only respond to commands — try !help.";
            ChatEncoder.WriteMessage(
                context.Response,
                new BanchoMessage(BanchoBot.Name, reply, sender, BanchoBot.UserId));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.Player.IsSilenced(now) || context.Player.IsRestricted)
        {
            var secondsLeft = context.Player.SilenceSecondsLeft(now);
            var notice = context.Player.IsRestricted
                ? "Your account is restricted, so you can only message BanchoBot."
                : ModerationPackets.SilencedReply(secondsLeft);
            ChatEncoder.WriteMessage(context.Response, new BanchoMessage(BanchoBot.Name, notice, sender, BanchoBot.UserId));
            if (!context.Player.IsRestricted)
                ModerationPackets.WriteSilenceEnd(context.Response, secondsLeft);
            return;
        }

        // Relay to a real online recipient, if present.
        var recipient = FindByName(context, target);
        if (recipient is null)
        {
            return;
        }

        _chatLog?.Record(context.Player.UserId, sender, recipient.Presence.Username, text, recipient.UserId);
        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(sender, text, target, context.Player.UserId));
        recipient.Enqueue(writer.ToArray());
    }

    /// <summary>
    /// Reads the message composite (sender + text + recipient + sender_id) from <paramref name="payload"/>,
    /// returning only the text and recipient (the client-supplied sender fields are ignored — the server
    /// fills them from the session). Synchronous because <see cref="BanchoPacketReader"/> is a ref struct
    /// and cannot live in the async handler.
    /// </summary>
    private static (string Text, string Target) ParseMessage(ReadOnlySpan<byte> payload)
    {
        var reader = new BanchoPacketReader(payload);
        _ = reader.ReadString();          // sender (client-supplied, ignored)
        var text = reader.ReadString();
        var target = reader.ReadString(); // recipient username
        _ = reader.ReadInt32();           // sender_id (client-supplied, ignored)
        return (text, target);
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
