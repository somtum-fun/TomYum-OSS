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

        // A session whose queue overflowed (the client stopped reading it) is ended; the client is
        // told to reconnect, and gets everything afresh in its new login.
        if (player.QueueOverflowed)
        {
            var log = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(BanchoExchange))
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            log.LogWarning("Ended user {UserId}'s session: more than {Limit} bytes were waiting for it.", player.UserId, OnlinePlayer.MaxQueuedBytes);
            await SessionEnd.LogOutAsync(player, services, log).ConfigureAwait(false);
            var restart = new BanchoPacketWriter(32).WritePacket(ServerPacketId.Restart, w => w.WriteInt32(0)).ToArray();
            return LegacyPackets.Outgoing(restart, player.Build);
        }

        // Numbered builds before b1807 gzip what they send from 150 bytes on (header byte 2); later ones
        // never do. A request that unpacks to more than any build sends is a gzip bomb: none of it is run.
        var unpacked = LegacyPackets.Decompress(body, player.Build);
        if (unpacked is null)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(BanchoExchange))
                .LogWarning("Refused a request from user {UserId}: its packets unpack to more than {Limit} bytes.", player.UserId, LegacyPackets.MaxUnpackedBytes);
        }
        body = LegacyPackets.Incoming(unpacked ?? [], player.Build);
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

    /// <summary>
    /// A login reply as the build that logged in reads it (and noted in the log for old builds): the
    /// session's build, or for a refused login (no session) the one named in <paramref name="loginBody"/>,
    /// so the refusal is one the build understands (an unknown one reads as success in old builds).
    /// </summary>
    public static byte[] LoginReply(byte[] reply, string loginBody, OnlinePlayer? player, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(services);
        var build = player?.Build ?? ClientBuild.FromLogin(loginBody);
        if (!build.IsLegacy)
            return reply;
        reply = LegacyPackets.Outgoing(reply, build);
        if (player is not null && services.GetService<ILoggerFactory>() is { } logs)
            LegacyPacketLog.Note(player, reply, outgoing: true, logs.CreateLogger(typeof(LegacyPacketLog)));
        return reply;
    }
}
