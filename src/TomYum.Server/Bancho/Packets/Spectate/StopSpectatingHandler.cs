using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// STOP_SPECTATING (17): no payload. Detaches the sender from whichever host it is spectating.
/// </summary>
public sealed class StopSpectatingHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.StopSpectating;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spectator = context.Player;

        if (spectator.SpectatingUserId is not int hostId)
            return Task.CompletedTask;

        var host = context.Registry.GetByUserId(hostId);
        if (host is not null)
            SpectatorManager.Detach(host, spectator, context.Registry);
        else
            spectator.SpectatingUserId = null; // host already gone

        return Task.CompletedTask;
    }
}
