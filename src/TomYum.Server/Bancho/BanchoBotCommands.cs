using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho.Packets.Match;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Bancho;

/// <summary>
/// The chat-command surface for <see cref="BanchoBot"/>: parses <c>!</c>-prefixed messages and
/// produces the bot's reply. Kept separate from <see cref="BanchoBot"/> (which owns the bot's
/// identity/presence) so the command set can grow without bloating the player object — add a case
/// here, not there.
/// </summary>
public sealed class BanchoBotCommands
{
    private readonly TomYumVersion _version;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly MatchRegistry _matches;
    private readonly BeatmapService _beatmaps;
    private readonly IServiceScopeFactory _scopeFactory;

    public BanchoBotCommands(
        TomYumVersion version,
        PlayerRegistry players,
        ChannelRegistry channels,
        MatchRegistry matches,
        BeatmapService beatmaps,
        IServiceScopeFactory scopeFactory)
    {
        _version = version;
        _players = players;
        _channels = channels;
        _matches = matches;
        _beatmaps = beatmaps;
        _scopeFactory = scopeFactory;
    }

    public async Task<string?> TryHandleAsync(
        OnlinePlayer caller,
        string message,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '!')
            return null;

        var parts = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var command = parts[0].ToLowerInvariant();
        return command switch
        {
            "help"    => Help(privileges),
            "ping"    => "pong!",
            "version" => string.Create(CultureInfo.InvariantCulture, $"TomYum {_version.Display}"),
            "roll"    => Roll(caller.Presence.Username, parts),
            "stats"   => IsStaff(privileges) ? Stats() : StaffOnly,
            "setpriv" => IsStaff(privileges)
                ? await SetPrivAsync(parts, cancellationToken).ConfigureAwait(false)
                : StaffOnly,
            "mp" => await MpAsync(caller, parts, privileges, cancellationToken).ConfigureAwait(false),
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown command \"{command}\". Try !help."),
        };
    }

    private const string StaffOnly = "That command is staff-only.";

    private static bool IsStaff(BanchoPrivileges privileges) =>
        privileges.HasFlag(BanchoPrivileges.Owner) || privileges.HasFlag(BanchoPrivileges.Developer);

    private static string Help(BanchoPrivileges privileges) => IsStaff(privileges)
        ? "Commands: !help, !ping, !roll [max], !version, !mp <start|abort|countdown|host|kick>, !stats (staff), !setpriv <user> <role> (staff)."
        : "Commands: !help, !ping, !roll [max], !version, !mp <start|abort|countdown|host|kick>.";

    private static string Roll(string sender, string[] parts)
    {
        var max = 100;
        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) && requested > 0)
            max = requested;

        var value = Random.Shared.Next(1, max + 1);
        return string.Create(CultureInfo.InvariantCulture, $"{sender} rolls {value} point(s)! (1-{max})");
    }

    private string Stats()
    {
        var players = _players.All;
        long queuedBytes = 0;
        foreach (var player in players)
            queuedBytes += player.QueuedBytes;

        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Players: {players.Count} | Queued: {queuedBytes / 1024.0:F1} KB | Channels: {_channels.All.Count} | Beatmaps cached: {_beatmaps.CachedCount} | Heap: {heapMb:F1} MB");
    }

    // ---- !mp subcommands ----

    private async Task<string?> MpAsync(
        OnlinePlayer caller,
        string[] parts,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
            return "Usage: !mp <start [seconds]|abort|countdown <seconds>|host <player>|kick <player>>";

        var sub = parts[1].ToLowerInvariant();

        var match = _matches.GetByUserId(caller.UserId);
        if (match is null)
            return "You are not in a multiplayer room.";

        // Only the host can control the match.
        if (sub is "start" or "abort" or "countdown" && match.HostUserId != caller.UserId && !IsStaff(privileges))
            return "Only the host can do that.";

        return sub switch
        {
            "start"     => await MpStartAsync(match, parts).ConfigureAwait(false),
            "abort"     => MpAbort(match),
            "countdown" => await MpCountdownAsync(match, parts).ConfigureAwait(false),
            "host"      => MpHost(match, parts, caller, privileges),
            "kick"      => MpKick(match, parts, privileges),
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown !mp subcommand \"{sub}\"."),
        };
    }

    private async Task<string?> MpStartAsync(Match match, string[] parts)
    {
        if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) && secs > 0)
            return await MpCountdownCoreAsync(match, secs, autoStart: true).ConfigureAwait(false);

        StartMatch(match);
        PostToMatch(match, "Good luck, have fun!");
        return null;
    }

    private static string? MpAbort(Match match)
    {
        var cts = match.CountdownCts;
        if (cts is not null)
        {
            cts.Cancel();
            match.CountdownCts = null;
            PostToMatch(match, "Countdown aborted.");
            return null;
        }

        if (match.InProgress)
        {
            lock (match.SyncRoot)
            {
                match.InProgress = false;
                foreach (var s in match.Slots)
                {
                    if (s.Status == SlotStatus.Playing)
                        s.Status = SlotStatus.NotReady;
                }
            }
            PostToMatch(match, "Match aborted.");
            return null;
        }

        return "Nothing to abort.";
    }

    private async Task<string?> MpCountdownAsync(Match match, string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) || secs <= 0)
            return "Usage: !mp countdown <seconds>";

        return await MpCountdownCoreAsync(match, secs, autoStart: false).ConfigureAwait(false);
    }

    private async Task<string?> MpCountdownCoreAsync(Match match, int seconds, bool autoStart)
    {
        // Cancel any existing countdown first.
        match.CountdownCts?.Cancel();

        var cts = new CancellationTokenSource();
        match.CountdownCts = cts;

        var action = autoStart ? "starts" : "countdown ends";
        PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match {action} in {seconds} second{(seconds == 1 ? "" : "s")}!"));

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCountdownAsync(match, seconds, autoStart, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (match.CountdownCts == cts)
                    match.CountdownCts = null;
                cts.Dispose();
            }
        });

        return null;
    }

    private async Task RunCountdownAsync(Match match, int seconds, bool autoStart, CancellationToken token)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);

        // Early-warning milestones for long countdowns.
        foreach (var milestone in new[] { 60, 30 })
        {
            if (seconds <= milestone) continue;
            var delay = end - DateTime.UtcNow - TimeSpan.FromSeconds(milestone);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);
            PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match starts in {milestone} seconds!"));
        }

        // Per-second countdown for the last 10 seconds (or fewer if the total is short).
        var countFrom = Math.Min(seconds - 1, 10);
        for (var i = countFrom; i >= 1; i--)
        {
            var delay = end - DateTime.UtcNow - TimeSpan.FromSeconds(i);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);
            PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match starts in {i} second{(i == 1 ? "" : "s")}!"));
        }

        var finalDelay = end - DateTime.UtcNow;
        if (finalDelay > TimeSpan.Zero)
            await Task.Delay(finalDelay, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (autoStart)
        {
            StartMatch(match);
            PostToMatch(match, "Good luck, have fun!");
        }
        else
        {
            PostToMatch(match, "Countdown ended. Type !mp start to begin.");
        }
    }

    private void StartMatch(Match match)
    {
        lock (match.SyncRoot)
        {
            if (match.InProgress) return;
            match.InProgress = true;
            match.LoadedPlayers.Clear();
            match.SkipRequests.Clear();
            foreach (var s in match.Slots)
            {
                if (s.Status is SlotStatus.Ready or SlotStatus.NotReady)
                    s.Status = SlotStatus.Playing;
            }
        }

        var startBytes = MatchBroadcast.EncodeMatch(ServerPacketId.MatchStart, match, sendPassword: true);
        MatchBroadcast.ToMatch(match, _players, startBytes);

        var lobbyUpdate = MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false);
        MatchBroadcast.ToLobby(_players, lobbyUpdate);
    }

    private string MpHost(Match match, string[] parts, OnlinePlayer caller, BanchoPrivileges privileges)
    {
        if (!IsStaff(privileges) && match.HostUserId != caller.UserId)
            return "Only the host can transfer host.";
        if (parts.Length < 3)
            return "Usage: !mp host <username>";

        var targetName = parts[2];
        var target = _players.GetByUsername(targetName);
        if (target is null)
            return string.Create(CultureInfo.InvariantCulture, $"\"{targetName}\" is not online.");

        lock (match.SyncRoot)
        {
            if (match.IndexOf(target.UserId) < 0)
                return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is not in this room.");
            match.HostUserId = target.UserId;
        }

        MatchBroadcast.UpdateMatch(match, _players);
        return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is now the host.");
    }

    private string MpKick(Match match, string[] parts, BanchoPrivileges privileges)
    {
        if (parts.Length < 3)
            return "Usage: !mp kick <username>";

        var targetName = parts[2];
        var target = _players.GetByUsername(targetName);
        if (target is null)
            return string.Create(CultureInfo.InvariantCulture, $"\"{targetName}\" is not online.");

        lock (match.SyncRoot)
        {
            var slotIdx = match.IndexOf(target.UserId);
            if (slotIdx < 0)
                return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is not in this room.");
            match.Slots[slotIdx].Reset();
        }

        match.Channel.Leave(target.UserId);
        target.LeaveChannel("#multiplayer");

        // Tell the kicked player they've been removed.
        target.Enqueue(new BanchoPacketWriter(16).WritePacket(ServerPacketId.MatchJoinFail, null).ToArray());

        MatchBroadcast.UpdateMatch(match, _players);
        return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} was kicked from the room.");
    }

    /// <summary>Posts a bot message directly into the match's per-room channel.</summary>
    private static void PostToMatch(Match match, string text)
    {
        var w = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(w, new BanchoMessage(BanchoBot.Name, text, "#multiplayer", BanchoBot.UserId));
        var bytes = w.ToArray();
        foreach (var member in match.Channel.Members)
            member.Enqueue(bytes);
    }

    // ---- !setpriv ----

    private async Task<string> SetPrivAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 3)
            return "Usage: !setpriv <username> <owner|admin|dev|mod|supporter|normal>";

        if (!TryParseRole(parts[2], out var privileges))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Unknown role \"{parts[2]}\". Valid: owner, admin, dev, mod, supporter, normal.");
        }

        var targetName = parts[1];
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var user = await users.GetBySafeNameAsync(User.MakeSafeName(targetName), cancellationToken).ConfigureAwait(false);
        if (user is null)
            return string.Create(CultureInfo.InvariantCulture, $"No such user \"{targetName}\".");

        await users.UpdateAsync(WithPrivileges(user, privileges), cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture,
            $"{user.Username} is now {privileges}. (Takes effect on their next login.)");
    }

    private static bool TryParseRole(string role, out UserPrivileges privileges)
    {
        privileges = role.ToLowerInvariant() switch
        {
            "owner" => UserPrivileges.Owner,
            "admin" or "administrator" => UserPrivileges.Normal | UserPrivileges.Supporter | UserPrivileges.Moderator | UserPrivileges.Administrator,
            "dev" or "developer" => UserPrivileges.Normal | UserPrivileges.Supporter | UserPrivileges.Moderator | UserPrivileges.Administrator | UserPrivileges.Developer,
            "mod" or "moderator" => UserPrivileges.Normal | UserPrivileges.Moderator,
            "supporter" => UserPrivileges.Normal | UserPrivileges.Supporter,
            "normal" or "player" => UserPrivileges.Normal,
            _ => UserPrivileges.None,
        };
        return privileges != UserPrivileges.None;
    }

    private static User WithPrivileges(User user, UserPrivileges privileges) => new()
    {
        Id             = user.Id,
        Username       = user.Username,
        SafeName       = user.SafeName,
        Email          = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country        = user.Country,
        CreationTime   = user.CreationTime,
        LatestActivity = user.LatestActivity,
        ApiKey         = user.ApiKey,
        Privileges     = privileges,
    };
}
