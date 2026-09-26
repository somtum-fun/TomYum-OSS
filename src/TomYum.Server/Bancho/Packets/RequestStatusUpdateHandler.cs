using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// REQUEST_STATUS_UPDATE (id 3): the client asks for a refresh of its own stats (e.g. after
/// returning to the menu). We reply with the originator's current USER_STATS (id 11) so their
/// own user panel re-syncs. No payload.
/// </summary>
public sealed class RequestStatusUpdateHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.RequestStatusUpdate;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        LoginResponseBuilder.WriteStats(context.Response, context.Player.Presence);
        return Task.CompletedTask;
    }
}
