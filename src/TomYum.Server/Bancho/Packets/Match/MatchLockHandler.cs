using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_LOCK (40): host toggles a slot between Open and Locked. Payload: i32 slot.</summary>
public sealed class MatchLockHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchLock;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null || match.HostUserId != context.Player.UserId) return Task.CompletedTask;

        var reader = new BanchoPacketReader(context.Payload.Span);
        var slot   = reader.ReadInt32();
        if (slot is < 0 or >= 16) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            match.Slots[slot].Status = match.Slots[slot].Status == SlotStatus.Locked
                ? SlotStatus.Open
                : SlotStatus.Locked;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
