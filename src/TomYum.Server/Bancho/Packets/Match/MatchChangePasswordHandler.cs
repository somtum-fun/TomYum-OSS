using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_CHANGE_PASSWORD (90): host changes the room password. Payload: string new_password.
/// Broadcasts MATCH_CHANGE_PASSWORD (91) to all players in the match (and UPDATE_MATCH to lobby).
/// </summary>
public sealed class MatchChangePasswordHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchChangePassword;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null || match.HostUserId != context.Player.UserId) return Task.CompletedTask;

        var reader = new BanchoPacketReader(context.Payload.Span);
        var newPw  = reader.ReadString();

        lock (match.SyncRoot) { match.Password = newPw; }

        var bytes = new BanchoPacketWriter(64)
            .WriteStringPacket(ServerPacketId.MatchChangePassword, newPw)
            .ToArray();
        MatchBroadcast.ToMatch(match, context.Registry, bytes);

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
