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
public sealed record DiscordPost(string? Content = null, DiscordEmbed? Embed = null)
{
    /// <summary>Buttons under the message (up to 5): actions the bot handles, or links.</summary>
    public IReadOnlyList<DiscordButton> Buttons { get; init; } = [];

    /// <summary>An image sent with the message; the embed shows it with <c>attachment://{name}</c>.</summary>
    public DiscordFile? File { get; init; }
}

/// <summary>How a button looks.</summary>
public enum DiscordButtonStyle
{
    Primary,
    Secondary,
    Success,
    Danger,
    Link,
}

/// <summary>
/// A button: an action (<see cref="CustomId"/>, handled by <see cref="DiscordInteractions"/>) or a
/// <see cref="DiscordButtonStyle.Link"/> to <see cref="Url"/>.
/// </summary>
public sealed record DiscordButton(string Label, DiscordButtonStyle Style, string? CustomId = null, string? Url = null)
{
    /// <summary>Shown greyed out (e.g. "previous" on the first page).</summary>
    public bool Disabled { get; init; }
}

/// <summary>A file sent with a message.</summary>
public sealed record DiscordFile(string Name, byte[] Bytes);

/// <summary>
/// What pressing a button does: <see cref="Update"/> replaces the message the button is on (null
/// keeps it); <see cref="Note"/> is added to the message's embed and its buttons are taken away
/// (a report someone dealt with); <see cref="Reply"/> is a message only the presser sees.
/// </summary>
public sealed record DiscordButtonResult(DiscordPost? Update, string? Reply, string? Note = null)
{
    public static DiscordButtonResult Say(string reply) => new(null, reply);

    public static DiscordButtonResult Handled(string note) => new(null, null, note);
}

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

    /// <summary>Posts and returns the message's id (null when it couldn't be posted).</summary>
    Task<ulong?> SendAsync(ulong channelId, DiscordPost post, CancellationToken cancellationToken = default);

    /// <summary>Replaces a message the bot posted; false when it's gone.</summary>
    Task<bool> EditAsync(ulong channelId, ulong messageId, DiscordPost post, CancellationToken cancellationToken = default);

    /// <summary>Pins a message.</summary>
    Task PinAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default);

    /// <summary>The id of the server's role called <paramref name="name"/>, made (with <paramref name="color"/>) if there's none; 0 when that fails.</summary>
    Task<ulong> EnsureRoleAsync(string name, uint color, CancellationToken cancellationToken = default);

    /// <summary>The bot's own status line ("Watching …").</summary>
    Task SetActivityAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Gives (or takes) one role; false when the member isn't in the server or it can't.</summary>
    Task<bool> SetRoleAsync(ulong discordUserId, ulong roleId, bool give, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the member exactly <paramref name="wanted"/> out of <paramref name="managed"/> (other
    /// roles untouched). False when they aren't in the server.
    /// </summary>
    Task<bool> SyncRolesAsync(ulong discordUserId, IReadOnlySet<ulong> managed, IReadOnlySet<ulong> wanted, CancellationToken cancellationToken = default);
}
