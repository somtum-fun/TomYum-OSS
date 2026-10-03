namespace TomYum.Plugins.Abstractions;

/// <summary>
/// How many players are online on each client, published to plugins via
/// <see cref="IPluginContext.GetHostService{TService}"/>. <see cref="IHostPlayerView"/> lists the
/// bancho sessions (stable and IRC together); this adds lazer, which the host reads from the
/// spectator server's presence feed, and the open stable multiplayer rooms.
/// </summary>
public interface IHostOnlineCounts
{
    /// <summary>Counts the players online right now (virtual bots like BanchoBot left out).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts. <see cref="HostOnlineCounts.Lazer"/> is null when the lazer feed can't be read.</returns>
    Task<HostOnlineCounts> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Players online by client.</summary>
/// <param name="Stable">osu!stable sessions (tournament client windows left out).</param>
/// <param name="Irc">IRC sessions.</param>
/// <param name="Lazer">osu!lazer players, or null when the host can't tell right now (no Redis, or it failed).</param>
/// <param name="MultiplayerMatches">osu!stable multiplayer rooms open right now.</param>
public sealed record HostOnlineCounts(int Stable, int Irc, int? Lazer, int MultiplayerMatches);
