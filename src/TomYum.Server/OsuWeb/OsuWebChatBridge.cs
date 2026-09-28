using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using StackExchange.Redis;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>A website chat message as osu-web's <c>TomYumChatBridge</c> publishes it.</summary>
public sealed record WebChatMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("sender_id")] int SenderId,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("target_id")] int? TargetId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("is_action")] bool IsAction);

/// <summary>
/// Links in-game chat with website (osu-web) chat, both ways:
/// <list type="bullet">
/// <item>Website → game: osu-web publishes every public-channel message and PM on Redis
/// (<see cref="RedisChannel"/>); they're delivered to the in-game channel's members or the PM's
/// recipient (not echoed to the sender's own game session) and logged.</item>
/// <item>Game → website: <see cref="ForwardChannelMessage"/> / <see cref="ForwardPrivateMessage"/>
/// queue messages that are posted through osu-web's InterOp in the background, so chat never waits
/// on the website. PMs to players who aren't in-game reach them on the website.</item>
/// </list>
/// The game's public channels are created on the website at startup. osu-web doesn't publish
/// messages it received from here, so nothing loops.
/// </summary>
public sealed class OsuWebChatBridge : BackgroundService
{
    /// <summary>The Redis channel osu-web publishes website messages on.</summary>
    public const string RedisChannel = "tomyum:chat";

    /// <summary>osu!stable's /me framing.</summary>
    private const string ActionPrefix = "\u0001ACTION ";

    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(5_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly ChatLog _chatLog;
    private readonly ILogger<OsuWebChatBridge> _logger;
    private readonly IConnectionMultiplexer? _redis;

    public OsuWebChatBridge(
        IServiceScopeFactory scopes,
        PlayerRegistry players,
        ChannelRegistry channels,
        ChatLog chatLog,
        ILogger<OsuWebChatBridge> logger,
        IConnectionMultiplexer? redis = null)
    {
        _scopes = scopes;
        _players = players;
        _channels = channels;
        _chatLog = chatLog;
        _logger = logger;
        _redis = redis;
    }

    /// <summary>Queues an in-game channel message for website chat (only the registry's public channels exist there).</summary>
    public void ForwardChannelMessage(int senderId, string channel, string text)
    {
        if (_channels.Get(channel) is not null)
            _outbound.Writer.TryWrite(new Outbound(senderId, channel, null, text));
    }

    /// <summary>Queues an in-game PM to <paramref name="targetName"/> for website chat.</summary>
    public void ForwardPrivateMessage(int senderId, string targetName, string text) =>
        _outbound.Writer.TryWrite(new Outbound(senderId, null, targetName, text));

    /// <summary>Delivers a website message in-game (public for tests). Returns how many sessions got it.</summary>
    public int Deliver(WebChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var text = message.IsAction ? $"{ActionPrefix}{message.Content}\u0001" : message.Content;
        var delivered = 0;

        if (message.Channel is { } channelName)
        {
            if (_channels.Get(channelName) is not { } channel)
                return 0;

            var framed = Frame(new BanchoMessage(message.Sender, text, channel.Name, message.SenderId));
            foreach (var member in channel.Members.Where(m => m.UserId != message.SenderId))
            {
                member.Enqueue(framed);
                delivered++;
            }
            _chatLog.Record(message.SenderId, message.Sender, channel.Name, message.Content, origin: ChatOrigin.Website);
        }
        else if (message.TargetId is { } targetId)
        {
            // BanchoBot has a session nobody reads: it answers through AnswerWebsiteCommandAsync instead.
            var recipient = targetId == BanchoBot.UserId ? null : _players.GetByUserId(targetId);
            if (recipient is not null)
            {
                recipient.Enqueue(Frame(new BanchoMessage(message.Sender, text, recipient.Presence.Username, message.SenderId)));
                delivered++;
            }
            _chatLog.Record(message.SenderId, message.Sender, recipient?.Presence.Username ?? (targetId == BanchoBot.UserId ? BanchoBot.Name : $"#{targetId}"), message.Content, targetId);
        }

        return delivered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The website may still be starting (e.g. both came up together): keep trying for a while.
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= 20 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                if (await EnsureWebsiteChannelsAsync(lastAttempt: attempt == 20, stoppingToken).ConfigureAwait(false))
                    return;
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
        }, stoppingToken);

        if (_redis is not null)
        {
            var subscriber = _redis.GetSubscriber();
            await subscriber.SubscribeAsync(StackExchange.Redis.RedisChannel.Literal(RedisChannel), (_, value) => OnWebsiteMessage(value))
                .ConfigureAwait(false);
        }

        await foreach (var message in _outbound.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await SendToWebsiteAsync(message, stoppingToken).ConfigureAwait(false);
    }

    private void OnWebsiteMessage(RedisValue value)
    {
        try
        {
            if (JsonSerializer.Deserialize<WebChatMessage>(value.ToString()) is { } message)
            {
                Deliver(message);
                if (IsForBanchoBot(message))
                    _ = AnswerWebsiteCommandAsync(message, CancellationToken.None);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring an unreadable website chat message.");
        }
    }

    /// <summary>Whether BanchoBot answers a website message: any PM to it, or a <c>!</c>command in a game channel.</summary>
    internal bool IsForBanchoBot(WebChatMessage message) =>
        message.SenderId != BanchoBot.UserId
        && (message.TargetId == BanchoBot.UserId
            || (message.Channel is { } channel && _channels.Get(channel) is not null && message.Content.StartsWith('!')));

    /// <summary>
    /// Runs a website user's command as them (with their in-game privileges; as their game session
    /// when they're online, so e.g. <c>!mp</c> works) and posts BanchoBot's reply where they asked:
    /// in the channel, in-game and on the website, or as a website PM. Returns the reply (for tests).
    /// </summary>
    public async Task<string?> AnswerWebsiteCommandAsync(WebChatMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var scope = _scopes.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                .GetByIdAsync(message.SenderId, cancellationToken).ConfigureAwait(false);
            if (user is null)
                return null;

            var privileges = PrivilegeMapper.ToBancho(user.Privileges);
            var caller = _players.GetByUserId(user.Id)
                ?? new OnlinePlayer("website", new BanchoPresence { UserId = user.Id, Username = user.Username, Privileges = privileges });
            var reply = await scope.ServiceProvider.GetRequiredService<BanchoBot>()
                .TryHandleCommandAsync(caller, message.Content, privileges, cancellationToken).ConfigureAwait(false);
            if (message.TargetId == BanchoBot.UserId)
                reply ??= "Hi! I only respond to commands — try !help.";
            if (reply is null)
                return null;

            if (message.Channel is { } channelName && _channels.Get(channelName) is { } channel)
            {
                var framed = Frame(new BanchoMessage(BanchoBot.Name, reply, channel.Name, BanchoBot.UserId));
                foreach (var member in channel.Members)
                    member.Enqueue(framed);
                _chatLog.Record(BanchoBot.UserId, BanchoBot.Name, channel.Name, reply);
                ForwardChannelMessage(BanchoBot.UserId, channel.Name, reply);
            }
            else
            {
                _chatLog.Record(BanchoBot.UserId, BanchoBot.Name, user.Username, reply, user.Id);
                ForwardPrivateMessage(BanchoBot.UserId, user.Username, reply);
            }
            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "BanchoBot couldn't answer website user {UserId}.", message.SenderId);
            return null;
        }
    }

    private async Task SendToWebsiteAsync(Outbound message, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var interop = scope.ServiceProvider.GetRequiredService<OsuWebInterop>();
            if (!interop.IsConfigured)
                return;

            int? targetId = null;
            if (message.TargetName is { } targetName)
            {
                var target = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                    .GetBySafeNameAsync(User.MakeSafeName(targetName), cancellationToken).ConfigureAwait(false);
                if (target is null || target.Id == message.SenderId)
                    return;
                targetId = target.Id;
            }

            var (text, isAction) = SplitAction(message.Text);
            await interop.SendChatMessageAsync(message.SenderId, message.Channel, targetId, text, isAction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Website chat missing a line beats failing in-game chat.
            _logger.LogWarning(ex, "Couldn't copy a chat message from user {UserId} to the website.", message.SenderId);
        }
    }

    /// <summary>Creates the game's channels on the website; false when it should be tried again.</summary>
    private async Task<bool> EnsureWebsiteChannelsAsync(bool lastAttempt, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var interop = scope.ServiceProvider.GetRequiredService<OsuWebInterop>();
            if (!interop.IsConfigured)
            {
                _logger.LogInformation("osu-web InterOp not configured: website chat isn't linked with the game.");
                return true;
            }

            // #announce is for staff on the website too (moderated: only privileged users can post).
            await interop.EnsureChatChannelsAsync(
                _channels.All.Select(c => (c.Name, c.Topic, c.Name == "#announce")), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Website chat linked: {Count} channel(s) available on the site.", _channels.All.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (lastAttempt)
                _logger.LogWarning(ex, "Couldn't create the game's chat channels on the website.");
            else
                _logger.LogInformation("Website not reachable yet for chat channels ({Message}); retrying.", ex.Message);
            return false;
        }
    }

    /// <summary>Stable sends /me as "\x01ACTION text\x01"; the website stores the text with an action flag.</summary>
    internal static (string Text, bool IsAction) SplitAction(string text) =>
        text.StartsWith(ActionPrefix, StringComparison.Ordinal)
            ? (text[ActionPrefix.Length..].TrimEnd('\u0001'), true)
            : (text, false);

    private static byte[] Frame(BanchoMessage message)
    {
        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, message);
        return writer.ToArray();
    }

    private sealed record Outbound(int SenderId, string? Channel, string? TargetName, string Text);
}
