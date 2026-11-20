namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Lets a plugin have BanchoBot spectate an osu!stable player, published to plugins via
/// <see cref="IPluginContext.GetHostService{TService}"/>. While BanchoBot watches, the player's client
/// streams its replay frames, so <see cref="SpectateFrameEvent"/> is raised for them even with no
/// human spectator (osu!dan uses this to see pauses). The player sees BanchoBot join as a spectator.
/// BanchoBot can watch any number of players at once; nothing is queued for it.
/// </summary>
public interface IHostSpectating
{
    /// <summary>BanchoBot starts spectating the player (nothing changes when it already is).</summary>
    /// <param name="hostUserId">The player to watch.</param>
    /// <returns>True when BanchoBot is watching them now; false when they aren't online in osu!stable.</returns>
    bool AttachBot(long hostUserId);

    /// <summary>BanchoBot stops spectating the player.</summary>
    /// <param name="hostUserId">The player.</param>
    /// <returns>True when BanchoBot was watching them and has stopped.</returns>
    bool DetachBot(long hostUserId);

    /// <summary>Whether BanchoBot is spectating the player right now.</summary>
    /// <param name="hostUserId">The player.</param>
    /// <returns>True when it is (false when they're offline).</returns>
    bool IsBotWatching(long hostUserId);

    /// <summary>
    /// BanchoBot starts spectating the player without the player's client being told (no "BanchoBot is spectating"):
    /// for a plugin that shows the player who really watches instead (<see cref="ShowWatcherAsync"/>, shown first, so
    /// the client has a spectator and streams). Hosts without it attach the bot as <see cref="AttachBot"/> does.
    /// </summary>
    /// <param name="hostUserId">The player to watch.</param>
    /// <returns>True when BanchoBot is watching them now; false when they aren't online in osu!stable.</returns>
    bool AttachBotQuietly(long hostUserId) => AttachBot(hostUserId);

    /// <summary>
    /// Shows the player that <paramref name="watcherUserId"/> (an account watching from elsewhere, such as osu!lazer)
    /// spectates them: their client gets that account's name and country, and lists it as a spectator. Nothing is
    /// sent to the watcher; frames still come through BanchoBot. Hosts without it show nobody.
    /// </summary>
    /// <param name="hostUserId">The player.</param>
    /// <param name="watcherUserId">The account watching them.</param>
    /// <param name="cancellationToken">Cancels the account lookup.</param>
    /// <returns>True when the watcher is shown now (or already was).</returns>
    Task<bool> ShowWatcherAsync(long hostUserId, long watcherUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// The player stops seeing <paramref name="watcherUserId"/> as a spectator. When it was the last one shown and
    /// BanchoBot was watching quietly, the player sees BanchoBot instead, so their client keeps streaming.
    /// </summary>
    /// <param name="hostUserId">The player.</param>
    /// <param name="watcherUserId">The account that stopped watching.</param>
    /// <returns>True when they were shown.</returns>
    bool HideWatcher(long hostUserId, long watcherUserId) => false;

    /// <summary>The accounts <see cref="ShowWatcherAsync"/> shows the player now (this session of theirs).</summary>
    /// <param name="hostUserId">The player.</param>
    /// <returns>Their ids (empty when none, or the player is offline).</returns>
    IReadOnlyCollection<long> ShownWatchers(long hostUserId) => Array.Empty<long>();

    /// <summary>
    /// The bot's display name (<c>Bancho:BotName</c>), for messages that mention it: a server moved from
    /// bancho.py may keep its old bot's name. Hosts that don't say get osu!'s, BanchoBot.
    /// </summary>
    string BotName => "BanchoBot";
}
