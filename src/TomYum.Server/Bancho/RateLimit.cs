namespace TomYum.Server.Bancho;

/// <summary>
/// At most <c>limit</c> of something in any <c>period</c> (a sliding window), e.g. a player's chat
/// messages or new rooms. Thread-safe.
/// </summary>
public sealed class RateLimit
{
    private readonly Queue<DateTimeOffset> _times = new();
    private readonly int _limit;
    private readonly TimeSpan _period;

    /// <summary>Allows <paramref name="limit"/> in any <paramref name="period"/>.</summary>
    public RateLimit(int limit, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        _period = period;
    }

    /// <summary>Counts one at <paramref name="now"/>; false (and not counted) when the window is already full.</summary>
    public bool TryTake(DateTimeOffset now)
    {
        lock (_times)
        {
            while (_times.Count > 0 && now - _times.Peek() >= _period)
                _times.Dequeue();
            if (_times.Count >= _limit)
                return false;
            _times.Enqueue(now);
            return true;
        }
    }
}
