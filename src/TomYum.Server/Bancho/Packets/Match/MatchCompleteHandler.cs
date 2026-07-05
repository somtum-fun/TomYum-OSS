using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_COMPLETE (49): client finished the map. Mark the slot Complete; when every Playing
/// slot is done, broadcast MATCH_COMPLETE (58) and reset the match to idle.
/// </summary>
public sealed class MatchCompleteHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchComplete;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        bool allDone;
        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0) match.Slots[idx].Status = SlotStatus.Complete;
            allDone = match.AllComplete();
        }

        if (allDone)
        {
            lock (match.SyncRoot)
            {
                match.InProgress = false;
                foreach (var s in match.Slots)
                    if (s.Status == SlotStatus.Complete)
                        s.Status = SlotStatus.NotReady;
            }

            var completeBytes = new BanchoPacketWriter(16)
                .WritePacket(ServerPacketId.MatchComplete, null)
                .ToArray();
            MatchBroadcast.ToMatch(match, context.Registry, completeBytes);

            var lobbyUpdate = MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false);
            MatchBroadcast.ToLobby(context.Registry, lobbyUpdate);
        }

        return Task.CompletedTask;
    }
}
