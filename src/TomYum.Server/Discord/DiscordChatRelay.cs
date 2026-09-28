using System.Globalization;
using System.Text;
using System.Threading.Channels;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Discord;

/// <summary>What happened to a Discord message sent to the relayed channel.</summary>
public enum RelayOutcome
{
    Relayed,
    Empty,
    NotLinked,
    Muted,
}

/// <summary>
/// Relays the in-game chat channel (<see cref="DiscordOptions.ChatChannel"/>, which the website
/// and lazer share) with a Discord channel, both ways. Game → Discord: everything the chat log
/// records for the channel, batched about once a second. Discord → game: only linked players, as
/// themselves, and not while they're silenced or restricted, so in-game moderation applies. Link
/// codes are never relayed.
/// </summary>
public sealed class DiscordChatRelay : BackgroundService
{
    /// <summary>Longest message relayed into the game (stable's chat box takes about this much).</summary>
    public const int MaxGameMessage = 400;

    private const string ActionPrefix = "\u0001ACTION ";

    private readonly Channel<string> _toDiscord = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly DiscordOptions _options;
    private readonly IDiscordGateway _gateway;
    private readonly ChatLog _chatLog;
    private readonly ChannelRegistry _channels;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly OsuWebChatBridge? _website;

    public DiscordChatRelay(
        DiscordOptions options,
        IDiscordGateway gateway,
        ChatLog chatLog,
        ChannelRegistry channels,
        IServiceScopeFactory scopes,
        TimeProvider time,
        OsuWebChatBridge? website = null)
    {
        _options = options;
        _gateway = gateway;
        _chatLog = chatLog;
        _channels = channels;
        _scopes = scopes;
        _time = time;
        _website = website;
        _chatLog.Recorded += OnRecorded;
    }

    /// <summary>A game/website chat line as it's posted on Discord, or null when it isn't relayed.</summary>
    public static string? ToDiscordLine(string sender, string text)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(text);
        if (IsLinkCode(text))
            return null;

        var name = DiscordFormat.Escape(sender);
        if (text.StartsWith(ActionPrefix, StringComparison.Ordinal))
            return $"\\* **{name}** {DiscordFormat.Escape(text[ActionPrefix.Length..].TrimEnd('\u0001'))}";
        return $"**{name}**: {DiscordFormat.Escape(text)}";
    }

    /// <summary>A Discord message as it's said in-game: one line, trimmed; null when nothing is left.</summary>
    public static string? ToGameText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (line.Length == 0)
            return null;
        return line.Length > MaxGameMessage ? line[..(MaxGameMessage - 1)] + "…" : line;
    }

    /// <summary>Relays a message from the Discord channel into the game and website chat.</summary>
    public async Task<RelayOutcome> FromDiscordAsync(ulong discordUserId, string text, CancellationToken cancellationToken = default)
    {
        if (ToGameText(text) is not { } line || IsLinkCode(line))
            return RelayOutcome.Empty;
        if (_channels.Get(_options.ChatChannel) is not { } channel)
            return RelayOutcome.Empty;

        using var scope = _scopes.CreateScope();
        var link = await scope.ServiceProvider.GetRequiredService<IDiscordLinkRepository>()
            .GetByDiscordIdAsync(discordUserId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return RelayOutcome.NotLinked;
        var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(link.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return RelayOutcome.NotLinked;
        if (user.IsRestricted || user.SilenceEndUtc > _time.GetUtcNow())
            return RelayOutcome.Muted;

        var writer = new BanchoPacketWriter(64 + (line.Length * 2));
        ChatEncoder.WriteMessage(writer, new BanchoMessage(user.Username, line, channel.Name, user.Id));
        var framed = writer.ToArray();
        foreach (var member in channel.Members)
            member.Enqueue(framed);
        _chatLog.Record(user.Id, user.Username, channel.Name, line, origin: ChatOrigin.Discord);
        _website?.ForwardChannelMessage(user.Id, channel.Name, line);
        return RelayOutcome.Relayed;
    }

    public override void Dispose()
    {
        _chatLog.Recorded -= OnRecorded;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new StringBuilder();
        while (await _toDiscord.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            // Gather what arrives within a second into one post (Discord allows 2000 characters).
            await Task.Delay(TimeSpan.FromSeconds(1), _time, stoppingToken).ConfigureAwait(false);
            while (_toDiscord.Reader.TryPeek(out var line) && batch.Length + line.Length + 1 <= 1900)
            {
                _toDiscord.Reader.TryRead(out _);
                if (batch.Length > 0)
                    batch.Append('\n');
                batch.Append(line.Length > 1900 ? line[..1900] : line);
            }
            if (batch.Length == 0 && _toDiscord.Reader.TryRead(out var tooLong))
                batch.Append(tooLong[..Math.Min(1900, tooLong.Length)]);

            if (_gateway.IsConnected)
                await _gateway.PostAsync(_options.ChatChannelId, new DiscordPost(batch.ToString()), stoppingToken).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private void OnRecorded(ChatLogEntry entry, ChatOrigin origin)
    {
        if (origin == ChatOrigin.Discord || entry.RecipientId is not null || _options.ChatChannelId == 0
            || !string.Equals(entry.Target, _options.ChatChannel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (ToDiscordLine(entry.SenderName, entry.Text) is { } line)
            _toDiscord.Writer.TryWrite(line);
    }

    private static bool IsLinkCode(string text) =>
        text.TrimStart().StartsWith("!link", true, CultureInfo.InvariantCulture);
}
