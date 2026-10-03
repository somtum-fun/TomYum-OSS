using System.Collections.Concurrent;
using System.Net;

namespace TomYum.Gateway;

/// <summary>Caps how many connections one address holds at once.</summary>
public sealed class ConnectionLimiter
{
    private readonly ConcurrentDictionary<IPAddress, int> _open = new();
    private readonly int _max;

    public ConnectionLimiter(int max) => _max = Math.Max(1, max);

    /// <summary>Takes a slot for <paramref name="address"/>; false when it already has the most allowed.</summary>
    public bool TryEnter(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        while (true)
        {
            var count = _open.GetOrAdd(address, 0);
            if (count >= _max)
                return false;
            if (_open.TryUpdate(address, count + 1, count))
                return true;
        }
    }

    /// <summary>Gives back a slot taken with <see cref="TryEnter"/>.</summary>
    public void Leave(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        while (_open.TryGetValue(address, out var count))
        {
            if (count <= 1 ? _open.TryRemove(new KeyValuePair<IPAddress, int>(address, count)) : _open.TryUpdate(address, count - 1, count))
                return;
        }
    }
}
