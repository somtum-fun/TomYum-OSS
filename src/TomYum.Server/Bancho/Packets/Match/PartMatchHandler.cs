using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// PART_MATCH (33): player voluntarily leaves their current match. Delegates to
/// <see cref="MatchBroadcast.RemovePlayer"/> for all slot cleanup, channel teardown,
/// host transfer, and auto-completion logic.
/// </summary>
public sealed class PartMatchHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.PartMatch;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        MatchBroadcast.RemovePlayer(match, context.Player, context.Registry, context.Matches);
        return Task.CompletedTask;
    }
}
