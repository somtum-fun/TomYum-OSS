using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_HAS_BEATMAP (59): client got the map. Return slot to NotReady. No payload.</summary>
public sealed class MatchHasBeatmapHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchHasBeatmap;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0 && match.Slots[idx].Status == SlotStatus.NoMap)
                match.Slots[idx].Status = SlotStatus.NotReady;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
