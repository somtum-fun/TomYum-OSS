namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Read-only view of online players, published to plugins via
/// <see cref="IPluginContext.GetHostService{TService}"/>. Lets a plugin inspect who is online and
/// what they are doing (status/map/mods) without any dependency on core internals — the safe way
/// for a plugin to "see the server".
/// </summary>
public interface IHostPlayerView
{
    /// <summary>Number of real players currently online (excludes virtual bots like BanchoBot).</summary>
    int OnlineCount { get; }

    /// <summary>A snapshot of all online players.</summary>
    IReadOnlyList<PluginPlayerInfo> OnlinePlayers { get; }

    /// <summary>Looks up an online player by user id, or <c>null</c> when offline.</summary>
    PluginPlayerInfo? FindByUserId(long userId);

    /// <summary>Looks up an online player by (case-insensitive) username, or <c>null</c> when offline.</summary>
    PluginPlayerInfo? FindByUsername(string username);
}

/// <summary>A read-only snapshot of one online player, handed to plugins.</summary>
/// <param name="UserId">The player's user id.</param>
/// <param name="Username">Display name.</param>
/// <param name="CountryCode">osu! numeric country id.</param>
/// <param name="Action">Raw bancho action byte (idle/playing/watching/…).</param>
/// <param name="ActionInfo">Free-text status (e.g. the beatmap title being played).</param>
/// <param name="BeatmapMd5">MD5 of the current beatmap, or empty.</param>
/// <param name="BeatmapId">Current beatmap id, or 0.</param>
/// <param name="Mods">Active mods bitmask.</param>
/// <param name="GameMode">Combined gamemode (0-11: vanilla 0-3, relax +4, autopilot +8).</param>
public sealed record PluginPlayerInfo(
    long UserId,
    string Username,
    int CountryCode,
    byte Action,
    string ActionInfo,
    string BeatmapMd5,
    long BeatmapId,
    uint Mods,
    int GameMode);
