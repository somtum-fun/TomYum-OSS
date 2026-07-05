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
        lock (match.SyncRoot) { slotIdx = match.IndexOf(context.Player.UserId); }
        if (slotIdx < 0) return Task.CompletedTask;

        var bytes = new BanchoPacketWriter(16)
            .WriteInt32Packet(ServerPacketId.MatchPlayerFailed, slotIdx)
            .ToArray();
        MatchBroadcast.ToMatch(match, context.Registry, bytes);
        return Task.CompletedTask;
    }
}
