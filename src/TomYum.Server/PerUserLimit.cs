using System.Collections.Concurrent;

namespace TomYum.Server;

/// <summary>
/// At most <c>limit</c> of something per player in any <c>window</c> (screenshot uploads, say). Kept in
/// memory: a restart starts everyone over.
/// </summary>
public sealed class PerUserLimit
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, Queue<DateTimeOffset>> _taken = new();

    /// <param name="limit">How many are allowed in a window.</param>
    /// <param name="window">How long the window is.</param>
    /// <param name="time">The clock (tests give their own).</param>
    public PerUserLimit(int limit, TimeSpan window, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        _window = window;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Counts one for <paramref name="userId"/> if they're under the limit; false when they aren't.</summary>
    public bool TryTake(int userId)
    {
        var now = _time.GetUtcNow();
        var taken = _taken.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (taken)
        {
            while (taken.Count > 0 && now - taken.Peek() >= _window)
                taken.Dequeue();
            if (taken.Count >= _limit)
                return false;
            taken.Enqueue(now);
            return true;
        }
    }
}
