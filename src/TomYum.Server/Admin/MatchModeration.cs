using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets.Match;

namespace TomYum.Server.Admin;

/// <summary>A live multiplayer room for the website's Tournament Committee panel.</summary>
public sealed record MatchEntry(
    int Id,
    string Name,
    bool HasPassword,
    int HostUserId,
    string HostUsername,
    string MapName,
    int MapId,
    int Mode,
    bool InProgress,
    string TeamType,
    int TournamentWatchers,
    IReadOnlyList<MatchPlayerEntry> Players);

/// <summary>A player in a <see cref="MatchEntry"/>: their slot's state and team (0 none, 1 blue, 2 red).</summary>
public sealed record MatchPlayerEntry(int Slot, int UserId, string Username, string Status, int Team);

/// <summary>Body of <c>POST /_admin/matches/{id}/message</c>.</summary>
public sealed record MatchMessageRequest(string? Text);

/// <summary>
/// Watches and steers live multiplayer rooms from the website (the Tournament Committee panel):
/// lists them, aborts the map being played, closes a room (everyone back to the lobby) and posts in
/// its chat as BanchoBot. The packets are the ones the game's own room handling sends.
/// </summary>
public sealed class MatchModeration
{
    private readonly MatchRegistry _matches;
    private readonly PlayerRegistry _players;

    public MatchModeration(MatchRegistry matches, PlayerRegistry players)
    {
        _matches = matches;
        _players = players;
    }

    /// <summary>Every live room, those being played first, then by id.</summary>
    public IReadOnlyList<MatchEntry> List() =>
        _matches.All
            .Select(Describe)
            .OrderByDescending(m => m.InProgress)
            .ThenBy(m => m.Id)
            .ToList();

    /// <summary>One room, or <c>null</c>.</summary>
    public MatchEntry? Get(ushort id) => _matches.Get(id) is { } match ? Describe(match) : null;

    /// <summary>
    /// Stops a running countdown, or the map being played (everyone back to the room, not ready).
    /// <c>null</c> when there's no such room, <c>false</c> when there was nothing to abort.
    /// </summary>
    public bool? Abort(ushort id)
    {
        if (_matches.Get(id) is not { } match)
            return null;

        return Abort(match, _players);
    }

    /// <summary>Aborts <paramref name="match"/> (see <see cref="Abort(ushort)"/>); also what <c>!mp abort</c> does.</summary>
    internal static bool Abort(Match match, PlayerRegistry players)
    {
        if (match.CountdownCts is { } countdown)
        {
            countdown.Cancel();
            match.CountdownCts = null;
            Post(match, "Countdown aborted.");
            return true;
        }

        lock (match.SyncRoot)
        {
            if (!match.InProgress)
                return false;

            match.InProgress = false;
            match.LoadedPlayers.Clear();
            match.SkipRequests.Clear();
            foreach (var slot in match.Slots)
            {
                if (slot.Status is SlotStatus.Playing or SlotStatus.Complete)
                    slot.Status = SlotStatus.NotReady;
            }
        }

        match.History?.GameAborted(match);
        MatchBroadcast.ToMatch(match, players, new BanchoPacketWriter(16).WritePacket(ServerPacketId.MatchAbort, null).ToArray());
        MatchBroadcast.UpdateMatch(match, players);
        Post(match, "Match aborted.");
        return true;
    }

    /// <summary>Sends everyone in the room back to the lobby and disbands it. <c>false</c> when there's no such room.</summary>
    public bool Close(ushort id)
    {
        if (_matches.Get(id) is not { } match)
            return false;

        Post(match, "This room was closed by the Tournament Committee.");
        match.CountdownCts?.Cancel();
        match.CountdownCts = null;

        int[] playerIds;
        OnlinePlayer[] watchers;
        lock (match.SyncRoot)
        {
            playerIds = [.. match.PlayerIds()];
            watchers = [.. match.TournamentWatchers];
            match.TournamentWatchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            match.Channel.Leave(watcher.UserId);
            watcher.LeaveChannel("#multiplayer");
        }

        var disbanded = false;
        foreach (var userId in playerIds)
        {
            if (_players.GetByUserId(userId) is { } player)
            {
                disbanded = MatchBroadcast.RemovePlayer(match, player, _players, _matches);
            }
            else
            {
                // Gone without leaving: free the slot.
                lock (match.SyncRoot)
                {
                    var slot = match.IndexOf(userId);
                    if (slot >= 0)
                        match.Slots[slot].Reset();
                }
            }
        }

        // Nobody (online) left to disband it on the way out.
        if (!disbanded)
        {
            _matches.Remove(match.Id);
            match.History?.Disbanded(match);
            MatchBroadcast.ToLobby(_players, new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.DisposeMatch, match.Id).ToArray());
        }

        return true;
    }

    /// <summary>Posts <paramref name="text"/> in the room's chat as BanchoBot. <c>false</c> when there's no such room.</summary>
    public bool Message(ushort id, string text)
    {
        if (_matches.Get(id) is not { } match)
            return false;

        Post(match, text);
        return true;
    }

    private static void Post(Match match, string text)
    {
        var writer = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, text, "#multiplayer", BanchoBot.UserId));
        var bytes = writer.ToArray();
        foreach (var member in match.Channel.Members)
            member.Enqueue(bytes);
    }

    private MatchEntry Describe(Match match)
    {
        lock (match.SyncRoot)
        {
            var players = new List<MatchPlayerEntry>();
            for (var i = 0; i < match.Slots.Length; i++)
            {
                var slot = match.Slots[i];
                if (!slot.HasPlayer)
                    continue;

                players.Add(new MatchPlayerEntry(i, slot.UserId, UsernameOf(slot.UserId), StatusName(slot.Status), slot.Team));
            }

            return new MatchEntry(
                match.Id,
                match.Name,
                !string.IsNullOrEmpty(match.Password),
                match.HostUserId,
                UsernameOf(match.HostUserId),
                match.MapName,
                match.MapId,
                match.Mode,
                match.InProgress,
                match.TeamType.ToString(),
                match.TournamentWatchers.Count,
                players);
        }
    }

    private string UsernameOf(int userId) => _players.GetByUserId(userId)?.Presence.Username ?? $"#{userId}";

    private static string StatusName(SlotStatus status) => status switch
    {
        SlotStatus.NotReady => "not ready",
        SlotStatus.Ready => "ready",
        SlotStatus.NoMap => "no map",
        SlotStatus.Playing => "playing",
        SlotStatus.Complete => "finished",
        _ => status.ToString().ToLowerInvariant(),
    };
}
