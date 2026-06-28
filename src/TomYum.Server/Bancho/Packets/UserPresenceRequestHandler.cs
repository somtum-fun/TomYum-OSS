using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// USER_PRESENCE_REQUEST (id 97): the client sends an i32 list of user ids it wants static
/// USER_PRESENCE (id 83) for. We reply with one USER_PRESENCE per requested id that is online.
/// Unknown/offline ids are skipped.
/// </summary>
public sealed class UserPresenceRequestHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.UserPresenceRequest;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var ids = reader.ReadInt32List();

        foreach (var id in ids)
        {
            var target = context.Registry.GetByUserId(id);
            if (target is not null)
            {
                LoginResponseBuilder.WritePresence(context.Response, target.Presence);
            }
        }

        return Task.CompletedTask;
    }
}
