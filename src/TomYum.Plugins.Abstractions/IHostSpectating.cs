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
}
