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

        // Re-broadcast the raw score frame as packet 48, with frame[4] (the 'id' field)
        // overwritten to carry the sender's slot index. This matches bancho.py cho.py:1714-1717
        // (buf[11] = slot_id, where buf starts with the 7-byte header) and BanchoNET
        // MatchScoreUpdate.cs:26 (bytes[11] = slotId). The packet payload is exactly the
        // 29-byte score frame — no length prefix or extra field is prepended.
        var payload = context.Payload.Span;
        if (payload.Length < 5) return Task.CompletedTask;

        // An old client's frame (docs/old-clients.md) gets the ScoreV2 flag today's clients read.
        var frame = LegacyFrames.ScoreFrame(payload, context.Player.Build);
        frame[4] = (byte)slotIdx;

        var w = new BanchoPacketWriter(40);
        w.WritePacket(ServerPacketId.MatchScoreUpdate, pw => pw.WriteRaw(frame));
        MatchBroadcast.ToMatch(match, context.Registry, w.ToArray());
        return Task.CompletedTask;
    }
}
