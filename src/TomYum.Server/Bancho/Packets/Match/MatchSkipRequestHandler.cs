using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_SKIP_REQUEST (60): player wants to skip the intro. Once every Playing player
/// requests skip, broadcast MATCH_SKIP (61) and MATCH_PLAYER_SKIPPED (81) per skipped slot.
/// </summary>
public sealed class MatchSkipRequestHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchSkipRequest;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        bool allSkipped;
        int slotIdx;
        int playingCount;
        lock (match.SyncRoot)
        {
            slotIdx = match.IndexOf(context.Player.UserId);
            match.SkipRequests.Add(context.Player.UserId);
            playingCount = match.PlayingCount();
            allSkipped   = match.SkipRequests.Count >= playingCount;
            if (allSkipped) match.SkipRequests.Clear();
        }

        // Notify all players that this slot has requested skip.
        if (slotIdx >= 0)
        {
            var skippedBytes = new BanchoPacketWriter(16)
                .WriteInt32Packet(ServerPacketId.MatchPlayerSkipped, slotIdx)
                .ToArray();
            MatchBroadcast.ToMatch(match, context.Registry, skippedBytes);
        }

        if (allSkipped)
        {
            var skipBytes = new BanchoPacketWriter(16)
                .WritePacket(ServerPacketId.MatchSkip, null)
                .ToArray();
            MatchBroadcast.ToMatch(match, context.Registry, skipBytes);
        }

        return Task.CompletedTask;
    }
}
