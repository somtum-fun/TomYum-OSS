using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// START_SPECTATING (16): payload is an i32 host user id. Attaches the sender as a spectator of that
/// host. Re-sending START for the current host (the "map finished downloading" case) just re-announces.
/// </summary>
public sealed class StartSpectatingHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.StartSpectating;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Payload.Length < sizeof(int))
            return Task.CompletedTask;

        var targetId = new BanchoPacketReader(context.Payload.Span).ReadInt32();
        var spectator = context.Player;

        var host = context.Registry.GetByUserId(targetId);
        if (host is null || host.UserId == spectator.UserId)
            return Task.CompletedTask; // unknown host, or trying to spectate self

        if (spectator.SpectatingUserId == targetId)
        {
            SpectatorManager.Reannounce(host, spectator, context.Registry);
            return Task.CompletedTask;
        }

        if (spectator.SpectatingUserId is int previousHostId)
        {
            var previousHost = context.Registry.GetByUserId(previousHostId);
            if (previousHost is not null)
                SpectatorManager.Detach(previousHost, spectator, context.Registry);
            else
                spectator.SpectatingUserId = null;
        }

        SpectatorManager.Attach(host, spectator, context.Registry);
        return Task.CompletedTask;
    }
}
