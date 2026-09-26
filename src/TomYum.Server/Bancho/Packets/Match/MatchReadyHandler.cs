using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_READY (39): player marks themselves ready. No payload.</summary>
public sealed class MatchReadyHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchReady;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0) match.Slots[idx].Status = SlotStatus.Ready;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
