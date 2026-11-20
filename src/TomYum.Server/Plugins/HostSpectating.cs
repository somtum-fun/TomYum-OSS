using Microsoft.Extensions.DependencyInjection;

using TomYum.Bancho;
using TomYum.Domain.Repositories;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets.Spectate;

namespace TomYum.Server.Plugins;

/// <summary><see cref="IHostSpectating"/>: BanchoBot spectating osu!stable players for plugins.</summary>
internal sealed class HostSpectating : IHostSpectating
{
    private readonly PlayerRegistry _players;
    private readonly IServiceScopeFactory? _scopes;

    public HostSpectating(PlayerRegistry players, IServiceScopeFactory? scopes = null)
    {
        _players = players;
        _scopes = scopes;
    }

    public bool AttachBotQuietly(long hostUserId)
    {
        if (Find(hostUserId) is not { } host)
            return false;
        SpectatorManager.AttachBotQuietly(host);
        return true;
    }

    public async Task<bool> ShowWatcherAsync(long hostUserId, long watcherUserId, CancellationToken cancellationToken = default)
    {
        if (Find(hostUserId) is not { } host || watcherUserId is <= 0 or > int.MaxValue || watcherUserId == hostUserId
            || watcherUserId == BanchoBot.UserId)
            return false;
        if (SpectatorManager.ShownWatchers(host).Contains((int)watcherUserId))
            return true;
        // Watching in osu!stable too: the client already knows them as a spectator.
        if (host.Spectators.Contains((int)watcherUserId))
            return false;

        if (await PresenceOfAsync((int)watcherUserId, cancellationToken).ConfigureAwait(false) is not { } presence)
            return false;
        SpectatorManager.ShowWatcher(host, presence);
        return true;
    }

    public bool HideWatcher(long hostUserId, long watcherUserId) =>
        Find(hostUserId) is { } host && watcherUserId is > 0 and <= int.MaxValue && SpectatorManager.HideWatcher(host, (int)watcherUserId, _players);

    public IReadOnlyCollection<long> ShownWatchers(long hostUserId) =>
        Find(hostUserId) is { } host ? SpectatorManager.ShownWatchers(host).Select(id => (long)id).ToArray() : [];

    // The watcher as osu!stable shows a player: from their osu!stable session when they have one, else their account.
    private async Task<BanchoPresence?> PresenceOfAsync(int userId, CancellationToken cancellationToken)
    {
        if (_players.GetByUserId(userId) is { } online)
            return online.Presence;
        if (_scopes is null)
            return null;

        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetService<IUserRepository>();
        if (users is null || await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { IsRestricted: false } user)
            return null;

        return new BanchoPresence
        {
            UserId = user.Id,
            Username = user.Username,
            CountryCode = CountryCodes.ToNumeric(user.Country),
        };
    }

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

    public string BotName => BanchoBot.Name;

    // osu!stable sessions only: IRC clients send no frames.
    private OnlinePlayer? Find(long userId) =>
        userId is > 0 and <= int.MaxValue && userId != BanchoBot.UserId && _players.GetByUserId((int)userId) is { IsIrc: false } player
            ? player
            : null;
}
