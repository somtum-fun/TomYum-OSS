using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_CHANGE_SLOT (38): player moves to a different open slot. Payload: i32 slot.</summary>
public sealed class MatchChangeSlotHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchChangeSlot;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        var reader  = new BanchoPacketReader(context.Payload.Span);
        var target  = reader.ReadInt32();
        if (target is < 0 or >= 16) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var src = match.IndexOf(context.Player.UserId);
            if (src < 0 || match.Slots[target].Status != SlotStatus.Open) return Task.CompletedTask;

            match.Slots[target].Status = match.Slots[src].Status;
            match.Slots[target].UserId = context.Player.UserId;
            match.Slots[target].Team   = match.Slots[src].Team;
            match.Slots[target].Mods   = match.Slots[src].Mods;
            match.Slots[src].Reset();
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
