using TomYum.Bancho;
using TomYum.Server.Bancho.Packets.Match;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// LOGOUT (id 2): the client is disconnecting. Cleans up match membership (if any), removes
/// the session, and broadcasts USER_LOGOUT (id 12) to everyone else.
/// </summary>
public sealed class LogoutHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.Logout;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Clean up match membership before removing from registry (RemovePlayer needs the registry).
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is not null)
            MatchBroadcast.RemovePlayer(match, context.Player, context.Registry, context.Matches);

        context.Registry.Remove(context.Player.Token);
        context.Channels.RemoveFromAll(context.Player.UserId);

        var writer = new BanchoPacketWriter(16);
        writer.WriteInt32Packet(ServerPacketId.UserLogout, context.Player.UserId);
        context.Registry.EnqueueToAll(writer.ToArray(), exceptUserId: context.Player.UserId);
        return Task.CompletedTask;
    }
}
