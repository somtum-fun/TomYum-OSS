using TomYum.Bancho;
using TomYum.Server.Bancho.Packets.Match;
using TomYum.Server.Bancho.Packets.Spectate;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// LOGOUT (id 2): the client is disconnecting. Cleans up match membership (if any), removes
/// the session, and broadcasts USER_LOGOUT (id 12) to everyone else.
/// </summary>
public sealed class LogoutHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.Logout;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A tournament client window closing: only what it did itself (the player is still online).
        if (context.Player.IsTournamentClient)
        {
            TournamentWatching.Stop(context.Player, context.Matches);
            SpectatorManager.TeardownForLeaver(context.Player, context.Registry);
            context.Registry.Remove(context.Player.Token);
            return;
        }

        // Private messages that reached the player in-game count as read on the website too.
        if (context.Services.GetService<TomYum.Persistence.OsuWeb.OsuWebPrivateMessages>() is { } messages)
        {
            try
            {
                await messages.MarkAllPrivateReadAsync(context.Player.UserId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Services.GetService<ILogger<LogoutHandler>>()?.LogWarning(ex, "Couldn't mark {UserId}'s messages read.", context.Player.UserId);
            }
        }

        // Clean up match membership before removing from registry (RemovePlayer needs the registry).
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is not null)
            MatchBroadcast.RemovePlayer(match, context.Player, context.Registry, context.Matches);

        // Tear down spectate links (stop watching + drain own spectators) while everyone is still online.
        SpectatorManager.TeardownForLeaver(context.Player, context.Registry);

        context.Registry.Remove(context.Player.Token);
        context.Channels.RemoveFromAll(context.Player.UserId);

        var writer = new BanchoPacketWriter(16);
        writer.WriteInt32Packet(ServerPacketId.UserLogout, context.Player.UserId);
        context.Registry.EnqueueToAll(writer.ToArray(), exceptUserId: context.Player.UserId);
    }
}
