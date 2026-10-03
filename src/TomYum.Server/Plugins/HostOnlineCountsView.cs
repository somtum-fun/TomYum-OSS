using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Plugins;

/// <summary>
/// <see cref="IHostOnlineCounts"/> over the live <see cref="PlayerRegistry"/> (stable and IRC),
/// <see cref="LazerPresence"/> (lazer, when Redis is configured) and <see cref="MatchRegistry"/>.
/// </summary>
internal sealed class HostOnlineCountsView : IHostOnlineCounts
{
    private readonly PlayerRegistry _players;
    private readonly MatchRegistry _matches;
    private readonly LazerPresence? _lazer;

    public HostOnlineCountsView(PlayerRegistry players, MatchRegistry matches, LazerPresence? lazer = null)
    {
        _players = players;
        _matches = matches;
        _lazer = lazer;
    }

    public async Task<HostOnlineCounts> GetAsync(CancellationToken cancellationToken = default)
    {
        int stable = 0, irc = 0;
        foreach (var player in _players.All)
        {
            if (player.UserId == BanchoBot.UserId)
                continue;
            if (player.IsIrc)
                irc++;
            else
                stable++;
        }

        int? lazer = null;
        if (_lazer is not null && await _lazer.TryGetOnlineAsync().ConfigureAwait(false) is { } online)
            lazer = online.Count(p => p.UserId != BanchoBot.UserId);

        return new HostOnlineCounts(stable, irc, lazer, _matches.Count);
    }
}
