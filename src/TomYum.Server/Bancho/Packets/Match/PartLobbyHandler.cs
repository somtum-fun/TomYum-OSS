using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>PART_LOBBY (29): player closed the lobby tab. Clear the lobby flag.</summary>
public sealed class PartLobbyHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.PartLobby;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Player.InLobby = false;
        return Task.CompletedTask;
    }
}
