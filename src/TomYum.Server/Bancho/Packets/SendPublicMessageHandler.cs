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
    private readonly ChatLog? _chatLog;
    private readonly OsuWeb.OsuWebChatBridge? _website;

    /// <summary>Creates the handler over the resident <see cref="BanchoBot"/> (for <c>!</c>commands), the chat log and the website chat link.</summary>
    public SendPublicMessageHandler(BanchoBot bot, ChatLog? chatLog = null, OsuWeb.OsuWebChatBridge? website = null)
    {
        _bot = bot;
        _chatLog = chatLog;
        _website = website;
    }

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SendPublicMessage;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (text, target) = ParseMessage(context.Payload.Span);

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
        {
            return;
        }

        ChatChannel? channel;
        if (target == "#multiplayer")
        {
            var matchForChannel = context.Matches.GetByUserId(context.Player.UserId);
            if (matchForChannel is null) return;
            channel = matchForChannel.Channel;
        }
        else if (target == "#spectator")
        {
            // The instance is owned by the host: the player is either watching someone (route to
            // that host's channel) or is itself the host (its own channel). Membership is checked below.
            var host = context.Player.SpectatingUserId is int hostId
                ? context.Registry.GetByUserId(hostId)
                : context.Player;
            if (host is null) return;
            channel = host.SpectatorChannel;
        }
        else
        {
            channel = context.Channels.Get(target);
        }

        if (channel is null || !channel.Contains(context.Player.UserId))
        {
            return;
        }

        // Silenced and restricted players can't talk in channels; tell them why instead of dropping silently.
        var now = DateTimeOffset.UtcNow;
        if (context.Player.IsSilenced(now) || context.Player.IsRestricted)
        {
            var secondsLeft = context.Player.SilenceSecondsLeft(now);
            var notice = context.Player.IsRestricted
                ? "Your account is restricted, so you can't talk in public channels."
                : ModerationPackets.SilencedReply(secondsLeft);
            ChatEncoder.WriteMessage(context.Response, new BanchoMessage(BanchoBot.Name, notice, target, BanchoBot.UserId));
            if (!context.Player.IsRestricted)
                ModerationPackets.WriteSilenceEnd(context.Response, secondsLeft);
            return;
        }

        var sender = context.Player.Presence.Username;
        var message = new BanchoMessage(sender, text, target, context.Player.UserId);
        _chatLog?.Record(context.Player.UserId, sender, target, text);
        _website?.ForwardChannelMessage(context.Player.UserId, target, text);
        if (context.Services.GetService<TomYum.Plugins.Abstractions.IEventBus>() is { } events)
            await events.PublishAsync(new TomYum.Plugins.Abstractions.ChatMessageEvent(context.Player.UserId, sender, target, text), cancellationToken).ConfigureAwait(false);

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
        var reply = await _bot.TryHandleCommandAsync(
            context.Player, text, context.Player.Presence.Privileges, cancellationToken).ConfigureAwait(false);
        if (reply is not null)
        {
            var botWriter = new BanchoPacketWriter(128);
            ChatEncoder.WriteMessage(botWriter, new BanchoMessage(BanchoBot.Name, reply, target, BanchoBot.UserId));
            var botFramed = botWriter.ToArray();
            foreach (var member in channel.Members)
            {
                member.Enqueue(botFramed);
            }
            _chatLog?.Record(BanchoBot.UserId, BanchoBot.Name, target, reply);
            _website?.ForwardChannelMessage(BanchoBot.UserId, target, reply);
        }
    }

    /// <summary>
    /// Parses the message composite (sender + text + target + sender_id), returning only the text
    /// and target — the client-supplied sender fields are ignored. Synchronous because
    /// <see cref="BanchoPacketReader"/> is a ref struct and cannot live in an async method.
    /// </summary>
    private static (string Text, string Target) ParseMessage(ReadOnlySpan<byte> payload)
    {
        var reader = new BanchoPacketReader(payload);
        _ = reader.ReadString();          // sender (client-supplied, ignored)
        var text = reader.ReadString();
        var target = reader.ReadString(); // channel name, e.g. #osu
        _ = reader.ReadInt32();           // sender_id (client-supplied, ignored)
        return (text, target);
    }
}
