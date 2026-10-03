using TomYum.Bancho;
using TomYum.Server.Bancho.Packets;

namespace TomYum.Server.Bancho;

/// <summary>
/// One round of a bancho session, the same over HTTP (each poll) and TCP (each batch of packets, or
/// queued packets on their own): what the client sent is dispatched, and what it gets back is its
/// queue plus whatever the handlers answered. Old builds (docs/old-clients.md) get both directions in
/// their own layout (<see cref="LegacyPackets"/>) and their packets named in the log once.
/// </summary>
public static class BanchoExchange
{
    /// <summary>Dispatches <paramref name="body"/> (framed client packets; may be empty) and returns what to send back.</summary>
    public static async Task<byte[]> RunAsync(
        OnlinePlayer player,
        byte[] body,
        BanchoPacketDispatcher dispatcher,
        PlayerRegistry registry,
        ChannelRegistry channels,
        MatchRegistry matches,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);

        body = LegacyPackets.Incoming(body, player.Build);
        var produced = body.Length == 0
            ? []
            : await dispatcher.DispatchAsync(player, registry, channels, matches, services, body).ConfigureAwait(false);

        var queued = player.DequeueAll();
        byte[] response;
        if (produced.Length == 0)
        {
            response = queued;
        }
        else if (queued.Length == 0)
        {
            response = produced;
        }
        else
        {
            response = new byte[queued.Length + produced.Length];
            queued.CopyTo(response, 0);
            produced.CopyTo(response, queued.Length);
        }

        response = LegacyPackets.Outgoing(response, player.Build);
        if (player.Build.IsLegacy && services.GetService<ILoggerFactory>() is { } logs)
        {
            var log = logs.CreateLogger(typeof(LegacyPacketLog));
            LegacyPacketLog.Note(player, body, outgoing: false, log);
            LegacyPacketLog.Note(player, response, outgoing: true, log);
        }
        return response;
    }

    /// <summary>A login reply as <paramref name="player"/>'s build reads it (and noted in the log for old builds).</summary>
    public static byte[] LoginReply(byte[] reply, OnlinePlayer? player, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(services);
        if (player is not { Build.IsLegacy: true })
            return reply;
        reply = LegacyPackets.Outgoing(reply, player.Build);
        if (services.GetService<ILoggerFactory>() is { } logs)
            LegacyPacketLog.Note(player, reply, outgoing: true, logs.CreateLogger(typeof(LegacyPacketLog)));
        return reply;
    }
}
