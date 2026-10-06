using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets.Spectate;

namespace TomYum.Server.Plugins;

/// <summary><see cref="IHostSpectating"/>: BanchoBot spectating osu!stable players for plugins.</summary>
internal sealed class HostSpectating : IHostSpectating
{
    private readonly PlayerRegistry _players;

    public HostSpectating(PlayerRegistry players) => _players = players;

    public bool AttachBot(long hostUserId)
    {
        if (Find(hostUserId) is not { } host)
            return false;
        SpectatorManager.AttachBot(host, _players);
        return true;
    }

    public bool DetachBot(long hostUserId) =>
        Find(hostUserId) is { } host && SpectatorManager.DetachBot(host, _players);

    public bool IsBotWatching(long hostUserId) =>
        Find(hostUserId) is { } host && host.Spectators.Contains(BanchoBot.UserId);

    // osu!stable sessions only: IRC clients send no frames.
    private OnlinePlayer? Find(long userId) =>
        userId is > 0 and <= int.MaxValue && userId != BanchoBot.UserId && _players.GetByUserId((int)userId) is { IsIrc: false } player
            ? player
            : null;
}
