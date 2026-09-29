using System.Collections.Concurrent;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>Friends kept in memory, without the website's accounts (lost on restart).</summary>
public sealed class InMemoryFriendRepository : IFriendRepository
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> _friends = new();

    public Task<IReadOnlyList<int>> GetFriendIdsAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<int>>(_friends.TryGetValue(userId, out var set) ? [.. set.Keys] : []);

    public Task AddAsync(int userId, int friendId, CancellationToken cancellationToken = default)
    {
        _friends.GetOrAdd(userId, _ => new())[friendId] = 0;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int userId, int friendId, CancellationToken cancellationToken = default)
    {
        if (_friends.TryGetValue(userId, out var set))
            set.TryRemove(friendId, out _);
        return Task.CompletedTask;
    }
}
