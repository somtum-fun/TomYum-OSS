using System.Collections.Concurrent;

namespace TomYum.Domain.Services;

/// <summary>
/// The ids of restricted players, kept in memory so leaderboards and global ranks can leave them
/// out cheaply. Loaded at startup and updated when staff restrict or unrestrict someone. Thread-safe.
/// </summary>
public sealed class RestrictedUsers
{
    private readonly ConcurrentDictionary<int, byte> _ids = new();

    /// <summary>A snapshot of the restricted ids.</summary>
    public IReadOnlyCollection<int> Ids => _ids.Keys.ToArray();

    public bool Contains(int userId) => _ids.ContainsKey(userId);

    public void Set(int userId, bool restricted)
    {
        if (restricted)
            _ids.TryAdd(userId, 0);
        else
            _ids.TryRemove(userId, out _);
    }

    /// <summary>Replaces the whole set (startup load).</summary>
    public void Load(IEnumerable<int> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        _ids.Clear();
        foreach (var id in userIds)
            _ids.TryAdd(id, 0);
    }
}
