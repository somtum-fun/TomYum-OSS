using TomYum.Bancho;
using TomYum.Server.Bancho.Packets;

namespace TomYum.Server.Bancho;

/// <summary>
/// Ends a game session from the server's side (a new login replacing it, a closed TCP connection, a client
/// that stopped polling) exactly as its own LOGOUT packet would: match, spectators, channels, everyone's
/// user list.
/// </summary>
public static class SessionEnd
{
    private static readonly byte[] LogoutPacket =
        new BanchoPacketWriter(16).WritePacket((ServerPacketId)ClientPacketId.Logout, w => w.WriteInt32(0)).ToArray();

    /// <summary>Logs <paramref name="player"/> out; does nothing when the session is already gone. Never throws.</summary>
    public static async Task LogOutAsync(OnlinePlayer player, IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        player.EndedByServer = true;
        try
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var registry = sp.GetRequiredService<PlayerRegistry>();
            if (registry.GetByToken(player.Token) is null)
                return;
            if (sp.GetService<BanchoPacketDispatcher>() is not { } dispatcher)
            {
                registry.Remove(player.Token);
                return;
            }
            await dispatcher.DispatchAsync(player, registry, sp.GetRequiredService<ChannelRegistry>(),
                sp.GetRequiredService<MatchRegistry>(), sp, LogoutPacket).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the session is over either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Couldn't log out {UserId}'s session.", player.UserId);
        }
    }
}
