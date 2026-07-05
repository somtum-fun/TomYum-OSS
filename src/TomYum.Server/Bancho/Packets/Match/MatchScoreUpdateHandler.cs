using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_SCORE_UPDATE (47): a 29-byte score frame from a playing client. Re-broadcast as
/// MATCH_SCORE_UPDATE (48) = i32 slot_index + raw score frame to all match players.
/// </summary>
public sealed class MatchScoreUpdateHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchScoreUpdate;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        int slotIdx;
        lock (match.SyncRoot) { slotIdx = match.IndexOf(context.Player.UserId); }
        if (slotIdx < 0) return Task.CompletedTask;

        var w = new BanchoPacketWriter(48);
        w.WritePacket(ServerPacketId.MatchScoreUpdate, pw =>
        {
            pw.WriteInt32(slotIdx);
            pw.WriteRaw(context.Payload.Span);
        });
        MatchBroadcast.ToMatch(match, context.Registry, w.ToArray());
        return Task.CompletedTask;
    }
}
