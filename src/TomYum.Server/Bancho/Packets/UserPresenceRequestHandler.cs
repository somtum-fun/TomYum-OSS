using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// USER_PRESENCE_REQUEST (id 97): the client sends an i32 list of user ids it wants static
/// USER_PRESENCE (id 83) for. We reply with one USER_PRESENCE per requested id that is online.
/// Unknown/offline ids are skipped.
/// </summary>
public sealed class UserPresenceRequestHandler : IClientPacketHandler
{
    /// <summary>The most players one request is answered for.</summary>
    internal const int MaxIds = 4096;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.UserPresenceRequest;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        // Each player once, and no more than a client's user list could ask for.
        var ids = reader.ReadInt32List().Distinct().Take(MaxIds);

        foreach (var id in ids)
        {
            var target = context.Registry.GetByUserId(id);
            if (target is not null && PlayerRegistry.IsVisibleTo(target, context.Player.UserId))
            {
                LoginResponseBuilder.WritePresence(context.Response, target.Presence);
            }
        }

        return Task.CompletedTask;
    }
}
