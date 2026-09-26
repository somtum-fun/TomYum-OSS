using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// JOIN_LOBBY (30): the client opened the multiplayer lobby tab. Mark the player as a
/// lobby viewer and send them a NEW_MATCH (27) for every active match so the list appears.
/// </summary>
public sealed class JoinLobbyHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.JoinLobby;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Player.InLobby = true;

        foreach (var match in context.Matches.All)
        {
            var bytes = MatchBroadcast.EncodeMatch(ServerPacketId.NewMatch, match, sendPassword: false);
            context.Response.WriteRaw(bytes);
        }

        return Task.CompletedTask;
    }
}
