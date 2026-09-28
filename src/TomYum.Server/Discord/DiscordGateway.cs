namespace TomYum.Server.Discord;

/// <summary>A field of a <see cref="DiscordEmbed"/>.</summary>
public sealed record DiscordField(string Name, string Value, bool Inline = true);

/// <summary>A Discord embed, independent of the Discord library (so the bot's logic is testable).</summary>
public sealed record DiscordEmbed
{
    public string? Title { get; init; }

    public string? Url { get; init; }

    public string? Description { get; init; }

    /// <summary>RGB, e.g. <c>0xFF66AA</c>.</summary>
    public uint? Color { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorUrl { get; init; }

    public string? AuthorIconUrl { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string? ImageUrl { get; init; }

    public string? Footer { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public IReadOnlyList<DiscordField> Fields { get; init; } = [];
}

/// <summary>A message to post: text, an embed, or both. Never pings anyone.</summary>
public sealed record DiscordPost(string? Content = null, DiscordEmbed? Embed = null);

/// <summary>A slash command's answer; <see cref="Private"/> ones only the caller sees.</summary>
public sealed record DiscordReply(DiscordPost Post, bool Private = false)
{
    public static DiscordReply Text(string text, bool isPrivate = false) => new(new DiscordPost(text), isPrivate);
}

/// <summary>What the bot needs from Discord (implemented over Discord.Net by <see cref="DiscordBotService"/>).</summary>
public interface IDiscordGateway
{
    /// <summary>Whether the bot is connected; posts made while it isn't are dropped.</summary>
    bool IsConnected { get; }

    /// <summary>Posts to a channel of the configured server (ignored for channel 0).</summary>
    Task PostAsync(ulong channelId, DiscordPost post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the member exactly <paramref name="wanted"/> out of <paramref name="managed"/> (other
    /// roles untouched). False when they aren't in the server.
    /// </summary>
    Task<bool> SyncRolesAsync(ulong discordUserId, IReadOnlySet<ulong> managed, IReadOnlySet<ulong> wanted, CancellationToken cancellationToken = default);
}
