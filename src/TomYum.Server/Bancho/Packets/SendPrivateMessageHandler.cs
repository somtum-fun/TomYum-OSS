using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// SEND_PRIVATE_MESSAGE (id 25): the client sends a direct message to another user. Payload is a
/// message composite; the client-supplied sender fields are ignored and filled from the session.
/// We relay SEND_MESSAGE (id 7) to the target's queue. If the target is <c>BanchoBot</c>, the bot
/// answers any <c>!</c>command (or a default greeting) straight back to the sender.
/// </summary>
/// <remarks>
/// An online recipient who is silenced (TARGET_IS_SILENCED, id 101) or only takes PMs from friends
/// (USER_DM_BLOCKED, id 100) doesn't get it, and the sender is told. A recipient who set an away
/// message answers with it (osu!'s "is away" line).
/// </remarks>
public sealed class SendPrivateMessageHandler : IClientPacketHandler
{
    private readonly BanchoBot _bot;
    private readonly ChatLog? _chatLog;
    private readonly OsuWeb.OsuWebChatBridge? _website;

    /// <summary>Creates the handler over the resident <see cref="BanchoBot"/> (DM command target), the chat log and the website chat link.</summary>
    public SendPrivateMessageHandler(BanchoBot bot, ChatLog? chatLog = null, OsuWeb.OsuWebChatBridge? website = null)
    {
        _bot = bot;
        _chatLog = chatLog;
        _website = website;
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

        // Long messages are cut; too many too fast are dropped (BanchoBot commands too), and the sender is told.
        text = ChatLimits.Cut(text, ChatLimits.MaxMessageLength);
        if (!context.Player.ChatRate.TryTake(DateTimeOffset.UtcNow))
        {
            ChatEncoder.WriteMessage(context.Response, new BanchoMessage(BanchoBot.Name, ChatLimits.TooFast, sender, BanchoBot.UserId));
            return;
        }

        var nowPlaying = NowPlaying.TryParse(text);
        if (nowPlaying is not null)
            context.Services.GetService<NowPlayingStore>()?.Set(context.Player.UserId, nowPlaying);

        // A DM to BanchoBot: answer back to the sender rather than routing to a real session.
        // Always allowed, also for silenced/restricted players (commands like !help keep working).
        if (string.Equals(target, BanchoBot.Name, StringComparison.OrdinalIgnoreCase))
        {
            _chatLog?.Record(context.Player.UserId, sender, BanchoBot.Name, text, BanchoBot.UserId);
            // A /np gets the map's details and pp; anything else is a command (or a friendly hint).
            var reply = nowPlaying is not null && context.Services.GetService<MapCommands>() is { } maps
                ? await maps.DescribeAsync(nowPlaying, cancellationToken).ConfigureAwait(false)
                : await _bot.TryHandleCommandAsync(
                    context.Player, text, context.Player.Presence.Privileges, cancellationToken).ConfigureAwait(false)
                  ?? "Hi! I only respond to commands — try !help.";
            ChatEncoder.WriteMessage(
                context.Response,
                new BanchoMessage(BanchoBot.Name, reply, sender, BanchoBot.UserId));
            // The conversation shows in the player's website chat too.
            _website?.ForwardPrivateMessage(context.Player.UserId, BanchoBot.Name, text);
            _website?.ForwardPrivateMessage(BanchoBot.UserId, sender, reply);
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

        var recipient = FindByName(context, target);
        if (recipient is not null && recipient.IsSilenced(now))
        {
            WriteRefusal(context.Response, ServerPacketId.TargetIsSilenced, recipient.Presence.Username);
            return;
        }
        if (recipient is not null && recipient.BlocksNonFriendDms && !recipient.Friends.Contains(context.Player.UserId))
        {
            WriteRefusal(context.Response, ServerPacketId.UserDmBlocked, recipient.Presence.Username);
            return;
        }
        // The website's blocks and "friends only" count in-game too, whether the recipient is online or
        // will read it on the website (staff may always write, as there).
        if ((context.Player.Presence.Privileges & (BanchoPrivileges.Moderator | BanchoPrivileges.Developer | BanchoPrivileges.Owner)) == 0
            && context.Services.GetService<TomYum.Domain.Repositories.IFriendRepository>() is { } friends)
        {
            var targetId = recipient?.UserId;
            if (targetId is null && context.Services.GetService<TomYum.Domain.Repositories.IUserRepository>() is { } users)
                targetId = (await users.GetBySafeNameAsync(TomYum.Domain.Entities.User.MakeSafeName(target), cancellationToken).ConfigureAwait(false))?.Id;
            if (targetId is { } id && id != context.Player.UserId
                && !await friends.MayMessageAsync(context.Player.UserId, id, cancellationToken).ConfigureAwait(false))
            {
                WriteRefusal(context.Response, ServerPacketId.UserDmBlocked, recipient?.Presence.Username ?? target);
                return;
            }
        }

        // Relay to a real online recipient, if present.
        // The website gets every PM too: the recipient may only be on the site (or read it there later).
        _website?.ForwardPrivateMessage(context.Player.UserId, target, text);
        if (context.Services.GetService<TomYum.Plugins.Abstractions.IEventBus>() is { } events)
            await events.PublishAsync(new TomYum.Plugins.Abstractions.ChatMessageEvent(context.Player.UserId, sender, null, text), cancellationToken).ConfigureAwait(false);

        if (recipient is null)
        {
            return;
        }

        _chatLog?.Record(context.Player.UserId, sender, recipient.Presence.Username, text, recipient.UserId);
        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(sender, text, target, context.Player.UserId));
        recipient.Enqueue(writer.ToArray());

        if (recipient.AwayMessage is { } away)
        {
            ChatEncoder.WriteMessage(context.Response,
                new BanchoMessage(recipient.Presence.Username, $"\u0001ACTION is away: {away}\u0001", sender, recipient.UserId));
        }
    }

    /// <summary>Tells the sender their PM to <paramref name="target"/> wasn't delivered (a message-shaped packet naming the recipient).</summary>
    private static void WriteRefusal(BanchoPacketWriter response, ServerPacketId packet, string target) =>
        response.WritePacket(packet, w => w.WriteString(string.Empty).WriteString(string.Empty).WriteString(target).WriteInt32(0));

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
