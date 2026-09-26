using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_START (44): host starts the match. Set InProgress, flip all Ready slots to Playing,
/// then send MATCH_START (46) to every player in the match and UPDATE_MATCH (26) to lobby viewers.
/// </summary>
public sealed class MatchStartHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchStart;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null || match.HostUserId != context.Player.UserId) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            match.InProgress = true;
            match.LoadedPlayers.Clear();
            match.SkipRequests.Clear();
            foreach (var s in match.Slots)
            {
                if (s.Status == SlotStatus.Ready)   s.Status = SlotStatus.Playing;
                if (s.Status == SlotStatus.NotReady) s.Status = SlotStatus.Playing;
            }
        }

        var startBytes = MatchBroadcast.EncodeMatch(ServerPacketId.MatchStart, match, sendPassword: true);
        MatchBroadcast.ToMatch(match, context.Registry, startBytes);

        // Lobby sees the match as in-progress.
        var lobbyUpdate = MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false);
        MatchBroadcast.ToLobby(context.Registry, lobbyUpdate);

        return Task.CompletedTask;
    }
}
