namespace TomYum.Domain.Entities;

/// <summary>
/// A game account linked to a Discord account (one each way). Made when the player types the code
/// the Discord bot gave them to BanchoBot, which proves they own both.
/// </summary>
public sealed class DiscordLink
{
    public int UserId { get; init; }

    /// <summary>The Discord user's snowflake id.</summary>
    public ulong DiscordId { get; init; }

    public DateTime LinkedAt { get; init; }
}
