using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_FAILED (56): client failed the map. Broadcast MATCH_PLAYER_FAILED (57) with the
/// slot index so every other player sees the fail animation.
/// </summary>
public sealed class MatchFailedHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchFailed;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        int slotIdx;
        bool allDone;
        lock (match.SyncRoot)
        {
            slotIdx = match.IndexOf(context.Player.UserId);
            if (slotIdx < 0) return Task.CompletedTask;

            // A failed player never sends MatchComplete (49). Move them to Complete so
            // AllComplete() can fire once every remaining player has finished or failed.
            match.Slots[slotIdx].Status = SlotStatus.Complete;
            allDone = match.InProgress && match.AllComplete();
        }

        var bytes = new BanchoPacketWriter(16)
            .WriteInt32Packet(ServerPacketId.MatchPlayerFailed, slotIdx)
            .ToArray();
        MatchBroadcast.ToMatch(match, context.Registry, bytes);

        if (allDone)
            MatchBroadcast.CompleteMatch(match, context.Registry);

        return Task.CompletedTask;
    }
}
