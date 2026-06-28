using System.Globalization;
using TomYum.Bancho;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Bancho;

/// <summary>
/// The chat-command surface for <see cref="BanchoBot"/>: parses <c>!</c>-prefixed messages and
/// produces the bot's reply. Kept separate from <see cref="BanchoBot"/> (which owns the bot's
/// identity/presence) so the command set can grow without bloating the player object — add a case
/// here, not there.
/// </summary>
/// <remarks>
/// Commands read off the in-memory singleton stores (players, channels, beatmap cache) for
/// reporting. Staff-only commands (e.g. <c>!stats</c>, which exposes internal resource counts) are
/// gated on the caller's <see cref="BanchoPrivileges"/>. A richer framework (per-command metadata,
/// argument parsing, cooldowns) is a later slice; for now a switch is enough.
/// </remarks>
public sealed class BanchoBotCommands
{
    private readonly TomYumVersion _version;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly BeatmapService _beatmaps;

    /// <summary>Creates the command set over the running version and the stores it reports on (all singletons).</summary>
    public BanchoBotCommands(TomYumVersion version, PlayerRegistry players, ChannelRegistry channels, BeatmapService beatmaps)
    {
        _version = version;
        _players = players;
        _channels = channels;
        _beatmaps = beatmaps;
    }

    /// <summary>
    /// Produces the bot's reply to a command, or <c>null</c> when the input is not a command the
    /// bot recognises (so callers stay silent rather than echo an error for ordinary chat).
    /// </summary>
    /// <param name="sender">The display name of the player who invoked the command.</param>
    /// <param name="message">The raw message text (may or may not start with <c>!</c>).</param>
    /// <param name="privileges">The caller's client privileges, used to gate staff-only commands.</param>
    public string? TryHandle(string sender, string message, BanchoPrivileges privileges)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '!')
        {
            return null;
        }

        var parts = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var command = parts[0].ToLowerInvariant();
        return command switch
        {
            "help" => Help(privileges),
            "ping" => "pong!",
            "version" => string.Create(CultureInfo.InvariantCulture, $"TomYum {_version.Display}"),
            "roll" => Roll(sender, parts),
            "stats" => IsStaff(privileges) ? Stats() : "That command is staff-only.",
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown command \"{command}\". Try !help."),
        };
    }

    /// <summary>Whether the caller holds administrator (or developer) privileges.</summary>
    private static bool IsStaff(BanchoPrivileges privileges) =>
        privileges.HasFlag(BanchoPrivileges.Administrator) || privileges.HasFlag(BanchoPrivileges.Developer);

    /// <summary>The help line, listing the staff-only <c>!stats</c> command only for staff.</summary>
    private static string Help(BanchoPrivileges privileges) => IsStaff(privileges)
        ? "Commands: !help, !ping, !roll [max], !version, !stats (staff). More to come."
        : "Commands: !help, !ping, !roll [max], !version. More to come.";

    private static string Roll(string sender, string[] parts)
    {
        var max = 100;
        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) && requested > 0)
        {
            max = requested;
        }

        var value = Random.Shared.Next(1, max + 1);
        return string.Create(CultureInfo.InvariantCulture, $"{sender} rolls {value} point(s)! (1-{max})");
    }

    /// <summary>
    /// Reports the server's in-memory footprint on one line: online players (including the bot),
    /// total bytes buffered across all per-player outbound queues (the real bloat vector — a
    /// registered session that stops polling keeps its bytes pending), channel count, cached
    /// beatmaps, and managed heap size. Cheap, read-only reads off the singleton stores.
    /// </summary>
    private string Stats()
    {
        var players = _players.All;
        long queuedBytes = 0;
        foreach (var player in players)
        {
            queuedBytes += player.QueuedBytes;
        }

        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Players: {players.Count} | Queued: {queuedBytes / 1024.0:F1} KB | Channels: {_channels.All.Count} | Beatmaps cached: {_beatmaps.CachedCount} | Heap: {heapMb:F1} MB");
    }
}
