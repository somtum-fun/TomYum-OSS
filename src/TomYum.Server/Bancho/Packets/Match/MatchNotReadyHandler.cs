using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_NOT_READY (55): player un-readies themselves. No payload.</summary>
public sealed class MatchNotReadyHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchNotReady;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0 && match.Slots[idx].Status == SlotStatus.Ready)
                match.Slots[idx].Status = SlotStatus.NotReady;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
