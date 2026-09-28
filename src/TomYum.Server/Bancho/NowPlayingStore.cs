using System.Collections.Concurrent;

namespace TomYum.Server.Bancho;

/// <summary>
/// The last <c>/np</c> of each player, whichever client or chat it came from (stable in-game,
/// lazer or the website through website chat), so <c>!map</c> works from any of them.
/// </summary>
public sealed class NowPlayingStore
{
    private readonly ConcurrentDictionary<int, NowPlaying> _last = new();

    public void Set(int userId, NowPlaying nowPlaying)
    {
        ArgumentNullException.ThrowIfNull(nowPlaying);
        _last[userId] = nowPlaying;
    }

    public NowPlaying? Get(int userId) => _last.TryGetValue(userId, out var nowPlaying) ? nowPlaying : null;
}
