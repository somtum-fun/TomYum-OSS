using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// JOIN_MATCH (32): player wants to enter a room. Payload: i32 match_id + string password.
/// Validates id and password, finds an open slot, then confirms with MATCH_JOIN_SUCCESS (36)
/// and pushes UPDATE_MATCH (26) to all existing members and lobby viewers.
/// </summary>
public sealed class JoinMatchHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.JoinMatch;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Player.IsRestricted)
        {
            ModerationPackets.WriteNoMultiplayer(context.Response);
            return Task.CompletedTask;
        }

        var reader   = new BanchoPacketReader(context.Payload.Span);
        var matchId  = (ushort)reader.ReadInt32();
        var password = reader.ReadString();

        var match = context.Matches.Get(matchId);
        if (match is null)
        {
            context.Response.WritePacket(ServerPacketId.MatchJoinFail, null);
            return Task.CompletedTask;
        }

        lock (match.SyncRoot)
        {
            if (!string.IsNullOrEmpty(match.Password) && match.Password != password)
            {
                context.Response.WritePacket(ServerPacketId.MatchJoinFail, null);
                return Task.CompletedTask;
            }

            var slot = match.FirstOpenSlot(context.Player.Build.MatchSlots);
            if (slot < 0)
            {
                context.Response.WritePacket(ServerPacketId.MatchJoinFail, null);
                return Task.CompletedTask;
            }

            match.Slots[slot].Join(context.Player.UserId);
        }

        match.Channel.Join(context.Player);
        context.Player.JoinChannel("#multiplayer");

        MatchEncoder.WriteMatchPacket(context.Response, ServerPacketId.MatchJoinSuccess, match, sendPassword: true);
        context.Response.WriteStringPacket(ServerPacketId.ChannelJoinSuccess, "#multiplayer");

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
