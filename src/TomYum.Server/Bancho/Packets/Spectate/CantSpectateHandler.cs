using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// CANT_SPECTATE (21): the sender (a spectator) doesn't have the host's beatmap. Relay
/// SPECTATOR_CANT_SPECTATE (22) naming the sender to the host and to every spectator of that host
/// (including the sender — the client uses it to render its own "downloading…" state). No state change;
/// after downloading, the client re-sends START_SPECTATING to resume.
/// </summary>
public sealed class CantSpectateHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.CantSpectate;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spectator = context.Player;

        if (spectator.SpectatingUserId is not int hostId)
            return Task.CompletedTask;

        var host = context.Registry.GetByUserId(hostId);
        if (host is null)
        {
            spectator.SpectatingUserId = null;
            return Task.CompletedTask;
        }

        var packet = SpectatorManager.SpectatorCantSpectate(spectator.UserId);
        host.Enqueue(packet);
        foreach (var otherId in host.Spectators) // includes the sender, by design
            foreach (var session in context.Registry.SpectatingSessions(otherId, host.UserId))
                session.Enqueue(packet);

        return Task.CompletedTask;
    }
}
