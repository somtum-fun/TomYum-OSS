using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Friends in the website's <c>phpbb_zebra</c> table, so they're the same in osu!stable, lazer and on
/// the website. A row is a friend (<c>friend = 1</c>) or a block (<c>foe = 1</c>); adding a friend
/// replaces a block, like the website does.
/// </summary>
public sealed class OsuWebFriends : IFriendRepository
{
    private readonly OsuWebDbContext _db;

    public OsuWebFriends(OsuWebDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<int>> GetFriendIdsAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<int>(
            $"SELECT CAST(zebra_id AS SIGNED) AS Value FROM phpbb_zebra WHERE user_id = {userId} AND friend = 1")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task AddAsync(int userId, int friendId, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO phpbb_zebra (user_id, zebra_id, friend, foe) VALUES ({userId}, {friendId}, 1, 0) ON DUPLICATE KEY UPDATE friend = 1, foe = 0",
            cancellationToken);

    public Task RemoveAsync(int userId, int friendId, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM phpbb_zebra WHERE user_id = {userId} AND zebra_id = {friendId} AND friend = 1",
            cancellationToken);

    /// <summary>The website's rules (<c>checkChatPmStart</c>): its blocks, and "friends only" (<c>user_allow_pm = 0</c>).</summary>
    public async Task<bool> MayMessageAsync(int senderId, int targetId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<int>(
            $"""
            SELECT CAST(
              NOT EXISTS (SELECT 1 FROM phpbb_zebra WHERE foe = 1
                AND ((user_id = {senderId} AND zebra_id = {targetId}) OR (user_id = {targetId} AND zebra_id = {senderId})))
              AND (COALESCE((SELECT user_allow_pm FROM phpbb_users WHERE user_id = {targetId}), 1) = 1
                OR EXISTS (SELECT 1 FROM phpbb_zebra WHERE user_id = {targetId} AND zebra_id = {senderId} AND friend = 1))
            AS SIGNED) AS Value
            """).SingleAsync(cancellationToken).ConfigureAwait(false) == 1;
}
