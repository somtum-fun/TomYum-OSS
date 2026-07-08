using TomYum.Server.Bancho;
using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Read-only projection of the live <see cref="PlayerRegistry"/> to the plugin-facing
/// <see cref="IHostPlayerView"/>. Virtual bots (BanchoBot) are excluded so plugins see real users.
/// </summary>
internal sealed class HostPlayerView : IHostPlayerView
{
    private readonly PlayerRegistry _players;

    public HostPlayerView(PlayerRegistry players) => _players = players;

    public int OnlineCount => _players.All.Count(p => p.UserId != BanchoBot.UserId);

    public IReadOnlyList<PluginPlayerInfo> OnlinePlayers =>
        _players.All.Where(p => p.UserId != BanchoBot.UserId).Select(Project).ToList();

    public PluginPlayerInfo? FindByUserId(long userId)
    {
        if (userId is < int.MinValue or > int.MaxValue)
            return null;

        var player = _players.GetByUserId((int)userId);
        return player is null ? null : Project(player);
    }

    public PluginPlayerInfo? FindByUsername(string username)
    {
        var player = _players.GetByUsername(username);
        return player is null ? null : Project(player);
    }

    private static PluginPlayerInfo Project(OnlinePlayer player)
    {
        var p = player.Presence;
        return new PluginPlayerInfo(
            p.UserId,
            p.Username,
            p.CountryCode,
            p.Action,
            p.ActionInfo,
            p.BeatmapMd5,
            p.BeatmapId,
            (uint)p.Mods,
            p.GameMode);
    }
}
