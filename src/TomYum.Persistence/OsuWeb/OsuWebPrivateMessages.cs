using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A private message someone sent a player on the website (or in-game while they were away).</summary>
public sealed record UnreadMessage(long MessageId, int SenderId, string SenderName, string Content, bool IsAction);

/// <summary>
/// Players' private messages in website chat (<c>osu_chat</c>): the unread ones, for osu!stable to show
/// at login, and marking conversations read (stable's <c>osu-markasread.php</c>, and at logout, when
/// everything that arrived was shown in-game).
/// </summary>
public sealed class OsuWebPrivateMessages
{
    private readonly OsuWebDbContext _db;
    private readonly string _chat;

    /// <param name="db">The website's database.</param>
    /// <param name="chatDatabase">The website's chat database (osu-web's <c>DB_DATABASE_CHAT</c>).</param>
    public OsuWebPrivateMessages(OsuWebDbContext db, string chatDatabase = "osu_chat")
    {
        _db = db;
        _chat = "`" + chatDatabase.Replace("`", string.Empty, StringComparison.Ordinal) + "`";
    }

    /// <summary>The newest <paramref name="limit"/> unread messages others sent <paramref name="userId"/> privately, oldest first.</summary>
    public async Task<IReadOnlyList<UnreadMessage>> UnreadAsync(int userId, int limit, CancellationToken cancellationToken = default)
    {
#pragma warning disable EF1002 // the schema name is ours (configuration), the values are parameters
        var messages = await _db.Database.SqlQueryRaw<UnreadMessage>(
            $$"""
            SELECT CAST(m.message_id AS SIGNED) AS MessageId, CAST(m.user_id AS SIGNED) AS SenderId, u.username AS SenderName,
              m.content AS Content, m.is_action AS IsAction
            FROM {{_chat}}.user_channels uc
            JOIN {{_chat}}.channels c ON c.channel_id = uc.channel_id AND c.type = 'PM'
            JOIN {{_chat}}.messages m ON m.channel_id = uc.channel_id AND m.message_id > COALESCE(uc.last_read_id, 0) AND m.user_id <> {0}
            JOIN phpbb_users u ON u.user_id = m.user_id
            WHERE uc.user_id = {0}
            ORDER BY m.message_id DESC LIMIT {1}
            """, userId, limit).ToListAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        messages.Reverse();
        return messages;
    }

    /// <summary>Marks the private conversation with <paramref name="otherUserId"/> read for <paramref name="userId"/>.</summary>
    public Task MarkConversationReadAsync(int userId, int otherUserId, CancellationToken cancellationToken = default) =>
        MarkReadAsync(userId, $"#pm_{Math.Min(userId, otherUserId)}-{Math.Max(userId, otherUserId)}", cancellationToken);

    /// <summary>Marks a channel (e.g. <c>#osu</c>) read for <paramref name="userId"/>.</summary>
    public Task MarkChannelReadAsync(int userId, string channel, CancellationToken cancellationToken = default) =>
        MarkReadAsync(userId, channel, cancellationToken);

    /// <summary>Marks every private conversation of <paramref name="userId"/> read.</summary>
    public async Task MarkAllPrivateReadAsync(int userId, CancellationToken cancellationToken = default)
    {
#pragma warning disable EF1002
        await _db.Database.ExecuteSqlRawAsync(
            $$"""
            UPDATE {{_chat}}.user_channels uc JOIN {{_chat}}.channels c ON c.channel_id = uc.channel_id AND c.type = 'PM'
            SET uc.last_read_id = (SELECT MAX(m.message_id) FROM {{_chat}}.messages m WHERE m.channel_id = uc.channel_id)
            WHERE uc.user_id = {0} AND EXISTS (SELECT 1 FROM {{_chat}}.messages m WHERE m.channel_id = uc.channel_id)
            """, [userId], cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    private async Task MarkReadAsync(int userId, string channelName, CancellationToken cancellationToken)
    {
#pragma warning disable EF1002
        await _db.Database.ExecuteSqlRawAsync(
            $$"""
            UPDATE {{_chat}}.user_channels uc JOIN {{_chat}}.channels c ON c.channel_id = uc.channel_id AND c.name = {1}
            SET uc.last_read_id = (SELECT MAX(m.message_id) FROM {{_chat}}.messages m WHERE m.channel_id = uc.channel_id)
            WHERE uc.user_id = {0} AND EXISTS (SELECT 1 FROM {{_chat}}.messages m WHERE m.channel_id = uc.channel_id)
            """, [userId, channelName], cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }
}
