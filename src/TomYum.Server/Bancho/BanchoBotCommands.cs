using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
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
/// reporting, and the user repository for privilege changes. Staff-only commands (e.g.
/// <c>!stats</c>, <c>!setpriv</c>) are gated on the caller's <see cref="BanchoPrivileges"/>. A
/// richer framework (per-command metadata, argument parsing, cooldowns) is a later slice; for now
/// a switch is enough.
/// </remarks>
public sealed class BanchoBotCommands
{
    private readonly TomYumVersion _version;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly BeatmapService _beatmaps;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Creates the command set over the running version, the stores it reports on, and a scope
    /// factory used to resolve the (scoped) user repository per command. The factory is needed
    /// because this type is a singleton while the EF repository is scoped to a request/DbContext.
    /// </summary>
    public BanchoBotCommands(
        TomYumVersion version,
        PlayerRegistry players,
        ChannelRegistry channels,
        BeatmapService beatmaps,
        IServiceScopeFactory scopeFactory)
    {
        _version = version;
        _players = players;
        _channels = channels;
        _beatmaps = beatmaps;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Produces the bot's reply to a command, or <c>null</c> when the input is not a command the
    /// bot recognises (so callers stay silent rather than echo an error for ordinary chat).
    /// </summary>
    /// <param name="caller">The player who invoked the command (the acting user for staff commands).</param>
    /// <param name="message">The raw message text (may or may not start with <c>!</c>).</param>
    /// <param name="privileges">The caller's client privileges, used to gate staff-only commands.</param>
    /// <param name="cancellationToken">Cancels any repository work a command performs.</param>
    public async Task<string?> TryHandleAsync(
        OnlinePlayer caller,
        string message,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken = default)
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
            "roll" => Roll(caller.Presence.Username, parts),
            "stats" => IsStaff(privileges) ? Stats() : StaffOnly,
            "setpriv" => IsStaff(privileges)
                ? await SetPrivAsync(parts, cancellationToken).ConfigureAwait(false)
                : StaffOnly,
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown command \"{command}\". Try !help."),
        };
    }

    private const string StaffOnly = "That command is staff-only.";

    /// <summary>Whether the caller holds administrator (or developer) privileges.</summary>
    private static bool IsStaff(BanchoPrivileges privileges) =>
        privileges.HasFlag(BanchoPrivileges.Administrator) || privileges.HasFlag(BanchoPrivileges.Developer);

    /// <summary>The help line, listing staff-only commands only for staff.</summary>
    private static string Help(BanchoPrivileges privileges) => IsStaff(privileges)
        ? "Commands: !help, !ping, !roll [max], !version, !stats (staff), !setpriv <user> <role> (staff)."
        : "Commands: !help, !ping, !roll [max], !version.";

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

    /// <summary>
    /// <c>!setpriv &lt;username&gt; &lt;role&gt;</c> — promotes/demotes another account. Roles map to
    /// <see cref="UserPrivileges"/>: <c>owner</c>, <c>admin</c>, <c>dev</c>, <c>mod</c>,
    /// <c>supporter</c>, <c>normal</c>. Persists through the repository so it survives the player
    /// reconnecting; an online target's change takes effect on their next login (presence is built
    /// from the stored privileges then). Staff-only — gated by the caller before this runs.
    /// </summary>
    private async Task<string> SetPrivAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 3)
        {
            return "Usage: !setpriv <username> <owner|admin|dev|mod|supporter|normal>";
        }

        if (!TryParseRole(parts[2], out var privileges))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Unknown role \"{parts[2]}\". Valid: owner, admin, dev, mod, supporter, normal.");
        }

        var targetName = parts[1];

        // Resolve the scoped user repository for this command (this type is a singleton; the EF
        // repository + its DbContext are scoped, so we open a short-lived scope per write).
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var user = await users.GetBySafeNameAsync(User.MakeSafeName(targetName), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"No such user \"{targetName}\".");
        }

        await users.UpdateAsync(WithPrivileges(user, privileges), cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture,
            $"{user.Username} is now {privileges}. (Takes effect on their next login.)");
    }

    /// <summary>Maps a role keyword to the <see cref="UserPrivileges"/> flag set it grants.</summary>
    private static bool TryParseRole(string role, out UserPrivileges privileges)
    {
        privileges = role.ToLowerInvariant() switch
        {
            "owner" => UserPrivileges.Owner,
            "admin" or "administrator" => UserPrivileges.Normal | UserPrivileges.Administrator,
            "dev" or "developer" => UserPrivileges.Normal | UserPrivileges.Administrator | UserPrivileges.Developer,
            "mod" or "moderator" => UserPrivileges.Normal | UserPrivileges.Moderator,
            "supporter" => UserPrivileges.Normal | UserPrivileges.Supporter,
            "normal" or "player" => UserPrivileges.Normal,
            _ => UserPrivileges.None,
        };

        return privileges != UserPrivileges.None;
    }

    /// <summary>Returns a copy of <paramref name="user"/> with its <see cref="User.Privileges"/> replaced.</summary>
    private static User WithPrivileges(User user, UserPrivileges privileges) => new()
    {
        Id = user.Id,
        Username = user.Username,
        SafeName = user.SafeName,
        Email = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country = user.Country,
        CreationTime = user.CreationTime,
        LatestActivity = user.LatestActivity,
        ApiKey = user.ApiKey,
        Privileges = privileges,
    };
}
