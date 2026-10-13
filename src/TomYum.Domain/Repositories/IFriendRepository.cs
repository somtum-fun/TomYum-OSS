namespace TomYum.Domain.Repositories;

/// <summary>Players' friends (one-way, like osu!'s: adding someone doesn't add you to their list).</summary>
public interface IFriendRepository
{
    /// <summary>The ids <paramref name="userId"/> has added as friends.</summary>
    Task<IReadOnlyList<int>> GetFriendIdsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="friendId"/> to <paramref name="userId"/>'s friends (nothing when already one).</summary>
    Task AddAsync(int userId, int friendId, CancellationToken cancellationToken = default);

    /// <summary>Removes <paramref name="friendId"/> from <paramref name="userId"/>'s friends.</summary>
    Task RemoveAsync(int userId, int friendId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="senderId"/> may send <paramref name="targetId"/> a private message: neither
    /// has blocked the other, and a target who takes messages from friends only has added the sender.
    /// Without blocks to know of, yes.
    /// </summary>
    Task<bool> MayMessageAsync(int senderId, int targetId, CancellationToken cancellationToken = default) => Task.FromResult(true);
}
