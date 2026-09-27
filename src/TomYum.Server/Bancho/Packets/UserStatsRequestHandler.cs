using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// USER_STATS_REQUEST (id 85): the client sends an i32 list of user ids it wants fresh
/// USER_STATS (id 11) for — typically the players currently visible in its user list. We
/// reply with one USER_STATS per requested id that is online. Unknown/offline ids are skipped.
/// </summary>
public sealed class UserStatsRequestHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.UserStatsRequest;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var ids = reader.ReadInt32List();

        foreach (var id in ids)
        {
            var target = context.Registry.GetByUserId(id);
            if (target is not null && PlayerRegistry.IsVisibleTo(target, context.Player.UserId))
            {
                LoginResponseBuilder.WriteStats(context.Response, target.Presence);
            }
        }

        return Task.CompletedTask;
    }
}
