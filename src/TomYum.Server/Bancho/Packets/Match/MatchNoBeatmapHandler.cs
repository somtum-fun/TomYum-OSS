using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_NO_BEATMAP (54): client does not have the current map. No payload.</summary>
public sealed class MatchNoBeatmapHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchNoBeatmap;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0) match.Slots[idx].Status = SlotStatus.NoMap;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
