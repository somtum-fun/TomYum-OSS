namespace TomYum.Domain.Entities;

/// <summary>A chat message as delivered in-game, kept for staff (GMT panel chat logs).</summary>
public sealed class ChatLogEntry
{
    public long Id { get; init; }

    /// <summary>When it was sent (UTC).</summary>
    public DateTime SentAt { get; init; }

    public int SenderId { get; init; }

    public string SenderName { get; init; } = string.Empty;

    /// <summary>The channel (<c>#osu</c>, <c>#multiplayer</c>, …) or, for a private message, the recipient's name.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The recipient's id for a private message; null for channel messages.</summary>
    public int? RecipientId { get; init; }

    public string Text { get; init; } = string.Empty;
}
