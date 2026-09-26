using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_TRANSFER_HOST (70): host transfers host role to another slot. Payload: i32 slot.
/// Sends MATCH_TRANSFER_HOST (50) to the new host and broadcasts UPDATE_MATCH.
/// </summary>
public sealed class MatchTransferHostHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchTransferHost;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null || match.HostUserId != context.Player.UserId) return Task.CompletedTask;

        var reader  = new BanchoPacketReader(context.Payload.Span);
        var slotIdx = reader.ReadInt32();
        if (slotIdx is < 0 or >= 16) return Task.CompletedTask;

        int newHostId;
        lock (match.SyncRoot)
        {
            var slot = match.Slots[slotIdx];
            if (!slot.HasPlayer) return Task.CompletedTask;
            newHostId        = slot.UserId;
            match.HostUserId = newHostId;
        }

        var newHost = context.Registry.GetByUserId(newHostId);
        newHost?.Enqueue(new BanchoPacketWriter(16)
            .WritePacket(ServerPacketId.MatchTransferHost, null)
            .ToArray());

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
